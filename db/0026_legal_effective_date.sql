-- NON-72: 게시 버전의 시행일(효력 발생일). 게시 시 입력, 미입력이면 게시일(current_date) 저장.
-- 기존 게시본은 published_at 날짜로 백필.
alter table public.legal_versions add column if not exists effective_date date;
update public.legal_versions set effective_date = published_at::date where effective_date is null;
