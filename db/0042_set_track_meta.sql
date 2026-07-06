-- NON-133: 믹스 트랙에 앨범·아티스트 메타 추가 (아티스트 링크·앨범명 표시용).
alter table public.set_tracks add column if not exists album_id text;
alter table public.set_tracks add column if not exists album_name text;
alter table public.set_tracks add column if not exists artists jsonb;  -- [{id,name}] 개별 아티스트 링크용
