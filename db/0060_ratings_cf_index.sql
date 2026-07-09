-- 0060_ratings_cf_index.sql
-- NON-83: 협업 필터(추천 CF)의 이웃→후보 스캔 지원(선택·성능). 코드는 이 인덱스 없이도 동작.
-- 이웃 user_id 집합으로 (target_type, target_spotify_id) 를 훑는데, 기존 unique idx
-- (user_id, target_type, target_id) 는 target_id 기준이라 target_spotify_id 를 커버 못 해 힙 페치 발생.
-- 소프트삭제 제외 + 표시 키까지 커버하는 부분 인덱스. 재실행 가능(idempotent).
create index if not exists ratings_user_target_active_idx
    on public.ratings (user_id, target_type, target_spotify_id)
    where deleted_at is null;
