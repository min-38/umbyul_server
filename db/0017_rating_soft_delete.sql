-- NON-46: 신고 처리로 리뷰를 삭제할 때 소프트 삭제(deleted_at).
-- 하드 삭제 대신 흔적을 남겨 복구·감사·신고화면 표시·댓글 스레드 유지가 가능하도록 함.
-- 공개 쿼리는 모두 `deleted_at is null` 로 필터, 작성자 본인 프로필에만 묘비로 노출.
-- deleted_by 는 admins.id(별도 테이블) 이라 FK 는 걸지 않음(감사 로그 admin_actions 로 이중 기록).
alter table public.ratings add column if not exists deleted_at timestamptz;
alter table public.ratings add column if not exists deleted_by uuid;
alter table public.ratings add column if not exists deleted_reason text;

-- 공개 집계·목록에서 삭제 리뷰 제외를 빠르게.
create index if not exists ratings_active_idx on public.ratings (target_type, target_spotify_id)
    where deleted_at is null;
