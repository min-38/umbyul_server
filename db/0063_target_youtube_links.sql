-- NON-154: YouTube 링크를 "오늘의 픽"이 아니라 곡/앨범 자체에 귀속. 링크가 있으면 상세 페이지·픽 어디서든
-- YouTube 아이콘 노출. 운영자가 어드민에서 대상별로 지정. 이전 접근(daily_picks.youtube_url)은 제거.
create table if not exists public.target_youtube_links (
    target_type       text not null check (target_type in ('track', 'album')),
    target_spotify_id text not null,
    youtube_url       text not null,
    updated_at        timestamptz not null default now(),
    primary key (target_type, target_spotify_id)
);

-- 다른 소유 테이블과 동일: RLS 활성화 + 정책 없음 = PostgREST 직접 접근 차단, .NET(service_role)만.
alter table public.target_youtube_links enable row level security;

-- 이전 위치(0062) 정리 — YouTube는 이제 대상별 테이블에서 관리.
alter table public.daily_picks drop column if exists youtube_url;
