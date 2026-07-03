using System.Security.Claims;
using Admin.Components;
using Admin.Data;
using Admin.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

var SessionDuration = TimeSpan.FromHours(1); // 관리자 세션 유효기간(고정).

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
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

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
    return Results.Redirect("/login?error=1");
}).DisableAntiforgery();

// 세션 연장: 인증 상태면 새 만료(1h)로 쿠키 재발급 후 원래 페이지로(NON-53).
app.MapPost("/auth/refresh", async (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Redirect("/login");
    Guid.TryParse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id);
    await SignInAdminAsync(ctx, id, ctx.User.Identity.Name ?? "", SessionDuration);
    var back = ctx.Request.Headers.Referer.ToString();
    return Results.Redirect(string.IsNullOrEmpty(back) ? "/" : back);
}).RequireAuthorization().DisableAntiforgery();

// GET+POST 모두 허용: 버튼은 form POST, 세션 만료 자동 로그아웃은 full navigation(GET)이라(NON-67).
app.MapMethods("/auth/logout", ["GET", "POST"], async (HttpContext ctx) =>
{
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
