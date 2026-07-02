using System.Security.Claims;
using Api.Account;
using Api.Auth;
using Api.Common;
using Api.Detail;
using Api.Home;
using Api.Legal;
using Api.Profile;
using Api.Ratings;
using Api.Search;
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
builder.Services.AddSingleton<SpotifyClient>();
builder.Services.AddSingleton<R2Storage>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseCors("web");

app.UseAuthentication();
app.UseAuthorization();

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

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

// 프로필 조회·프로비저닝 (/me/profile, /me/username-available)
app.MapProfileEndpoints(dbConnString);

// 공개 유저 프로필 (/users/{username}) — 유저 + 작성 리뷰 (NON-24)
app.MapPublicProfileEndpoints(dbConnString);

// 통합 검색 (/search) — Spotify(track/album/artist) + users
app.MapSearchEndpoints(dbConnString);

// 앨범/곡 상세 (/detail/track, /detail/album) — Spotify 라이브 + 평점/리뷰 (NON-6)
app.MapDetailEndpoints(dbConnString);

// 아티스트 상세 (/artist/{id}) — 공개, Spotify + 커뮤니티 평가 배지 (NON-13)
app.MapArtistEndpoints(dbConnString);

// 평점·리뷰 등록/수정/삭제 (/me/ratings) — 로그인 (NON-7)
app.MapRatingEndpoints(dbConnString);

// 리뷰 좋아요/싫어요 (/me/reactions), 신고 (/me/reports) — 로그인 (NON-23)
app.MapReactionEndpoints(dbConnString);
app.MapReportEndpoints(dbConnString);

// 리뷰 댓글 (/detail/comments/{id} 공개, /me/comments 작성·삭제) — NON-36
app.MapCommentEndpoints(dbConnString);

// 팔로우 (/me/follows, /users/{username}/followers·following) — NON-25
app.MapFollowEndpoints(dbConnString);

// 알림 (/me/notifications) — NON-26
app.MapNotificationEndpoints(dbConnString);

// 계정 설정 (/me/avatar, /me/username, /me/account, /media/avatar) — NON-30
app.MapAccountEndpoints(dbConnString);

// 홈 피드 (/home) — 공개(옵셔널 인증), DB만 조회 (NON-43)
app.MapHomeEndpoints(dbConnString);

// 약관/개인정보 (/legal/{type}) — 공개 조회 (NON-64)
app.MapLegalEndpoints(dbConnString);

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
