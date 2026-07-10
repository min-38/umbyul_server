-- NON-158: 조회 수 중복 제거. 뷰어(로그인 user / 익명 IP 해시)당 공지 1회만 카운트.
create table if not exists public.announcement_views (
    announcement_id uuid not null references public.announcements (id) on delete cascade,
    viewer          text not null,   -- 'u:{userId}'(로그인) | 'ip:{hash}'(익명)
    created_at      timestamptz not null default now(),
    primary key (announcement_id, viewer)
);
-- 정책 없음 = anon 차단, .NET service_role만.
alter table public.announcement_views enable row level security;
