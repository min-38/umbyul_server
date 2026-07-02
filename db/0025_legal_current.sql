-- NON-71: 게시본은 (종류×로케일)당 하나만 공개. is_current 로 명시하고 부분 유니크로 강제.
-- 새 버전 게시 시 이전 current 를 해제하고 새 것을 current 로(앱 로직). 인덱스는 두 개 동시 current 를 막음.
alter table public.legal_versions add column if not exists is_current boolean not null default false;

create unique index if not exists legal_versions_one_current
    on public.legal_versions (type, locale) where is_current;
