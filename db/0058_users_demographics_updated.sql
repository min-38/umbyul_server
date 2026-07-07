-- 0058_users_demographics_updated.sql
-- LEG-11 (NON-142): 국가/성별 온보딩 후 정정 허용(GDPR Art.16) — 단, 잦은 변경(인구통계 차트 조작·churn)
-- 방지 위해 변경 쿨다운을 두려고 마지막 변경 시각을 기록. null = 온보딩 후 아직 변경 안 함(최초 변경 자유).
alter table public.users add column if not exists demographics_updated_at timestamptz;
