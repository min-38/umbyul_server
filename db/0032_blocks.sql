-- NON-115: 유저 차단(상호). blocker 가 blocked 를 차단.
-- 상호 판정 = 어느 방향이든 행이 있으면 서로 안 보임(피드·프로필·팔로우에서 제외).
create table if not exists public.blocks (
    blocker_id uuid not null references public.users (id) on delete cascade,
    blocked_id uuid not null references public.users (id) on delete cascade,
    created_at timestamptz not null default now(),
    primary key (blocker_id, blocked_id),
    constraint blocks_no_self check (blocker_id <> blocked_id)
);

-- "나를 차단한 사람" 역방향 조회용(상호 판정의 두 번째 항).
create index if not exists blocks_blocked_idx
    on public.blocks (blocked_id);
