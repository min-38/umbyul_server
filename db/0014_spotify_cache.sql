-- NON-44: Spotify 원시 응답 캐시(검색·상세). 인메모리와 달리 재시작·다중 인스턴스에도 유지 → 429 완화.
-- 스펙 §데이터 저장 원칙의 "갱신되는 임시 캐시". 이미지는 URL만(파일 저장 금지). 서버(postgres, BYPASSRLS)만 접근.
create table if not exists public.spotify_cache (
    cache_key  text primary key,   -- 요청 URL
    payload    text not null,      -- 원시 JSON 응답
    fetched_at timestamptz not null default now()
);
create index if not exists spotify_cache_fetched_idx on public.spotify_cache (fetched_at);

-- RLS 활성화(정책 없음 = 직접 접근 차단). .NET 은 BYPASSRLS 로 동작.
alter table public.spotify_cache enable row level security;
