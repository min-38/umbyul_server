-- NON-47: 유저 모더레이션. 제재 이력(user_sanctions) + 집행용 denormalized 상태(users).
-- 경고/기간정지/영구정지/해제를 모두 이력으로 남기고, 빠른 집행(NON-48)을 위해
-- users 에 현재 상태(suspended_until, banned)를 비정규화. deleted_by 처럼 admin_id 는 FK 안 검(별도 테이블).
create table if not exists public.user_sanctions (
    id             bigint generated always as identity primary key,
    user_id        uuid not null references public.users (id) on delete cascade,
    type           text not null check (type in ('warning', 'suspension', 'ban', 'unban')),
    until          timestamptz,                 -- suspension 해제 시각(그 외 null)
    reason         text,
    admin_id       uuid,                        -- admins.id (FK 안 검)
    admin_username text not null,
    report_id      uuid,                        -- 신고에서 부여 시 연결(nullable)
    created_at     timestamptz not null default now()
);
create index if not exists user_sanctions_user_idx on public.user_sanctions (user_id, created_at desc);

alter table public.users add column if not exists suspended_until timestamptz;
alter table public.users add column if not exists banned boolean not null default false;
