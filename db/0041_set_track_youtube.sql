-- NON-133: 믹스 트랙에 개별 유튜브 링크(선택).
-- 스포티파이 없는 유저도 곡별로 유튜브에서 들을 수 있게. DJ가 추가 시 직접 붙임(선택).
alter table public.set_tracks add column if not exists youtube_url text;
