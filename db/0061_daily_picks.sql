-- NON-154: Discover "오늘의 음악" — 운영자 큐레이션 데일리 픽.
-- 어드민이 큐를 미리 쌓고(pick_date), 시스템은 pick_date <= 오늘 중 최신 1건을 노출.
-- 날짜 기반 로테이션이라 크론·상태변경 불필요, 전날 공백이어도 자동으로 최신 픽이 노출.
-- 표시 메타(이름/아티스트/커버)는 저장 안 함 — Api가 target으로 Spotify 상세(캐시) 해석.
create table if not exists public.daily_picks (
    id                uuid primary key default gen_random_uuid(),
    target_type       text not null check (target_type in ('track', 'album')),
    target_spotify_id text not null,
    note              text,                       -- 운영자 코멘트("오늘의 음악" 이유), 선택
    pick_date         date not null unique,       -- 노출일. 하루 1개.
    created_at        timestamptz not null default now()
);

create index if not exists daily_picks_date_idx on public.daily_picks (pick_date desc);

-- 다른 소유 테이블과 동일: RLS 활성화 + 정책 없음 = PostgREST 직접 접근 차단, .NET(service_role)만 접근.
alter table public.daily_picks enable row level security;
