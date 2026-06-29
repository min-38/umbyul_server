-- 0003_add_terms_accepted.sql
-- 약관·개인정보 동의 시각. 프로비저닝(users row 생성) 시 now() 기록 — 법적 증빙.
-- 표준 SQL, 이식 가능.
alter table public.users add column if not exists terms_accepted_at timestamptz;
