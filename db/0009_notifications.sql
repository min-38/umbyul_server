-- NON-26: 알림. .NET(postgres, BYPASSRLS)로만 접근.
create table if not exists public.notifications (
    id           uuid primary key default gen_random_uuid(),
    recipient_id uuid not null references public.users (id) on delete cascade,
    actor_id     uuid not null references public.users (id) on delete cascade,
    type         text not null check (type in ('follow', 'review_like')),
    target_id    text,  -- review_like: rating id / follow: null
    read_at      timestamptz,
    created_at   timestamptz not null default now()
);
create index if not exists notifications_recipient_idx on public.notifications (recipient_id, created_at desc);

alter table public.notifications enable row level security;
