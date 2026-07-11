-- QA6-5 (NON-197): target_artists jsonb 키 케이싱 정규화.
-- 쓰기는 System.Text.Json 기본 PascalCase("Id"/"Name")인데 레거시 소문자("id"/"name") 행이 섞이면,
-- ArtistEndpoints 의 containment `@> [{"Id": ...}]` 가 에러 없이 조용히 매칭 실패(아티스트 페이지에서 누락).
-- 레거시 행을 한 번 PascalCase 로 정규화. ("id" 키가 있는 행만 대상 → 이미 PascalCase 인 행은 불변, 재실행 no-op.)
--
-- 참고) ChartEndpoints 의 coalesce(e->>'Id', e->>'id') 방어 코드는 마이그레이션 미적용 데이터 대비
--       graceful degradation 위해 그대로 유지(코드 변경 없음).
update public.ratings
set target_artists = (
    select jsonb_agg(jsonb_build_object(
        'Id',   coalesce(e->>'Id',   e->>'id'),
        'Name', coalesce(e->>'Name', e->>'name')))
    from jsonb_array_elements(target_artists) e)
where target_artists is not null
  and exists (select 1 from jsonb_array_elements(target_artists) e where e ? 'id');

-- junction 승격 기준(기록용, 지금은 조치 없음):
--   아티스트 페이지에 페이지네이션/정렬이 필요해지거나 차트 unnest가 느려지면
--   rating_artists(rating_id, artist_spotify_id, name, position) junction 으로 승격.
--   평가별 스냅샷을 유지하므로 Spotify 컴플라이언스(영구 카탈로그 금지) 중립.
