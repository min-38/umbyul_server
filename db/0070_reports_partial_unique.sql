-- QA6-1 (NON-193): 신고 재신고 영구 차단 해소.
-- 기존 reports_one_per_target(영구 unique, status 무관)는 관리자가 신고를 resolved 처리하고
-- 콘텐츠가 부활해도 같은 신고자가 재신고 불가하게 만듦. 열린(pending) 신고에 대해서만 유일하도록
-- partial unique로 교체 → resolved 이후엔 재신고 가능.
-- API insert는 arbiter 미지정 `on conflict do nothing`이라 구/신 스키마 모두 안전(적용 전엔 기존 동작 유지).

alter table public.reports drop constraint if exists reports_one_per_target;

create unique index if not exists reports_one_open_per_target
    on public.reports (reporter_id, target_type, target_id)
    where status = 'pending';
