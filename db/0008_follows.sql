-- NON-25: 팔로우 시스템. .NET(postgres, BYPASSRLS)로만 접근.
create table if not exists public.follows (
    follower_id  uuid not null references public.users (id) on delete cascade,
    following_id uuid not null references public.users (id) on delete cascade,
    created_at   timestamptz not null default now(),
    primary key (follower_id, following_id),
    constraint follows_no_self check (follower_id <> following_id)
);
-- 팔로워 목록(역방향) 조회용
create index if not exists follows_following_idx on public.follows (following_id);

alter table public.follows enable row level security;
