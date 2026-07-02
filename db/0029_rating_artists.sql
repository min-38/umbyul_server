-- NON-85: 평가 표시 메타에 아티스트 목록(구조화) 추가.
-- Discover·피드에서 각 아티스트 이름을 아티스트 페이지(/artist/{id})로 개별 링크하기 위함.
-- [{ "id": "...", "name": "..." }, ...] 형태(상세의 artists 그대로). 이름 콤마 문제를 피하려 조인 문자열 대신 배열로.
-- 표시용 조인 이름(target_artist)은 그대로 두고 병행. 구 평점은 NULL → 재평가/신규 평가 시 채워짐(링크 없이 이름만).
alter table public.ratings add column if not exists target_artists jsonb;
