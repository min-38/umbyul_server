-- 0081_ratings_public_read_exclude_deleted.sql
-- ratings 공개 SELECT 정책이 soft-deleted(모더레이션 삭제) 행까지 노출하는 문제 수정 (출시 전 감사 NON-251).
-- 0002 의 policy `using (true)` + `grant select ... to anon` 는 0017(deleted_at 도입) 이후에도 갱신되지 않아,
-- Supabase anon/authenticated 키로 PostgREST 를 직접 조회하면 삭제된 리뷰 본문·deleted_reason(신고 ID) 까지 노출됨.
-- 읽기(공개)는 유지하되 삭제 안 된 행만. 삭제 행이 아예 안 나오므로 deleted_reason 도 함께 가려짐.
-- API(service_role)는 정책과 무관하게 전체 접근 → Admin 모더레이션 화면에는 영향 없음.
-- 재실행 가능.
drop policy if exists "public read" on public.ratings;
create policy "public read" on public.ratings for select using (deleted_at is null);
