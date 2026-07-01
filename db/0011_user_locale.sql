-- 0011_user_locale.sql
-- 회원별 표시 언어 설정(i18n, NON-39). 표준 SQL, 이식 가능.
--   locale: 'ko' | 'en'. NULL = 미설정 → 로그인 시 IP·브라우저 기본값을 따름.
alter table public.users add column if not exists locale text
    check (locale is null or locale in ('ko', 'en'));
