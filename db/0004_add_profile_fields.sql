-- 0004_add_profile_fields.sql
-- 온보딩("Almost there")에서 받는 추가 프로필 필드. 표준 SQL, 이식 가능.
--   birth_date: 만 14세 미만 가입 차단 근거(앱·서버에서 검증).
--   gender: 선택(비공개 포함). 허용값 CHECK.
alter table public.users add column if not exists birth_date date;
alter table public.users add column if not exists gender text
    check (gender is null or gender in ('male', 'female', 'other', 'undisclosed'));
