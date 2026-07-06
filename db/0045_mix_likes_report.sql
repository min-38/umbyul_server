-- NON-133: 믹스 좋아요 + 믹스 댓글 신고 대상.
create table if not exists public.set_likes (
    set_id     uuid        not null references public.sets (id) on delete cascade,
    user_id    uuid        not null references public.users (id) on delete cascade,
    created_at timestamptz not null default now(),
    primary key (set_id, user_id)
);

alter table public.set_likes enable row level security;

-- 신고 대상에 set_comment 추가(믹스 댓글 신고).
alter table public.reports drop constraint if exists reports_target_type_check;
alter table public.reports add constraint reports_target_type_check
    check (target_type in ('rating', 'user', 'comment', 'set_comment'));
