-- QA6-2 (NON-194): review_comments.deleted_by 를 관리자(모더레이션 삭제) 전용으로 정규화.
-- 기존엔 본인 삭제 시 user_id, 관리자 삭제 시 admin_id 가 섞여 저장돼 판별자 없이 FK 불가.
-- set_comments 컨벤션 채택: 본인 삭제는 deleted_by=null, 관리자 삭제만 admins.id 기록.
--
-- ⚠️ 순서: API(CommentEndpoints 본인삭제 deleted_by 미기록)가 배포된 뒤 적용할 것.
--    적용 전 배포에서 본인삭제가 여전히 user_id를 쓰면 FK add(not valid)가 신규행을 즉시 검사해 실패함.
--
-- 백필: 본인 삭제 흔적(deleted_by=user_id) null 처리. (관리자 삭제는 admins.id 라 user_id와 불일치 → 보존.)
update public.review_comments set deleted_by = null where deleted_by = user_id;

alter table public.review_comments
    add constraint review_comments_deleted_by_fkey
    foreign key (deleted_by) references public.admins (id) on delete set null not valid;
alter table public.review_comments validate constraint review_comments_deleted_by_fkey;
