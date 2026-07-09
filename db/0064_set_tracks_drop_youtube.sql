-- NON-154: 믹스 트랙의 개별 YouTube 입력 제거. YouTube는 이제 곡 자체의 전역 링크
-- (target_youtube_links, ISRC 기준)에서 표시하므로 set_tracks.youtube_url은 불필요. 재실행 가능.
alter table public.set_tracks drop column if exists youtube_url;
