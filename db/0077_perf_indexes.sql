-- QA6-12 (NON-204): 성능 인덱스 일괄. 매 요청 시퀀스 스캔이던 핫 경로 지원.
-- ⚠️ 대형 테이블은 SQL Editor에서 `create index concurrently ...`를 트랜잭션 밖에서 개별 실행 권장(잠금 최소화).
--    아래는 이식성 위해 plain create index. MVP 규모에선 잠금 시간 미미.

-- 피드 장르 칩: where target_spotify_id = any(@ids). 유일 인덱스가 (target_type, target_spotify_id)라 선두 없이 못 탐 → 시퀀스 스캔.
create index if not exists genre_tags_spotify_idx on public.genre_tags (target_spotify_id);

-- 믹스 최신순: where deleted_at is null order by created_at desc (sets_owner_idx는 owner 선두라 미커버).
create index if not exists sets_active_created_idx on public.sets (created_at desc) where deleted_at is null;

-- 유저 검색/멘션 자동완성: username ilike '%q%'/'q%' — lower unique는 ILIKE 못 서빙 → trigram GIN.
create extension if not exists pg_trgm;
create index if not exists users_username_trgm_idx on public.users using gin (username gin_trgm_ops);

-- 알림 unread 뱃지: 유저별 미읽음만.
create index if not exists notifications_unread_idx on public.notifications (recipient_id) where read_at is null;

-- /chart/users 팔로워 기간 필터: follows.created_at.
create index if not exists follows_created_idx on public.follows (created_at);
