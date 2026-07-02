using Npgsql;

namespace Api.Spotify;

/// Spotify 원시 응답 캐시. 인터페이스로 추상화 → 추후 Redis(Upstash) 등으로 교체 용이 (NON-44/41).
public interface ISpotifyResponseCache
{
    /// TTL 내 신선한 캐시가 있으면 payload, 없으면 null.
    Task<string?> GetAsync(string key, TimeSpan ttl, CancellationToken ct);
    Task SetAsync(string key, string payload, CancellationToken ct);
}

/// DB 미설정 시 no-op(캐시 없이 라이브 조회).
public sealed class NullSpotifyResponseCache : ISpotifyResponseCache
{
    public Task<string?> GetAsync(string key, TimeSpan ttl, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task SetAsync(string key, string payload, CancellationToken ct) => Task.CompletedTask;
}

/// Supabase Postgres 백엔드. 캐시 실패는 조용히 스킵(라이브 조회로 폴백) — 캐시 때문에 서비스가 죽지 않게.
public sealed class PostgresSpotifyResponseCache(string dbConnString) : ISpotifyResponseCache
{
    public async Task<string?> GetAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select payload from public.spotify_cache where cache_key = @k and fetched_at > @cutoff", conn);
            cmd.Parameters.AddWithValue("k", key);
            cmd.Parameters.AddWithValue("cutoff", DateTimeOffset.UtcNow - ttl);
            return await cmd.ExecuteScalarAsync(ct) as string;
        }
        catch (NpgsqlException) { return null; }
    }

    public async Task SetAsync(string key, string payload, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """
                insert into public.spotify_cache (cache_key, payload, fetched_at)
                values (@k, @p, now())
                on conflict (cache_key) do update set payload = excluded.payload, fetched_at = now()
                """, conn);
            cmd.Parameters.AddWithValue("k", key);
            cmd.Parameters.AddWithValue("p", payload);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (NpgsqlException) { /* 캐시 쓰기 실패는 무시 */ }
    }
}
