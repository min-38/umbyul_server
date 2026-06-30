using System.Security.Claims;
using Api.Common;
using Npgsql;

namespace Api.Social;

/// 신고 접수 (NON-23). 로그인 필요. 처리(검토/조치)는 Admin(추후) → 지금은 저장만(status=pending).
/// 같은 유저가 같은 대상 재신고 = idempotent(중복 무시).
public static class ReportEndpoints
{
    public sealed record ReportRequest(string? TargetType, string? TargetId, string? Reason, string? Detail);

    public static void MapReportEndpoints(this WebApplication app, string? dbConnString)
    {
        var me = app.MapGroup("/me").RequireAuthorization();

        me.MapPost("/reports", async (ReportRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } sub || !Guid.TryParse(sub, out var uid))
                return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!ReportValidation.IsTargetType(req.TargetType)) return ApiResults.BadRequest("INVALID_TARGET_TYPE");
            if (string.IsNullOrWhiteSpace(req.TargetId)) return ApiResults.BadRequest("INVALID_TARGET");
            if (!ReportValidation.IsReason(req.Reason)) return ApiResults.BadRequest("INVALID_REASON");
            if (!ReportValidation.IsDetail(req.Detail)) return ApiResults.BadRequest("DETAIL_TOO_LONG");

            var detail = string.IsNullOrWhiteSpace(req.Detail) ? null : req.Detail!.Trim();
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    insert into public.reports (reporter_id, target_type, target_id, reason, detail)
                    values (@uid, @tt, @tid, @reason, @detail)
                    on conflict (reporter_id, target_type, target_id) do nothing
                    """, conn);
                cmd.Parameters.AddWithValue("uid", uid);
                cmd.Parameters.AddWithValue("tt", req.TargetType!);
                cmd.Parameters.AddWithValue("tid", req.TargetId!.Trim());
                cmd.Parameters.AddWithValue("reason", req.Reason!);
                cmd.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK");
            }
            catch (PostgresException ex) when (ex.SqlState == "23503")
            {
                return ApiResults.BadRequest("INVALID_TARGET");
            }
            catch (NpgsqlException)
            {
                return ApiResults.ServiceUnavailable("DB_UNAVAILABLE");
            }
        });
    }
}
