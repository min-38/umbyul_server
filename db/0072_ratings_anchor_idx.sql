-- QA6-3 (NON-195): 평가 집계를 앵커(target_id = ISRC/UPC, 0005) 기준으로 통일하기 위한 지원 인덱스.
-- 차트/트랙리스트 배지/아티스트 트랙 집계가 그동안 target_spotify_id로 그룹화돼, 같은 곡의
-- 멀티에디션(같은 ISRC, 다른 spotify_id)이 분산 집계됨(차트 중복·순위 희석). target_id로 그룹화 통일.
create index if not exists ratings_anchor_active_idx
    on public.ratings (target_type, target_id) where deleted_at is null;
