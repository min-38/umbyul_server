-- NON-133: DJ 세트 — 유저 큐레이션 트랙 세트.
-- 세트 트랙은 우리 카탈로그 트랙(spotify_id/isrc)을 denormalize 저장 → Spotify 재조회 없이 렌더.
-- 세트에서의 평점 = 그 트랙의 일반 평점(ratings)으로 흘러감(별도 평점 아님).
create table if not exists public.sets (
    id         uuid        primary key default gen_random_uuid(),
    owner_id   uuid        not null references public.users (id) on delete cascade,
    title      text        not null,
    note       text,
    listen_url text,        -- 듣기 링크(유튜브/스포티파이/… 아무 URL). nullable.
    created_at timestamptz not null default now()
);

create index if not exists sets_owner_idx on public.sets (owner_id, created_at desc);

create table if not exists public.set_tracks (
    set_id     uuid not null references public.sets (id) on delete cascade,
    spotify_id text not null,             -- 표시·듣기 포인터
    position   int  not null,             -- 세트 내 순서
    isrc       text,                       -- 평점 집계 키(ratings.target_id). null이면 평가 불가
    name       text not null,
    artist     text not null,
    image_url  text,
    primary key (set_id, spotify_id)      -- 한 세트에 같은 곡 중복 금지
);

alter table public.sets enable row level security;
alter table public.set_tracks enable row level security;
