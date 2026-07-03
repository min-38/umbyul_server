-- NON-122: 유저 장르 태깅(투표). pk가 (user, target, genre)라 한 유저가 한 대상에 복수 장르 가능.
create table if not exists public.genre_tags (
    user_id          uuid not null references public.users (id) on delete cascade,
    target_type      text not null check (target_type in ('track', 'album')),
    target_spotify_id text not null,
    genre_id         int not null references public.genres (id) on delete cascade,
    created_at       timestamptz not null default now(),
    primary key (user_id, target_type, target_spotify_id, genre_id)
);

-- 대상별 집계(상위 장르 투표수) 조회용.
create index if not exists genre_tags_target_idx
    on public.genre_tags (target_type, target_spotify_id);
