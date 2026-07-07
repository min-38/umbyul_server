using Npgsql;

namespace Admin.Data;

// AdminDb 도메인 분리(NON-110): 신고 처리 + 리뷰 모더레이션(신고 없이 직접). 코어의 partial.
public sealed partial class AdminDb
{
    // ── 신고 ──
    public async Task<List<ReportRow>> GetReportsAsync(string? status, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        // 5개 대상 타입(rating/user/comment/set_comment/set)을 각 테이블로 조인해 제목·본문·작성자(제재 대상) 해석.
        // target_id 는 0052 CHECK 로 uuid 보장 → ::uuid 캐스트 안전.
        await using var cmd = new NpgsqlCommand(
            """
            select rep.id, ru.username, rep.target_type, rep.target_id, rep.reason, rep.detail, rep.status, rep.created_at,
                   rat.id, rat.target_name, rat.target_artist, rat.review, rau.username, tu.username, rat.deleted_at, rat.user_id,
                   rc.id, rc.body, rc.deleted_at, rcu.username, rc.user_id,
                   sc.id, sc.body, sc.deleted_at, scu.username, sc.user_id,
                   st.id, st.title, st.note, st.deleted_at, stu.username, st.owner_id
            from public.reports rep
            join public.users ru on ru.id = rep.reporter_id
            left join public.ratings rat on rep.target_type = 'rating' and rat.id = rep.target_id::uuid
            left join public.users rau on rau.id = rat.user_id
            left join public.users tu on rep.target_type = 'user' and tu.id = rep.target_id::uuid
            left join public.review_comments rc on rep.target_type = 'comment' and rc.id = rep.target_id::uuid
            left join public.users rcu on rcu.id = rc.user_id
            left join public.set_comments sc on rep.target_type = 'set_comment' and sc.id = rep.target_id::uuid
            left join public.users scu on scu.id = sc.user_id
            left join public.sets st on rep.target_type = 'set' and st.id = rep.target_id::uuid
            left join public.users stu on stu.id = st.owner_id
            where (@status = '' or rep.status = @status)
            order by rep.created_at desc
            limit @lim offset @off
            """, conn);
        cmd.Parameters.AddWithValue("status", status ?? "");
        cmd.Parameters.AddWithValue("lim", limit);
        cmd.Parameters.AddWithValue("off", offset);

        var list = new List<ReportRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var targetType = r.GetString(2);
            string? title = null, sub = null, body = null;
            var targetDeleted = false;
            Guid? offenderId = null; // 제재 대상(콘텐츠 작성자 또는 신고된 유저)
            string? offenderName = null;
            switch (targetType)
            {
                case "rating" when !r.IsDBNull(8):
                    title = r.IsDBNull(9) ? "(대상 음악 미상)" : r.GetString(9);
                    var artist = r.IsDBNull(10) ? null : r.GetString(10);
                    offenderName = r.IsDBNull(12) ? null : r.GetString(12);
                    sub = string.Join(" · ", new[] { artist, offenderName is null ? null : $"by {offenderName}" }.Where(x => x is not null));
                    body = r.IsDBNull(11) ? null : r.GetString(11);
                    targetDeleted = !r.IsDBNull(14);
                    offenderId = r.IsDBNull(15) ? null : r.GetGuid(15);
                    break;
                case "rating":
                    title = "(삭제된 리뷰)";
                    break;
                case "comment" when !r.IsDBNull(16):
                    title = "댓글";
                    offenderName = r.IsDBNull(19) ? null : r.GetString(19);
                    sub = offenderName is null ? null : $"by {offenderName}";
                    body = r.IsDBNull(17) ? null : r.GetString(17);
                    targetDeleted = !r.IsDBNull(18);
                    offenderId = r.IsDBNull(20) ? null : r.GetGuid(20);
                    break;
                case "comment":
                    title = "(삭제된 댓글)";
                    break;
                case "set_comment" when !r.IsDBNull(21):
                    title = "믹스 댓글";
                    offenderName = r.IsDBNull(24) ? null : r.GetString(24);
                    sub = offenderName is null ? null : $"by {offenderName}";
                    body = r.IsDBNull(22) ? null : r.GetString(22);
                    targetDeleted = !r.IsDBNull(23);
                    offenderId = r.IsDBNull(25) ? null : r.GetGuid(25);
                    break;
                case "set_comment":
                    title = "(삭제된 믹스 댓글)";
                    break;
                case "set" when !r.IsDBNull(26):
                    title = r.IsDBNull(27) ? "(제목 없는 믹스)" : r.GetString(27);
                    offenderName = r.IsDBNull(30) ? null : r.GetString(30);
                    sub = offenderName is null ? null : $"by {offenderName}";
                    body = r.IsDBNull(28) ? null : r.GetString(28);
                    targetDeleted = !r.IsDBNull(29);
                    offenderId = r.IsDBNull(31) ? null : r.GetGuid(31);
                    break;
                case "set":
                    title = "(삭제된 믹스)";
                    break;
                default: // user
                    offenderName = r.IsDBNull(13) ? null : r.GetString(13);
                    title = offenderName is null ? "(알 수 없는 유저)" : $"@{offenderName}";
                    offenderId = Guid.TryParse(r.GetString(3), out var ug) ? ug : null;
                    break;
            }
            list.Add(new ReportRow(
                r.GetGuid(0), r.GetString(1), targetType, r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7),
                title, sub, body, targetDeleted, offenderId, offenderName));
        }
        return list;
    }

    public async Task SetReportStatusAsync(Guid id, string status, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand("update public.reports set status = @s where id = @id", conn))
        {
            cmd.Parameters.AddWithValue("s", status);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, $"report.{status}", id.ToString(), null, ct);
    }

    // 신고된 콘텐츠 소프트삭제 + 신고 resolved + 감사로그(단일 트랜잭션, ADM-12). 이미 삭제된 건 no-op.
    // 모든 대상 테이블(ratings/review_comments/set_comments/sets)이 deleted_at/by/reason 컬럼을 가짐(0055).
    // table·action 은 코드 리터럴만 전달(사용자 입력 아님).
    public Task DeleteRatingAndResolveAsync(Guid reportId, string ratingId, Actor actor, CancellationToken ct = default)
        => SoftDeleteTargetAndResolveAsync(reportId, ratingId, "ratings", "rating.delete", actor, ct);
    public Task DeleteCommentAndResolveAsync(Guid reportId, string commentId, Actor actor, CancellationToken ct = default)
        => SoftDeleteTargetAndResolveAsync(reportId, commentId, "review_comments", "comment.delete", actor, ct);
    public Task DeleteSetCommentAndResolveAsync(Guid reportId, string commentId, Actor actor, CancellationToken ct = default)
        => SoftDeleteTargetAndResolveAsync(reportId, commentId, "set_comments", "set_comment.delete", actor, ct);
    public Task DeleteSetAndResolveAsync(Guid reportId, string setId, Actor actor, CancellationToken ct = default)
        => SoftDeleteTargetAndResolveAsync(reportId, setId, "sets", "set.delete", actor, ct);

    private async Task SoftDeleteTargetAndResolveAsync(Guid reportId, string targetId, string table, string action, Actor actor, CancellationToken ct)
    {
        if (!Configured || !Guid.TryParse(targetId, out var tid)) return;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var del = new NpgsqlCommand(
            $"update public.{table} set deleted_at = now(), deleted_by = @by, deleted_reason = @reason where id = @tid and deleted_at is null", conn, tx))
        {
            del.Parameters.AddWithValue("tid", tid);
            del.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            del.Parameters.AddWithValue("reason", $"report:{reportId}");
            await del.ExecuteNonQueryAsync(ct);
        }
        await using (var upd = new NpgsqlCommand("update public.reports set status = 'resolved' where id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("id", reportId);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, action, targetId, $"report {reportId}", ct, tx);
        await tx.CommitAsync(ct);
    }

    // ── 리뷰 모더레이션(신고 없이 직접) NON-98 ──

    /// 리뷰(review 있는 평가) 목록. search 는 유저명·리뷰 본문 부분일치. 삭제된 것은 제외. 페이지네이션(ADM-7).
    public async Task<List<ReviewRow>> ListReviewsAsync(string? search, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select r.id, u.id, u.username, r.target_type, r.target_name, r.target_artist, r.target_spotify_id,
                   r.score, r.review, r.created_at
            from public.ratings r
            join public.users u on u.id = r.user_id
            where r.review is not null and length(trim(r.review)) > 0 and r.deleted_at is null
              and (@q = '' or u.username ilike '%' || @q || '%' or r.review ilike '%' || @q || '%')
            order by r.created_at desc
            limit @lim offset @off
            """, conn);
        cmd.Parameters.AddWithValue("q", LikeEscape(search ?? ""));
        cmd.Parameters.AddWithValue("lim", limit);
        cmd.Parameters.AddWithValue("off", offset);
        var list = new List<ReviewRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new ReviewRow(
                rd.GetGuid(0), rd.GetGuid(1), rd.GetString(2), rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5),
                rd.IsDBNull(6) ? null : rd.GetString(6), rd.GetDecimal(7), rd.GetString(8),
                rd.GetFieldValue<DateTimeOffset>(9)));
        return list;
    }

    /// 신고 없이 리뷰를 소프트삭제(사유 기록). 감사 로그 남김.
    public async Task SoftDeleteRatingAsync(Guid ratingId, string? reason, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using var del = new NpgsqlCommand(
            """
            update public.ratings
            set deleted_at = now(), deleted_by = @by, deleted_reason = @reason
            where id = @rid and deleted_at is null
            """, conn);
        del.Parameters.AddWithValue("rid", ratingId);
        del.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
        del.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        await del.ExecuteNonQueryAsync(ct);
        await LogAsync(conn, actor, "rating.delete", ratingId.ToString(), "moderation", ct);
    }

    // ── 믹스(DJ 세트) 모더레이션 (NON-141) ──

    /// 믹스 목록(신고 없이 직접). search 는 제목·소유자명 부분일치. 소프트삭제된 것은 제외. 페이지네이션.
    public async Task<List<SetRow>> ListSetsAsync(string? search, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select s.id, s.owner_id, u.username, s.title, s.note,
                   (select count(*) from public.set_tracks t where t.set_id = s.id)::int, s.created_at
            from public.sets s
            join public.users u on u.id = s.owner_id
            where s.deleted_at is null
              and (@q = '' or s.title ilike '%' || @q || '%' or u.username ilike '%' || @q || '%')
            order by s.created_at desc
            limit @lim offset @off
            """, conn);
        cmd.Parameters.AddWithValue("q", LikeEscape(search ?? ""));
        cmd.Parameters.AddWithValue("lim", limit);
        cmd.Parameters.AddWithValue("off", offset);
        var list = new List<SetRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new SetRow(
                rd.GetGuid(0), rd.GetGuid(1), rd.GetString(2), rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4), rd.GetInt32(5), rd.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    /// 신고 없이 믹스를 소프트삭제(사유 기록). 공개 목록/상세에서 즉시 숨겨짐(API deleted_at 필터). 감사 로그.
    public async Task SoftDeleteSetAsync(Guid setId, string? reason, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using var del = new NpgsqlCommand(
            "update public.sets set deleted_at = now(), deleted_by = @by, deleted_reason = @reason where id = @id and deleted_at is null", conn);
        del.Parameters.AddWithValue("id", setId);
        del.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
        del.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        await del.ExecuteNonQueryAsync(ct);
        await LogAsync(conn, actor, "set.delete", setId.ToString(), "moderation", ct);
    }
}
