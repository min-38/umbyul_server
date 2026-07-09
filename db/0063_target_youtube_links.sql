-- NON-154: YouTube 링크를 곡/앨범 "음악 고유 ID"(target_id = ISRC(track)/UPC(album), 없으면 spotify_id 폴백)에
-- 귀속. ratings와 동일한 키라 같은 곡의 다른 에디션에도 링크가 뜬다. 링크 있으면 상세·오늘의 픽 어디서든 아이콘.
-- 운영자는 어드민에서 spotify_id로 추가 → 우리 ratings.target_id 로 해석해 저장.
create table if not exists public.target_youtube_links (
    target_type text not null check (target_type in ('track', 'album')),
    target_id   text not null,   -- ISRC(track)/UPC(album), 없으면 spotify_id 폴백 (ratings.target_id와 동일 규약)
    youtube_url text not null,
    updated_at  timestamptz not null default now(),
    primary key (target_type, target_id)
);

-- 다른 소유 테이블과 동일: RLS 활성화 + 정책 없음 = PostgREST 직접 접근 차단, .NET(service_role)만.
alter table public.target_youtube_links enable row level security;

-- 이전 위치(0062) 정리 — YouTube는 이제 대상별 테이블에서 관리.
alter table public.daily_picks drop column if exists youtube_url;
