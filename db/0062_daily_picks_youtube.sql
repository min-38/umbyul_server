-- NON-154: 오늘의 음악 카드에 운영자 지정 YouTube 링크. Spotify·MusicBrainz는 자동(메타 기반)이지만
-- YouTube는 정확한 영상 ID가 메타에 없어 운영자가 직접 URL을 넣는다. 재실행 가능(idempotent).
alter table public.daily_picks add column if not exists youtube_url text;
