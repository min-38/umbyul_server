-- NON-70: 게시 버전에 사람이 지정하는 버전 라벨(예: v1.01). 개정 표현·이력용.
alter table public.legal_versions add column if not exists version text;
