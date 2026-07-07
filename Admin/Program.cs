using System.Security.Claims;
using System.Threading.RateLimiting;
using Admin.Components;
using Admin.Data;
using Admin.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

var SessionDuration = TimeSpan.FromHours(1); // 관리자 세션 유효기간(고정).
// 로그인 타이밍 균등화용 더미 해시(유저 부재/비활성 시에도 동일 비용 검증 → 유저 열거 방지, ADM-3).
var DummyHash = BCrypt.Net.BCrypt.HashPassword("timing-equalizer");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<AdminDb>();
builder.Services.AddScoped<SessionGuard>(); // 서킷당 진행 중 작업 추적(NON-53)

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
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

// 로그인/로그아웃은 minimal endpoint(Blazor 컴포넌트는 응답 시작 후라 쿠키 세팅 불가).
app.MapPost("/auth/login", async (HttpContext ctx, AdminDb db) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var admin = await db.GetAdminAuthAsync(username);
    if (admin is { } a && a.IsActive && BCrypt.Net.BCrypt.Verify(password, a.Hash)) // 비활성 관리자는 로그인 차단(NON-103)
    {
        await SignInAdminAsync(ctx, a.Id, a.Username, SessionDuration);
        await db.LogAsync(new Actor(a.Id, a.Username), "login", null, null);
        return Results.Redirect("/");
    }
    // 유저 부재/비활성 시에도 동일 비용의 해시 검증 → 응답시간 기반 유저 열거 방지(ADM-3).
    if (admin is not { IsActive: true }) BCrypt.Net.BCrypt.Verify(password, DummyHash);
    await db.LogAsync(new Actor(null, string.IsNullOrEmpty(username) ? "?" : username), "login.failed",
        null, ctx.Connection.RemoteIpAddress?.ToString());
    return Results.Redirect("/login?error=1");
}).DisableAntiforgery().RequireRateLimiting("login");

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
    await db.LogAsync(new Actor(id, ctx.User.Identity.Name ?? "?"), "session.refresh", null, null);
    var back = ctx.Request.Headers.Referer.ToString();
    return Results.Redirect(string.IsNullOrEmpty(back) ? "/" : back);
}).RequireAuthorization().DisableAntiforgery();

// GET+POST 모두 허용: 버튼은 form POST, 세션 만료 자동 로그아웃은 full navigation(GET)이라(NON-67).
app.MapMethods("/auth/logout", ["GET", "POST"], async (HttpContext ctx, AdminDb db) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true)
    {
        Guid.TryParse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id);
        await db.LogAsync(new Actor(id, ctx.User.Identity.Name ?? "?"), "logout", null, null);
    }
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

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
