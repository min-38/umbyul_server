-- NON-31: 유저별 알림 설정. 없으면 전부 on(기본값)으로 취급.
-- master=false면 모든 알림 차단. 종류별(follow/review_like) on/off.
create table if not exists public.notification_prefs (
    user_id     uuid primary key references public.users (id) on delete cascade,
    master      boolean not null default true,
    follow      boolean not null default true,
    review_like boolean not null default true,
    updated_at  timestamptz not null default now()
);

alter table public.notification_prefs enable row level security;
