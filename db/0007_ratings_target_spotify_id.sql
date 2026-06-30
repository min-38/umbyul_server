-- NON-24: 프로필 리뷰 목록에서 곡/앨범 이름을 보여주기 위한 Spotify 포인터.
-- target_id(ISRC/UPC)는 집계 키, target_spotify_id 는 표시용 라이브 조회 포인터(콘텐츠 비영구).
-- 평가 등록(NON-7 확장) 시 함께 저장. 구 평점은 null → 목록 해석 불가.
alter table public.ratings add column if not exists target_spotify_id text;
