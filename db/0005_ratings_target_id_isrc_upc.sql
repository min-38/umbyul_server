-- NON-6/NON-7: 평점 앵커를 ISRC(track)/UPC(album)로 확정 (기획안 §5 컴플라이언스).
-- 컬럼 타입은 그대로 text. 저장 값의 의미만 변경 → COMMENT 로 문서화.
-- ISRC/UPC가 없는 리소스는 spotify_id 로 폴백.
comment on column public.ratings.target_id is
    'ISRC(track) / UPC(album). 없으면 spotify_id 폴백. (이전 주석: spotify_id)';
