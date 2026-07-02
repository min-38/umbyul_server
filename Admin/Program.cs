using System.Security.Claims;
using Admin.Components;
using Admin.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

var SessionDuration = TimeSpan.FromHours(1); // 관리자 세션 유효기간(고정).

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<AdminDb>();

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
    if (admin is { } a && BCrypt.Net.BCrypt.Verify(password, a.Hash))
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(SessionDuration);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, a.Id.ToString()),
                new Claim(ClaimTypes.Name, a.Username),
                new Claim("session_exp", expiresAt.ToUnixTimeSeconds().ToString()), // 헤더 잔여시간·자동 로그아웃용
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = expiresAt });
        await db.LogAsync(new Actor(a.Id, a.Username), "login", null, null);
        return Results.Redirect("/");
    }
    return Results.Redirect("/login?error=1");
}).DisableAntiforgery();

app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
