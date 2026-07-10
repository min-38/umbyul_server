-- NON-41 L5: 아티스트 페이지가 ratings.target_artists 를 jsonb containment(@>)로 조회(수록곡 라이브 호출 대체).
-- GIN 인덱스로 가속. 없어도 동작(seq scan)하므로 미적용 상태에서도 안전.
create index if not exists ratings_target_artists_gin on public.ratings using gin (target_artists);
