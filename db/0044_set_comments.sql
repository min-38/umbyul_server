-- NON-133: 믹스 댓글(평면). 소프트삭제.
create table if not exists public.set_comments (
    id         uuid        primary key default gen_random_uuid(),
    set_id     uuid        not null references public.sets (id) on delete cascade,
    user_id    uuid        not null references public.users (id) on delete cascade,
    body       text        not null,
    created_at timestamptz not null default now(),
    deleted_at timestamptz
);

create index if not exists set_comments_set_idx on public.set_comments (set_id, created_at desc);

alter table public.set_comments enable row level security;
