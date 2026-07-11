-- QA6-4 (NON-196): 0052 CHECK(reports_target_id_uuid)·0054 FK(user_sanctions_report_fkey)가
-- NOT VALID 인 채 VALIDATE 마이그레이션이 없어 레거시 위반 행이 남을 수 있음. NOT VALID는 신규 행만 검사 →
-- 비uuid target_id 레거시 신고가 있으면 AdminDb.Reports 의 target_id::uuid 캐스트가 22P02로 신고 페이지를
-- 크래시(0052가 막으려던 모더레이션 DoS). 레거시 정리 후 VALIDATE.
--
-- 위반 행 사전 확인(참고):
--   select id, target_type, target_id from public.reports
--   where target_id !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';

-- 정리 1) uuid 형식이 아닌 레거시 신고 삭제. 현 시스템의 5개 대상 타입은 전부 uuid이고, 비uuid target 은
--         admin 이 해석·처리 불가하며 신고 페이지를 크래시시키는 불가동 행(신규 행은 0052가 이미 차단).
--         user_sanctions_report_fkey 가 on delete set null 이라 참조 제재의 report_id 는 자동 null 처리됨.
delete from public.reports
where target_id !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';

-- 정리 2) 이미 사라진 신고를 가리키는 레거시 dangling 참조 해제(제재 자체는 보존).
update public.user_sanctions s set report_id = null
where report_id is not null and not exists (select 1 from public.reports r where r.id = s.report_id);

alter table public.reports validate constraint reports_target_id_uuid;
alter table public.user_sanctions validate constraint user_sanctions_report_fkey;
