-- NON-73: 관리자 편집 FAQ. 구조화된 항목(질문·답변·카테고리·순서). 로케일별, en 폴백.
-- 콘텐츠는 관리자 편집, 디자인은 웹 컴포넌트. 법적문서 아니라 버전·시행일 없음.
create table if not exists public.faq_items (
    id         uuid primary key default gen_random_uuid(),
    locale     text not null,
    category   text not null default '',
    question   text not null,
    answer     text not null default '',   -- 마크다운
    sort_order int  not null default 0,
    published  boolean not null default false,
    updated_at timestamptz not null default now(),
    updated_by uuid                          -- admins.id (FK 안 검)
);
create index if not exists faq_items_public_idx on public.faq_items (locale, published, sort_order);

alter table public.faq_items enable row level security; -- 정책 없음 = anon 직접 접근 차단.
