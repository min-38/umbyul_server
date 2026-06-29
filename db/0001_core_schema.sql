-- 0001_core_schema.sql
-- 이식 가능한 표준 PostgreSQL 스키마 (Phase 1 핵심 테이블).
-- Supabase 의존 없음 — 어떤 Postgres에도 적용 가능.
-- Supabase 전용(auth.users FK, RLS)은 0002_supabase_rls.sql 에 분리.

-- ⚠️ 레거시 리셋: 이전 실험용 스키마(MusicBrainz 시절)를 정리하고 현재 설계로 교체한다.
--    재실행 가능하도록 우리 테이블도 함께 drop. CASCADE 로 FK 의존 순서 무시.
drop table if exists
    public.review_reactions,
    public.review_reports,
    public.reviews,
    public.ratings,
    public.point_transactions,
    public.follows,
    public.tracks,
    public.albums,
    public.profiles,
    public.users
cascade;

-- users: 앱 프로필. id 는 인증 유저 id(가입 시 프로비저닝 단계에서 주입).
create table if not exists public.users (
    id         uuid primary key,
    username   text not null,
    country    text,                          -- ISO 3166-1 alpha-2 (OAuth 유저는 온보딩 전 NULL 가능)
    avatar_url text,
    is_artist  boolean not null default false,
    created_at timestamptz not null default now(),
    constraint users_username_format check (
        char_length(username) between 2 and 30
        and username ~ '^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$'
    ),
    constraint users_country_format check (country is null or country ~ '^[A-Z]{2}$')
);

-- 핸들은 대소문자 무시 유니크 (사칭 방지 + NON-18 중복검사 기준)
create unique index if not exists users_username_lower_key on public.users (lower(username));

-- albums: Spotify 캐시 (ID + 이미지 URL 등 최소 메타만)
create table if not exists public.albums (
    spotify_id   text primary key,
    upc          text,                         -- 표준 앨범 코드
    name         text not null,
    artist       text not null,
    image_url    text,                         -- URL만 저장 (이미지 파일 저장 금지)
    release_date date
);
create index if not exists albums_upc_idx on public.albums (upc);

-- tracks: Spotify 캐시
create table if not exists public.tracks (
    spotify_id   text primary key,
    isrc         text,                         -- 표준 트랙 코드
    album_id     text references public.albums (spotify_id) on delete cascade,
    name         text not null,
    track_number int
);
create index if not exists tracks_isrc_idx on public.tracks (isrc);

-- ratings: 평점·리뷰. target 은 폴리모픽(album/track) → 단일 FK 불가, target_type CHECK + 인덱스로 처리.
create table if not exists public.ratings (
    id          uuid primary key default gen_random_uuid(),
    user_id     uuid not null references public.users (id) on delete cascade,
    target_type text not null check (target_type in ('album', 'track')),
    target_id   text not null,                 -- albums.spotify_id 또는 tracks.spotify_id
    score       numeric(2, 1) not null check (
        score >= 0.5 and score <= 5.0 and (score * 2) = floor(score * 2)  -- 0.5 단위
    ),
    review      text,
    created_at  timestamptz not null default now(),
    constraint ratings_one_per_target unique (user_id, target_type, target_id)  -- 1인 1평점/대상
);
create index if not exists ratings_target_idx on public.ratings (target_type, target_id);
