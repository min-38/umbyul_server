-- 0055_sets_soft_delete.sql
-- 세트/세트댓글 소프트삭제 컬럼 (3차 QA DB-9).
-- sets 는 소프트삭제 컬럼이 전혀 없어 Admin 이 신고된 믹스를 하드삭제 없이 내릴 수 없다.
-- set_comments 는 deleted_at 만 있고 누가/왜 삭제했는지 기록이 없다(ratings/review_comments 와 불일치).
-- 여기서는 컬럼만 추가(전부 nullable) — 읽기 경로 `deleted_at is null` 필터와 Admin 테이크다운 UI 는
-- MISS-1(NON-141)에서. 컬럼만으론 동작 변화 없음(구스키마 대비 안전). deleted_by 는 admins.id 참조 의도
-- 이나 별테이블이라 FK 미검(ratings.deleted_by 와 동일 패턴). 재실행 가능.
alter table public.sets add column if not exists deleted_at     timestamptz;
alter table public.sets add column if not exists deleted_by     uuid;
alter table public.sets add column if not exists deleted_reason text;

alter table public.set_comments add column if not exists deleted_by     uuid;
alter table public.set_comments add column if not exists deleted_reason text;
