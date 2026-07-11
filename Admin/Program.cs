using System.Security.Claims;
using System.Threading.RateLimiting;
using Admin.Components;
using Admin.Data;
using Admin.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var SessionDuration = TimeSpan.FromHours(1); // 관리자 세션 유효기간(고정).
// 로그인 타이밍 균등화용 더미 해시(유저 부재/비활성 시에도 동일 비용 검증 → 유저 열거 방지, ADM-3).
var DummyHash = BCrypt.Net.BCrypt.HashPassword("timing-equalizer");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<AdminDb>();
builder.Services.AddScoped<SessionGuard>(); // 서킷당 진행 중 작업 추적(NON-53)
builder.Services.AddHttpClient(); // 공지 이미지 업로드 → Api(R2) 프록시 호출(NON-168)
// 업로드 전용 named client — 기본 100s 대신 15s로 제한해 Api/R2 행 시 빨리 에러 배너로 전환(NON-222).
builder.Services.AddHttpClient("upload", c => c.Timeout = TimeSpan.FromSeconds(15));

// 관리자 계정(별도 admins 테이블) + 쿠키 세션.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/login";
        o.ExpireTimeSpan = SessionDuration;
        o.SlidingExpiration = false; // 고정 만료 → 헤더 잔여시간 카운트다운을 정확히.
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always; // 항상 Secure 쿠키(프록시 뒤 HTTP 로 발급되는 것 방지, ADM-14).
        // 비활성화된 관리자의 라이브 세션도 다음 요청에 무효화(NON-103 우회 차단, ADM-1).
        // Blazor 서버 서킷 내 SignalR 메시지엔 안 걸리나, full navigation·신규 서킷·연장에서 재검증.
        o.Events.OnValidatePrincipal = async context =>
        {
            if (Guid.TryParse(context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var adminId))
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<AdminDb>();
                if (!await db.IsAdminActiveAsync(adminId, context.HttpContext.RequestAborted))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// 로그인 무차별 대입 방어(ADM-3): IP당 분당 5회. 초과 시 429.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsync("너무 많은 로그인 시도입니다. 잠시 후 다시 시도하세요.", ct);
    };
});

// 세션 쿠키 암호화 키 지속화(ADM-14) — 미설정 시 재시작마다 키 소실 → 전 세션 무효화(운영 불편).
// 컨테이너/다중 인스턴스면 DataProtection:KeyPath 에 공유 볼륨 경로 지정.
var keyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrEmpty(keyPath))
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyPath));

// 신뢰 프록시의 X-Forwarded-* 반영(RemoteIpAddress·scheme) — Secure 쿠키·로그인 레이트리밋 IP 정확도(ADM-14).
// 프록시 IP 는 배포별이라 설정(ForwardedHeaders:KnownProxies)으로 주입. 미설정이면 루프백만 신뢰.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                         | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    foreach (var ip in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        if (System.Net.IPAddress.TryParse(ip, out var addr)) o.KnownProxies.Add(addr);
});

var app = builder.Build();

// 부트스트랩: ADMIN:BOOTSTRAP_* 설정 시 첫 관리자 생성(없을 때만).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AdminDb>();
    var bu = app.Configuration["ADMIN:BOOTSTRAP_USERNAME"];
    var bp = app.Configuration["ADMIN:BOOTSTRAP_PASSWORD"];
    if (!string.IsNullOrEmpty(bu) && !string.IsNullOrEmpty(bp))
        await db.EnsureBootstrapAdminAsync(bu, BCrypt.Net.BCrypt.HashPassword(bp));
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseForwardedHeaders(); // 신뢰 프록시 X-Forwarded-* 반영(ADM-14) — HTTPS 리다이렉트·쿠키 이전에.
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

// 로그인/로그아웃은 minimal endpoint(Blazor 컴포넌트는 응답 시작 후라 쿠키 세팅 불가).
app.MapPost("/auth/login", async (HttpContext ctx, AdminDb db, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    // 로그인 폼의 AntiforgeryToken 검증(로그인-CSRF 차단, ADM-8). 수동 폼이라 미들웨어 대신 직접 검증.
    try { await antiforgery.ValidateRequestAsync(ctx); }
    catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException) { return Results.Redirect("/login?error=1"); }
    var form = await ctx.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    // 인증 조회 순단은 /Error(예외 핸들러) 대신 로그인 폼 에러로(NON-222).
    (Guid Id, string Username, string Hash, bool IsActive)? admin;
    try { admin = await db.GetAdminAuthAsync(username); }
    catch (NpgsqlException) { return Results.Redirect("/login?error=1"); }
    if (admin is { } a && a.IsActive && BCrypt.Net.BCrypt.Verify(password, a.Hash)) // 비활성 관리자는 로그인 차단(NON-103)
    {
        await SignInAdminAsync(ctx, a.Id, a.Username, SessionDuration);
        // 감사 로그는 best-effort — SignIn 후 throw하면 예외 핸들러가 Set-Cookie를 지워 로그인이 무산되므로(NON-222).
        try { await db.LogAsync(new Actor(a.Id, a.Username), "login", null, null); } catch (NpgsqlException) { }
        return Results.Redirect("/");
    }
    // 유저 부재/비활성 시에도 동일 비용의 해시 검증 → 응답시간 기반 유저 열거 방지(ADM-3).
    if (admin is not { IsActive: true }) BCrypt.Net.BCrypt.Verify(password, DummyHash);
    try
    {
        // IP는 원본 대신 솔트 해시로 저장 — 코드베이스 유일했던 raw IP 영속화 제거(SEC-A-4 일관, QA9-2).
        // 브루트포스 상관관계(같은 IP 반복)엔 충분. 90일 후 RetentionPurgeService가 파기.
        await db.LogAsync(new Actor(null, string.IsNullOrEmpty(username) ? "?" : username), "login.failed",
            null, HashLoginIp(ctx.Connection.RemoteIpAddress?.ToString()));
    }
    catch (NpgsqlException) { /* 감사 실패가 로그인 폼 응답을 막지 않음 */ }
    return Results.Redirect("/login?error=1");
}).DisableAntiforgery().RequireRateLimiting("login"); // 미들웨어 자동검증은 끄고 위에서 수동 검증(ADM-8)

// 세션 연장: 인증 상태면 새 만료(1h)로 쿠키 재발급 후 원래 페이지로(NON-53).
app.MapPost("/auth/refresh", async (HttpContext ctx, AdminDb db) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Redirect("/login");
    Guid.TryParse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id);
    // 비활성화된 관리자는 연장 거부(DB 재확인 없이 무한 연장되던 우회 차단, ADM-1).
    if (!await db.IsAdminActiveAsync(id))
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    }
    await SignInAdminAsync(ctx, id, ctx.User.Identity.Name ?? "", SessionDuration);
    // 감사 로그 best-effort — 쿠키 재발급 후 throw 시 헤더 소실로 연장이 무산되는 것 방지(NON-222).
    try { await db.LogAsync(new Actor(id, ctx.User.Identity.Name ?? "?"), "session.refresh", null, null); } catch (NpgsqlException) { }
    var back = ctx.Request.Headers.Referer.ToString();
    return Results.Redirect(string.IsNullOrEmpty(back) ? "/" : back);
}).RequireAuthorization().DisableAntiforgery();

// GET+POST 모두 허용: 버튼은 form POST, 세션 만료 자동 로그아웃은 full navigation(GET)이라(NON-67).
app.MapMethods("/auth/logout", ["GET", "POST"], async (HttpContext ctx, AdminDb db) =>
{
    // GET 은 세션 만료 자동 로그아웃 전용(NON-67) — 유효 세션에 대한 cross-site GET 강제 로그아웃(CSRF) 방지 위해
    // 실제 만료(session_exp 경과)일 때만 처리. 버튼(POST)은 항상 처리(NON-260).
    if (HttpMethods.IsGet(ctx.Request.Method) && !SessionExpired(ctx.User))
        return Results.Redirect("/");
    if (ctx.User.Identity?.IsAuthenticated == true)
    {
        Guid.TryParse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id);
        // 감사 로그 best-effort — DB 순단이 로그아웃(쿠키 삭제)을 막지 않게, 공용 PC 세션 종료 보장(NON-222).
        try { await db.LogAsync(new Actor(id, ctx.User.Identity.Name ?? "?"), "logout", null, null); } catch (NpgsqlException) { }
    }
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// 세션(app-level 고정 만료) 경과 여부 — GET 자동 로그아웃 게이트(NON-260). 클레임 없으면 만료 아님(강제 로그아웃 안 함).
static bool SessionExpired(System.Security.Claims.ClaimsPrincipal user) =>
    long.TryParse(user.FindFirst("session_exp")?.Value, out var unix)
    && DateTimeOffset.FromUnixTimeSeconds(unix) <= DateTimeOffset.UtcNow;

// 로그인 실패 로그의 IP 솔트 해시(QA9-2) — 원본 IP 미저장. null/빈 IP는 "unknown". 16 hex 결정적(같은 IP 상관관계 유지).
static string HashLoginIp(string? ip) =>
    string.IsNullOrEmpty(ip)
        ? "unknown"
        : System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("glitter-adm-login-v1" + ip)))[..16];

// 관리자 쿠키 재발급(로그인·연장 공용): 고정 만료 + session_exp claim.
static async Task SignInAdminAsync(HttpContext ctx, Guid id, string username, TimeSpan duration)
{
    var expiresAt = DateTimeOffset.UtcNow.Add(duration);
    var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, id.ToString()),
            new Claim(ClaimTypes.Name, username),
            new Claim("session_exp", expiresAt.ToUnixTimeSeconds().ToString()),
        ],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new AuthenticationProperties { ExpiresUtc = expiresAt });
}
