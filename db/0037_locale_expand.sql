-- 0037_locale_expand.sql (NON-125)
-- users.locale CHECK를 ja/es까지 확장. 기존 제약(0011)은 ('ko','en')만 허용 →
-- 로그인 유저가 일본어·스페인어를 저장하려면 아래로 넓혀야 함(미적용이면 /me/locale 저장이 23514로 실패, 쿠키는 적용됨).
-- 0011에서 `add column ... check(...)`로 만든 인라인 제약의 기본 이름 = users_locale_check.

alter table public.users drop constraint if exists users_locale_check;
alter table public.users add constraint users_locale_check
    check (locale is null or locale in ('ko', 'en', 'ja', 'es'));
