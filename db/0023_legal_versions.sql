-- NON-69: 약관/개인정보 게시 버전 이력. "게시"할 때마다 초안(legal_documents)을 불변 스냅샷으로 남긴다.
-- 공개는 (종류×로케일)의 최신 published_at 버전. legal_documents 는 편집 가능한 초안으로 유지.
create table if not exists public.legal_versions (
    id           uuid primary key default gen_random_uuid(),
    type         text not null check (type in ('terms', 'privacy')),
    locale       text not null,
    content      text not null,
    published_at timestamptz not null default now(),
    published_by uuid                        -- admins.id (FK 안 검)
);
create index if not exists legal_versions_lookup_idx on public.legal_versions (type, locale, published_at desc);

alter table public.legal_versions enable row level security; -- 정책 없음 = anon 직접 접근 차단.
