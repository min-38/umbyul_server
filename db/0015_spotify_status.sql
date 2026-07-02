-- NON-46: Spotify 레이트리밋 상태(관리자 모니터링). 단일 행. Api가 429 감지 시 기록.
create table if not exists public.spotify_status (
    id                  int primary key default 1,
    blocked_until       timestamptz,        -- Spotify가 통보한 Retry-After 기준 해제 예정 시각
    retry_after_seconds int,
    updated_at          timestamptz,
    constraint spotify_status_single check (id = 1)
);
insert into public.spotify_status (id) values (1) on conflict (id) do nothing;

alter table public.spotify_status enable row level security;
