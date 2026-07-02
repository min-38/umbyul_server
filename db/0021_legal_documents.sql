-- NON-64: 관리자 작성 약관/개인정보처리방침. 로케일별 1개(현재 버전) + 게시 여부.
-- 정본·폴백은 en. locale 은 자유 코드(ko/ja/es… 확장 가능). content 는 마크다운.
create table if not exists public.legal_documents (
    id         uuid primary key default gen_random_uuid(),
    type       text not null check (type in ('terms', 'privacy')),
    locale     text not null,
    content    text not null default '',
    published  boolean not null default false,
    updated_at timestamptz not null default now(),
    updated_by uuid,                          -- admins.id (FK 안 검)
    unique (type, locale)
);

alter table public.legal_documents enable row level security; -- 정책 없음 = anon 직접 접근 차단. .NET service_role 로만.
