-- 0053_ratings_drop_write_policies.sql
-- ratings 의 authenticated 직접 쓰기 정책·권한 제거 (3차 QA DB-4).
-- 아키텍처는 "모든 쓰기는 .NET Api(service_role) 경유"로 이동했고, 0006 이후 테이블은 전부 정책 없음.
-- ratings 만 0002 의 own insert/update/delete 정책 + grant 가 남아, JWT 소지자가 PostgREST 로
-- 직접 평점을 쓰면 제재 검사(Moderation)·최소 리뷰 길이(NON-91)·표시메타 일관성·레이트리밋을 우회한다.
-- 읽기(public read)는 유지 — ratings 는 공개. 재실행 가능.
drop policy if exists "own insert" on public.ratings;
drop policy if exists "own update" on public.ratings;
drop policy if exists "own delete" on public.ratings;
revoke insert, update, delete on public.ratings from authenticated;
