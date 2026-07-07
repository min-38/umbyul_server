-- 0051_users_column_grant.sql
-- users 공개 SELECT 를 컬럼 화이트리스트로 축소 (3차 QA DB-2).
-- 0002 는 `grant select on public.users to anon, authenticated` (테이블 전체) + policy using(true) 를 걸었다.
-- 당시 주석("email 미보관이라 공개 안전")은 이후 추가된 birth_date/gender/locale/suspended_until/banned
-- 이전 기준 — 현재는 이 민감 컬럼들이 anon 키로 PostgREST 조회 가능(비공개 성별·생일 노출).
-- 공개 프로필 렌더에 필요한 컬럼만 노출로 축소. API(service_role)는 컬럼 grant 무관하게 전체 접근.
-- 재실행 가능.
revoke select on public.users from anon, authenticated;
grant select (id, username, country, avatar_url, is_artist, created_at)
    on public.users to anon, authenticated;
