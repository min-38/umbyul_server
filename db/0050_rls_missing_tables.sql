-- 0050_rls_missing_tables.sql
-- RLS 누락 테이블 방어선 보강 (3차 QA DB-1).
-- 다른 테이블과 달리 이 5개는 RLS 활성화가 빠져 있어, Supabase 기본 권한상 anon/authenticated 가
-- PostgREST 로 직접 읽기/쓰기 가능(제재 이력·차단 그래프 노출, 장르 투표·차단 위조).
-- 정책은 두지 않음 = 직접 접근 전면 차단. 데이터 흐름은 .NET Api(service_role, RLS 우회)만.
-- 재실행 가능(idempotent).
alter table public.user_sanctions  enable row level security;
alter table public.feed_dismissals enable row level security;
alter table public.blocks          enable row level security;
alter table public.genres          enable row level security;
alter table public.genre_tags      enable row level security;
