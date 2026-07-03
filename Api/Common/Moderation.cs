using Npgsql;

namespace Api.Common;

/// 유저 제재 집행(NON-48). 쓰기 시점에 banned / suspended_until 을 확인해 차단.
/// 컬럼 미존재(마이그레이션 0018 전)나 조회 실패 시엔 null(통과) — fail-open.
public static class Moderation
{
    public static async Task<SanctionBlock?> CheckAsync(NpgsqlConnection conn, Guid userId, CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(
                "select banned, suspended_until from public.users where id = @uid", conn);
            cmd.Parameters.AddWithValue("uid", userId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            var banned = !r.IsDBNull(0) && r.GetBoolean(0);
            var until = r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(1);
            return Evaluate(banned, until, DateTimeOffset.UtcNow);
        }
        catch (NpgsqlException) { return null; } // 컬럼 미존재 등 — 통과
    }

    /// 제재 판정(순수 함수라 단위 테스트 가능). 영구정지 우선, 다음 정지(until > now)면 정지, 아니면 통과.
    /// 경계: until == now 는 만료(통과)로 본다.
    public static SanctionBlock? Evaluate(bool banned, DateTimeOffset? until, DateTimeOffset now)
    {
        if (banned) return new SanctionBlock(true, null);
        if (until is { } u && u > now) return new SanctionBlock(false, u);
        return null;
    }

    /// 차단 시 표준 403 응답(정지: until 포함 / 영구정지).
    public static IResult ToResult(SanctionBlock block) =>
        block.Banned
            ? ApiResults.Forbidden("ACCOUNT_BANNED")
            : ApiResults.Forbidden("ACCOUNT_SUSPENDED", new { until = block.Until });
}

/// null = 제재 없음. Banned=true 영구정지, 아니면 Until 까지 정지.
public sealed record SanctionBlock(bool Banned, DateTimeOffset? Until);
