using Npgsql;

namespace Api.Social;

/// 알림 적재 헬퍼 (NON-26). 팔로우/좋아요 등 발생 지점에서 호출.
/// 실패는 삼킨다(알림 실패가 주 동작을 막지 않도록). 자기 자신에겐 알림 생성 안 함.
public static class Notifications
{
    public static async Task CreateAsync(
        NpgsqlConnection conn, Guid recipientId, Guid actorId, string type, string? targetId)
    {
        if (recipientId == actorId) return;
        try
        {
            await using var cmd = new NpgsqlCommand(
                "insert into public.notifications (recipient_id, actor_id, type, target_id) values (@r, @a, @t, @tid)",
                conn);
            cmd.Parameters.AddWithValue("r", recipientId);
            cmd.Parameters.AddWithValue("a", actorId);
            cmd.Parameters.AddWithValue("t", type);
            cmd.Parameters.AddWithValue("tid", (object?)targetId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException) { /* 알림 실패 무시 */ }
    }
}
