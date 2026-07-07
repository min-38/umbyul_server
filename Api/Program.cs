using System.Security.Claims;
using System.Threading.RateLimiting;
using Api.Account;
using Api.Auth;
using Api.Chart;
using Api.Common;
using Api.Detail;
using Api.Discover;
using Api.Faq;
using Api.Feed;
using Api.Genre;
using Api.Inquiry;
using Api.Legal;
using Api.Profile;
using Api.Ratings;
using Api.Search;
using Api.Sets;
using Api.Social;
using Api.Spotify;
using Api.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Supabase JWT(JWKS) 검증 — 프론트가 받은 액세스 토큰을 게이트웨이에서 검증한다.
var supabase = builder.Configuration.GetSection("SUPABASE");
var supabaseUrl = supabase["URL"]?.TrimEnd('/');
// JWKS URL은 명시값 우선, 없으면 SUPABASE:URL 에서 유도 (별도 시크릿 불필요)
var jwksUrl = supabase["JWKS_URL"];
if (string.IsNullOrEmpty(jwksUrl) && !string.IsNullOrEmpty(supabaseUrl))
    jwksUrl = $"{supabaseUrl}/auth/v1/.well-known/jwks.json";

// DB connection string은 DATABASE 섹션의 개별 항목으로 조합한다 (Supabase direct/pooler 모두 대응).
// 비밀번호는 user-secrets/env(DATABASE:PASSWORD), 나머지(HOST/PORT/USER/DATABASE)는 appsettings 가능.
var db = builder.Configuration.GetSection("DATABASE");
string? dbConnString = null;
if (!string.IsNullOrEmpty(db["HOST"]) && !string.IsNullOrEmpty(db["PASSWORD"]))
{
    dbConnString = new NpgsqlConnectionStringBuilder
    {
        Host = db["HOST"],
        Port = int.TryParse(db["PORT"], out var port) ? port : 5432,
        Database = string.IsNullOrEmpty(db["DATABASE"]) ? "postgres" : db["DATABASE"],
        Username = string.IsNullOrEmpty(db["USER"]) ? "postgres" : db["USER"],
        Password = db["PASSWORD"],
        SslMode = SslMode.Require,
    }.ConnectionString;
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // 클레임 이름을 원본(sub/email) 그대로 사용 — .NET 기본 URI 매핑 비활성화.
        options.MapInboundClaims = false;
        // JWKS 미설정 시 ConfigurationManager 생성이 throw → 모든 요청이 죽으므로 가드.
        // (미설정이면 서명키가 없어 토큰 검증 실패 = 401, 앱은 정상 기동)
        if (!string.IsNullOrEmpty(jwksUrl))
        {
            options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                jwksUrl,
                new JwksConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = true });
        }
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"{supabaseUrl}/auth/v1",
            ValidateAudience = true,
            ValidAudience = "authenticated",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = "sub",
        };
    });

builder.Services.AddAuthorization();

// CORS — 브라우저(web)가 .NET Api 를 직접 호출(온보딩 username 실시간 검사 등). 토큰은 Authorization 헤더.
var webOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:3000"];
builder.Services.AddCors(o => o.AddPolicy("web", p =>
    p.WithOrigins(webOrigins).AllowAnyHeader().AllowAnyMethod()));

// Spotify 카탈로그 클라이언트 (토큰 캐시 공유 위해 싱글톤)
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
// Spotify 응답 캐시 (Postgres) — 재시작·다중 인스턴스에도 유지, 429 완화 (NON-44)
builder.Services.AddSingleton<ISpotifyResponseCache>(
    dbConnString is null ? new NullSpotifyResponseCache() : new PostgresSpotifyResponseCache(dbConnString));
// 오래된 spotify_cache 주기 삭제(LEG-7 / DB-16). DB 미설정이면 미등록.
if (dbConnString is not null)
    builder.Services.AddHostedService(_ => new SpotifyCachePurgeService(dbConnString));
builder.Services.AddSingleton<SpotifyClient>();
builder.Services.AddSingleton<R2Storage>();

// 레이트리밋(NON-96): 쓰기(POST/PUT/DELETE)만 유저(sub)/IP 기준 슬라이딩 윈도우로 제한.
// 조회(GET/HEAD)·이미지 프록시는 무제한(브라우징·아바타 버스트 보호). 초과 시 429 {code:RATE_LIMITED}.
// IP는 UseForwardedHeaders로 신뢰 프록시에서만 복원한 RemoteIpAddress 사용 — 원시 X-Forwarded-For는 스푸핑 가능(SEC-A-4).
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var m = ctx.Request.Method;
        if (HttpMethods.IsGet(m) || HttpMethods.IsHead(m) || HttpMethods.IsOptions(m))
            return RateLimitPartition.GetNoLimiter("read");
        var key = ctx.User.FindFirstValue("sub")
                  ?? ctx.Connection.RemoteIpAddress?.ToString()
                  ?? "anon";
        return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromSeconds(60),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
        });
    });
    // 공개 GET인 /email-available은 전역 리미터(GET 면제) 밖 → 이메일 열거 방지 위해 IP당 별도 제한(SEC-A-5).
    options.AddPolicy("email-check", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.HttpContext.Response.ContentType = "application/json";
        await ctx.HttpContext.Response.WriteAsync("{\"code\":\"RATE_LIMITED\",\"data\":null}", ct);
    };
});

// 신뢰 프록시 뒤에서만 X-Forwarded-For로 실제 클라이언트 IP를 복원(레이트리밋 파티션 스푸핑 방지 — SEC-A-4).
// 프록시 IP는 배포별이라 설정(ForwardedHeaders:KnownProxies)으로 주입. 미설정이면 루프백만 신뢰(안전측: 프록시 뒤에선 과도 제한 → 우회보다 안전).
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                         | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    foreach (var ip in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        if (System.Net.IPAddress.TryParse(ip, out var addr)) o.KnownProxies.Add(addr);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// 신뢰 프록시의 X-Forwarded-* 반영(RemoteIpAddress·scheme) — 레이트리밋 IP 파티션의 신뢰 원천(SEC-A-4).
app.UseForwardedHeaders();

app.UseHttpsRedirection();

app.UseCors("web");

app.UseAuthentication();
app.UseAuthorization();

// 인증 이후에 둬 sub 클레임으로 파티션(로그인 유저는 유저별, 아니면 IP별). (NON-96)
app.UseRateLimiter();

// 보호 엔드포인트: 유효한 Supabase JWT면 user id(sub = auth.uid())와 email 반환, 무효면 401.
app.MapGet("/me", (ClaimsPrincipal user) =>
{
    var userId = user.FindFirstValue("sub");
    var email = user.FindFirstValue("email");
    return ApiResults.Ok("OK", new { userId, email });
})
.RequireAuthorization();

// DB 연결 확인: Supabase Postgres에 SELECT 1. 비밀번호는 user-secrets/env(DATABASE:PASSWORD).
app.MapGet("/health/db", async () =>
{
    if (string.IsNullOrEmpty(dbConnString))
    {
        return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
    }
    try
    {
        await using var conn = new NpgsqlConnection(dbConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        await cmd.ExecuteScalarAsync();
        return ApiResults.Ok("OK");
    }
    catch (Exception ex)
    {
        // 표시용 자연어는 보내지 않되, 개발 환경에선 진단을 위해 detail 만 data 에 싣는다.
        var detail = app.Environment.IsDevelopment() ? ex.Message : null;
        return ApiResults.ServiceUnavailable("DB_UNAVAILABLE", detail);
    }
});

// 프로필 조회·프로비저닝 (/me/profile, /me/username-available)
app.MapProfileEndpoints(dbConnString);

// 공개 유저 프로필 (/users/{username}) — 유저 + 작성 리뷰 (NON-24)
app.MapPublicProfileEndpoints(dbConnString);

// 통합 검색 (/search) — Spotify(track/album/artist) + users
app.MapSearchEndpoints(dbConnString);

// 앨범/곡 상세 (/detail/track, /detail/album) — Spotify 라이브 + 평점/리뷰 (NON-6)
app.MapDetailEndpoints(dbConnString);

// 평점 시세 (/detail/rating-history) — 일별 누적 평균 시계열 (NON-124)
app.MapRatingHistoryEndpoints(dbConnString);

// 유저 장르 태깅 (/genres, /genres/for, /me/genre-tags) — 커뮤니티 큐레이트 (NON-122)
app.MapGenreEndpoints(dbConnString);

// 아티스트 상세 (/artist/{id}) — 공개, Spotify + 커뮤니티 평가 배지 (NON-13)
app.MapArtistEndpoints(dbConnString);

// 평점·리뷰 등록/수정/삭제 (/me/ratings) — 로그인 (NON-7)
app.MapRatingEndpoints(dbConnString);

// 리뷰 좋아요/싫어요 (/me/reactions), 신고 (/me/reports) — 로그인 (NON-23)
app.MapReactionEndpoints(dbConnString);
app.MapReportEndpoints(dbConnString);

// 리뷰 댓글 (/detail/comments/{id} 공개, /me/comments 작성·삭제) — NON-36
app.MapCommentEndpoints(dbConnString);

// 댓글 @멘션 (/users/search 자동완성, /me/mention-mute 뮤트) — NON-131
app.MapMentionEndpoints(dbConnString);

// DJ 세트 (/sets, /me/sets, 스포티파이 플리 임포트) — NON-133
app.MapSetEndpoints(dbConnString);

// 팔로우 (/me/follows, /users/{username}/followers·following) — NON-25
app.MapFollowEndpoints(dbConnString);

// 유저 차단 (/me/blocks) — NON-115
app.MapBlockEndpoints(dbConnString);

// 알림 (/me/notifications) — NON-26
app.MapNotificationEndpoints(dbConnString);

// 계정 설정 (/me/avatar, /me/username, /me/account, /media/avatar) — NON-30
app.MapAccountEndpoints(dbConnString);

// 홈 피드 (/feed) — 공개(옵셔널 인증), Reddit식 정렬 + 반응 집계 (NON-88)
app.MapFeedEndpoints(dbConnString);

// Discover (/discover) — 공개, DB만 조회: 신규·급상승 (NON-81)
app.MapDiscoverEndpoints(dbConnString);

// Chart (/chart) — 공개, DB 집계 랭킹(최다/최고·베이지안/호불호) (NON-82)
app.MapChartEndpoints(dbConnString);

// 약관/개인정보 (/legal/{type}) — 공개 조회 (NON-64)
app.MapLegalEndpoints(dbConnString);

// FAQ (/faq) — 공개 조회 (NON-73)
app.MapFaqEndpoints(dbConnString);

// 문의 (/inquiries) — 공개 접수 (NON-76)
app.MapInquiryEndpoints(dbConnString);

app.Run();
