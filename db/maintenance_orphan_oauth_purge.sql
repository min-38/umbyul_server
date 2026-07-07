-- maintenance_orphan_oauth_purge.sql (LEG-13, NON-142)
-- 마이그레이션 아님 — 주기적으로 돌리는 유지보수 쿼리(수동 실행 또는 pg_cron 스케줄).
--
-- 목적: OAuth 로 가입을 시작했으나 온보딩(프로필 생성)을 끝내지 않아
--       public.users 행이 없는 auth.users 레코드를 파기(데이터 최소화·GDPR).
--       이런 계정은 동의 기록도 없이 이메일만 남아 있음.
--
-- 안전장치: 30일 유예 — 그보다 최근 가입은 온보딩 진행 중일 수 있어 건드리지 않음.
-- auth.users 삭제는 Supabase 인증 관련 행(sessions 등)에 FK cascade 로 정리됨.
-- 실행 전 확인용 SELECT 를 먼저 돌려 대상 수를 점검할 것.

-- 1) 확인(삭제 전 대상 미리보기):
--   select au.id, au.email, au.created_at
--   from auth.users au
--   where not exists (select 1 from public.users u where u.id = au.id)
--     and au.created_at < now() - interval '30 days'
--   order by au.created_at;

-- 2) 파기:
delete from auth.users au
where not exists (select 1 from public.users u where u.id = au.id)
  and au.created_at < now() - interval '30 days';

-- pg_cron 예시(매일 04:00 UTC):
--   select cron.schedule('purge-orphan-oauth', '0 4 * * *', $$
--     delete from auth.users au
--     where not exists (select 1 from public.users u where u.id = au.id)
--       and au.created_at < now() - interval '30 days';
--   $$);
