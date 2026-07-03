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
        await using var cmd = new NpgsqlCommand(
            """
            select rep.id, ru.username, rep.target_type, rep.target_id, rep.reason, rep.detail, rep.status, rep.created_at,
                   rat.id, rat.target_name, rat.target_artist, rat.review, rau.username, tu.username, rat.deleted_at, rat.user_id
            from public.reports rep
            join public.users ru on ru.id = rep.reporter_id
            left join public.ratings rat on rep.target_type = 'rating' and rat.id = rep.target_id::uuid
            left join public.users rau on rau.id = rat.user_id
            left join public.users tu on rep.target_type = 'user' and tu.id = rep.target_id::uuid
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
            string? title, sub, body;
            var targetDeleted = false;
            Guid? offenderId = null; // 제재 대상(리뷰 작성자 또는 신고된 유저)
            string? offenderName = null;
            if (targetType == "rating")
            {
                var ratingExists = !r.IsDBNull(8); // rat.id — 존재 여부를 이름 유무와 구분
                if (!ratingExists)
                {
                    title = "(삭제된 리뷰)";
                    sub = null;
                    body = null;
                }
                else
                {
                    title = r.IsDBNull(9) ? "(대상 음악 미상)" : r.GetString(9); // target_name(캐시). 옛 평점은 null 가능
                    var artist = r.IsDBNull(10) ? null : r.GetString(10);
                    var author = r.IsDBNull(12) ? null : r.GetString(12);
                    sub = string.Join(" · ", new[] { artist, author is null ? null : $"by {author}" }.Where(x => x is not null));
                    body = r.IsDBNull(11) ? null : r.GetString(11);
                    targetDeleted = !r.IsDBNull(14); // rat.deleted_at — 이미 소프트 삭제됨
                    offenderId = r.IsDBNull(15) ? null : r.GetGuid(15);
                    offenderName = author;
                }
            }
            else // user
            {
                offenderName = r.IsDBNull(13) ? null : r.GetString(13);
                title = offenderName is null ? "(알 수 없는 유저)" : $"@{offenderName}";
                offenderId = Guid.TryParse(r.GetString(3), out var ug) ? ug : null;
                sub = null;
                body = null;
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

    /// 신고된 리뷰 소프트 삭제(deleted_at) + 해당 신고 resolved 처리.
    /// 하드 삭제 대신 흔적을 남겨 복구·감사·신고화면 표시가 가능. 이미 삭제된 건 건드리지 않음.
    public async Task DeleteRatingAndResolveAsync(Guid reportId, string ratingId, Actor actor, CancellationToken ct = default)
    {
        if (!Configured || !Guid.TryParse(ratingId, out var rid)) return;
        await using var conn = await OpenAsync(ct);
        await using (var del = new NpgsqlCommand(
            """
            update public.ratings
            set deleted_at = now(), deleted_by = @by, deleted_reason = @reason
            where id = @rid and deleted_at is null
            """, conn))
        {
            del.Parameters.AddWithValue("rid", rid);
            del.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            del.Parameters.AddWithValue("reason", $"report:{reportId}");
            await del.ExecuteNonQueryAsync(ct);
        }
        await using (var upd = new NpgsqlCommand("update public.reports set status = 'resolved' where id = @id", conn))
        {
            upd.Parameters.AddWithValue("id", reportId);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, "rating.delete", ratingId, $"report {reportId}", ct);
    }

    // ── 리뷰 모더레이션(신고 없이 직접) NON-98 ──

    /// 리뷰(review 있는 평가) 목록. search 는 유저명·리뷰 본문 부분일치. 삭제된 것은 제외.
    public async Task<List<ReviewRow>> ListReviewsAsync(string? search, CancellationToken ct = default)
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
            limit 100
            """, conn);
        cmd.Parameters.AddWithValue("q", search ?? "");
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
}
