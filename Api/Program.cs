using System.Security.Claims;
using Api.Auth;
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
var jwksUrl = supabase["JWKS_URL"];

// DB connection string은 URL의 프로젝트 ref + 비밀번호(시크릿)로 조합한다 (Direct connection).
var dbPassword = supabase["DB_PASSWORD"];
string? dbConnString = null;
if (!string.IsNullOrEmpty(supabaseUrl) && !string.IsNullOrEmpty(dbPassword))
{
    var projectRef = new Uri(supabaseUrl).Host.Split('.')[0];
    dbConnString = new NpgsqlConnectionStringBuilder
    {
        Host = $"db.{projectRef}.supabase.co",
        Port = 5432,
        Database = "postgres",
        Username = "postgres",
        Password = dbPassword,
        SslMode = SslMode.Require,
    }.ConnectionString;
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // 클레임 이름을 원본(sub/email) 그대로 사용 — .NET 기본 URI 매핑 비활성화.
        options.MapInboundClaims = false;
        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            jwksUrl ?? string.Empty,
            new JwksConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

// 보호 엔드포인트: 유효한 Supabase JWT면 user id(sub = auth.uid())와 email 반환, 무효면 401.
app.MapGet("/me", (ClaimsPrincipal user) =>
{
    var userId = user.FindFirstValue("sub");
    var email = user.FindFirstValue("email");
    return Results.Ok(new { userId, email });
})
.RequireAuthorization();

// DB 연결 확인: Supabase Postgres에 SELECT 1. 비밀번호는 user-secrets/env(SUPABASE:DB_PASSWORD).
app.MapGet("/health/db", async () =>
{
    if (string.IsNullOrEmpty(dbConnString))
    {
        return Results.Problem("SUPABASE:URL/DB_PASSWORD not configured", statusCode: 503);
    }
    try
    {
        await using var conn = new NpgsqlConnection(dbConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        await cmd.ExecuteScalarAsync();
        return Results.Ok(new { db = "ok" });
    }
    catch (NpgsqlException)
    {
        return Results.Problem("database unreachable", statusCode: 503);
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

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
