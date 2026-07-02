using System.Text;

namespace Admin;

/// 단순 Basic Auth 게이트. 관리자 암호 하나(ADMIN:PASSWORD)와 비교.
/// 네트워크 격리(로컬/IP 허용목록) 전제라 이 정도 인증으로 충분. 암호 미설정 시 전면 차단.
public sealed class BasicAuthMiddleware(RequestDelegate next, IConfiguration config)
{
    private readonly string? _password = config["ADMIN:PASSWORD"];

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (string.IsNullOrEmpty(_password)) { await Deny(ctx, "ADMIN:PASSWORD not configured"); return; }

        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var pass = raw.Contains(':') ? raw[(raw.IndexOf(':') + 1)..] : raw; // user:pass → pass
                if (FixedEquals(pass, _password)) { await next(ctx); return; }
            }
            catch { /* malformed → deny */ }
        }
        await Deny(ctx, null);
    }

    private static async Task Deny(HttpContext ctx, string? note)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"Glitter Admin\"";
        if (note is not null) await ctx.Response.WriteAsync(note);
    }

    // 타이밍 공격 완화용 상수시간 비교
    private static bool FixedEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
