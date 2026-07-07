-- 0052_reports_target_uuid.sql
-- reports.target_id uuid 형식 CHECK (3차 QA DB-3).
-- 5개 대상 타입(rating/user/comment/set_comment/set) 모두 uuid 인데 target_id 는 미검증 text.
-- Admin 신고 목록이 `target_id::uuid` 로 조인 → 잘못된 값 한 건이면 22P02 로 신고 페이지 전체 크래시
-- (유저가 유발 가능한 모더레이션 DoS). API 도 Guid.TryParse 로 신규 저장을 막지만, DB 레벨 방어선 추가.
-- 기존 레거시 행이 형식을 위반할 수 있어 NOT VALID(신규/변경 행만 강제). 정리 후
--   `alter table public.reports validate constraint reports_target_id_uuid;` 로 전체 검증 가능.
alter table public.reports drop constraint if exists reports_target_id_uuid;
alter table public.reports add constraint reports_target_id_uuid
    check (target_id ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$')
    not valid;
