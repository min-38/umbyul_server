-- NON-57: 경고(warning) 유저 통보. 유저가 경고 배너를 "확인"한 시각.
-- 미확인(acknowledged_at is null) 경고만 상단 배너에 노출.
alter table public.user_sanctions add column if not exists acknowledged_at timestamptz;
