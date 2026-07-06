-- NON-131: 댓글 @멘션 알림 제어.
-- 1) 멘션 알림 전역 on/off (기본 on).
alter table public.notification_prefs
    add column if not exists mention boolean not null default true;

-- 2) 특정 트랙/앨범만 멘션 알림 뮤트. row 있으면 그 대상의 멘션 알림 안 옴.
create table if not exists public.mention_mutes (
    user_id           uuid        not null references public.users (id) on delete cascade,
    target_type       text        not null check (target_type in ('track', 'album')),
    target_spotify_id text        not null,
    created_at        timestamptz not null default now(),
    primary key (user_id, target_type, target_spotify_id)
);

alter table public.mention_mutes enable row level security;

-- 3) 알림 type 에 'mention' 허용.
alter table public.notifications drop constraint if exists notifications_type_check;
alter table public.notifications add constraint notifications_type_check
    check (type in ('follow', 'review_like', 'warning', 'mention'));
