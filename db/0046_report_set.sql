-- NON-134(QA): 믹스 자체 신고 대상(set) 추가.
alter table public.reports drop constraint if exists reports_target_type_check;
alter table public.reports add constraint reports_target_type_check
    check (target_type in ('rating', 'user', 'comment', 'set_comment', 'set'));
