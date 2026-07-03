-- NON-103: 관리자 비활성화(삭제 대신). false 면 로그인 차단. 기본 활성.
alter table public.admins
    add column if not exists is_active boolean not null default true;
