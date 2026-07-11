-- QA9-6(결정2): 리뷰어 레벨/XP 공개 표시 옵트아웃. true면 공개 화면(피드·리뷰·댓글·차트·공개 프로필)에서
-- 레벨 뱃지를 숨긴다(레벨 0으로 반환 → 클라가 미표시). 기본 false(현행대로 표시).
alter table public.users add column if not exists hide_level boolean not null default false;
