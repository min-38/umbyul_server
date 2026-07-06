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
            // 수신자 알림 설정 확인 후 적재. 설정 row 없으면 기본 on → 적재.
            // master=false 이거나 해당 종류 off 면 적재하지 않음.
            await using var cmd = new NpgsqlCommand(
                """
                insert into public.notifications (recipient_id, actor_id, type, target_id)
                select @r, @a, @t, @tid
                where not exists (
                    select 1 from public.notification_prefs p
                    where p.user_id = @r and (
                        p.master = false
                        or (@t = 'follow' and p.follow = false)
                        or (@t = 'review_like' and p.review_like = false)
                    )
                )
                """, conn);
            cmd.Parameters.AddWithValue("r", recipientId);
            cmd.Parameters.AddWithValue("a", actorId);
            cmd.Parameters.AddWithValue("t", type);
            cmd.Parameters.AddWithValue("tid", (object?)targetId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException) { /* 알림 실패 무시 */ }
    }

    /// 댓글 @멘션 알림 (NON-131). master·mention 설정 off 이거나 그 대상(트랙/앨범) 멘션 뮤트면 적재 안 함.
    public static async Task CreateMentionAsync(
        NpgsqlConnection conn, Guid recipientId, Guid actorId, string ratingId,
        string targetType, string targetSpotifyId)
    {
        if (recipientId == actorId) return;
        try
        {
            await using var cmd = new NpgsqlCommand(
                """
                insert into public.notifications (recipient_id, actor_id, type, target_id)
                select @r, @a, 'mention', @tid
                where not exists (
                    select 1 from public.notification_prefs p
                    where p.user_id = @r and (p.master = false or p.mention = false)
                )
                and not exists (
                    select 1 from public.mention_mutes m
                    where m.user_id = @r and m.target_type = @tt and m.target_spotify_id = @tsid
                )
                """, conn);
            cmd.Parameters.AddWithValue("r", recipientId);
            cmd.Parameters.AddWithValue("a", actorId);
            cmd.Parameters.AddWithValue("tid", ratingId);
            cmd.Parameters.AddWithValue("tt", targetType);
            cmd.Parameters.AddWithValue("tsid", targetSpotifyId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException) { /* 알림 실패 무시 */ }
    }
}
