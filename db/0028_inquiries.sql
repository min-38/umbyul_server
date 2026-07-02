-- NON-76: 문의(Contact). 공개 접수 → 관리자 열람 + 처리 완료 토글. 답변은 관리자가 외부 이메일로 직접.
create table if not exists public.inquiries (
    id         uuid primary key default gen_random_uuid(),
    category   text not null default '',
    email      text not null,
    title      text not null,
    content    text not null,
    handled    boolean not null default false,
    handled_at timestamptz,
    handled_by uuid,                          -- admins.id (FK 안 검)
    created_at timestamptz not null default now()
);
create index if not exists inquiries_pending_idx on public.inquiries (handled, created_at desc);

alter table public.inquiries enable row level security; -- 정책 없음 = anon 직접 접근 차단. .NET service_role 로만.
