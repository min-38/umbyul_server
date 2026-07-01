-- NON-43: 평점 표시 메타데이터(캐시). 홈·프로필·아티스트 리뷰 피드를 Spotify 호출 없이 렌더하기 위해
-- 평점 생성 시 표시용 이름·아티스트·커버 URL을 함께 저장(스펙 §데이터 저장 원칙의 "갱신되는 임시 캐시").
-- 커버는 URL만(이미지 파일 저장 금지). 구 평점은 NULL → 1회 백필 또는 재평가 시 채워짐.
alter table public.ratings add column if not exists target_name text;
alter table public.ratings add column if not exists target_artist text;
alter table public.ratings add column if not exists target_image_url text;
