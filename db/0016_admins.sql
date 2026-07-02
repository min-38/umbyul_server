-- NON-46: 관리자 계정(공개 users와 완전 분리) + 감사 로그.
-- 암호는 해시만 저장(BCrypt). 첫 관리자는 Admin 앱 부트스트랩(ADMIN:BOOTSTRAP_*)으로 생성.
create table if not exists public.admins (
    id            uuid primary key default gen_random_uuid(),
    username      text not null,
    password_hash text not null,
    created_at    timestamptz not null default now()
);
create unique index if not exists admins_username_lower_key on public.admins (lower(username));

-- 감사 로그: 누가(관리자) 무엇을 했는지. admin_username 은 비정규화(관리자 삭제돼도 이력 유지).
create table if not exists public.admin_actions (
    id             bigint generated always as identity primary key,
    admin_id       uuid references public.admins (id) on delete set null,
    admin_username text not null,
    action         text not null,          -- 예: login, report.reviewed, report.resolved, rating.delete, admin.create
    target         text,                   -- 대상 식별자(리포트 id, 리뷰 id 등)
    detail         text,
    created_at     timestamptz not null default now()
);
create index if not exists admin_actions_created_idx on public.admin_actions (created_at desc);

-- RLS 활성화(정책 없음 = 직접 접근 차단). Admin/Api 는 service_role 로 동작.
alter table public.admins enable row level security;
alter table public.admin_actions enable row level security;
