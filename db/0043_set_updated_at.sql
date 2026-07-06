-- NON-133: 믹스 업데이트 시각 (상세 뷰 "업데이트 날짜" 표시용). 곡 추가/삭제/순서변경 등에서 갱신.
alter table public.sets add column if not exists updated_at timestamptz not null default now();
