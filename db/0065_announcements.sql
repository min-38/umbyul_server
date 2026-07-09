-- NON-158/165: 공지사항. announcements(게시 상태·메타) + announcement_locales(로케일별 본문).
create table if not exists public.announcements (
    id           uuid primary key default gen_random_uuid(),
    published    boolean not null default false,
    published_at timestamptz,
    notified_at  timestamptz,           -- 게시 강제 알림 1회 발송 가드(NON-166)
    created_at   timestamptz not null default now(),
    updated_at   timestamptz not null default now(),
    updated_by   uuid                    -- admins.id (FK 미강제)
);

create table if not exists public.announcement_locales (
    announcement_id uuid not null references public.announcements (id) on delete cascade,
    locale          text not null,       -- ko/en/ja/es; en 정본 폴백
    title           text not null default '',
    body            text not null default '',  -- markdown
    primary key (announcement_id, locale)
);

-- 공개 목록/상세 조회용(게시된 것, 최신순).
create index if not exists announcements_published_idx
    on public.announcements (published, published_at desc);

-- 정책 없음 = anon 차단, .NET service_role(BYPASSRLS)만 접근.
alter table public.announcements enable row level security;
alter table public.announcement_locales enable row level security;

-- 알림 type에 announcement 추가(NON-166 게시 알림). 0020/0039 drop-and-re-add 패턴.
alter table public.notifications drop constraint if exists notifications_type_check;
alter table public.notifications add constraint notifications_type_check
    check (type in ('follow', 'review_like', 'warning', 'mention', 'announcement'));
