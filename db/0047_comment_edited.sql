-- 2차 QA BUG-11: 댓글 수정 시 '(수정됨)' 표시용. 리뷰 댓글 + 믹스 댓글에 수정 시각.
alter table public.review_comments add column if not exists edited_at timestamptz;
alter table public.set_comments   add column if not exists edited_at timestamptz;
