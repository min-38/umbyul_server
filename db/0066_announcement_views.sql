-- NON-158: 공지 조회 수. 상세 열람 시 +1. 없어도 읽기 API는 best-effort로 동작(기본 0).
alter table public.announcements add column if not exists view_count int not null default 0;
