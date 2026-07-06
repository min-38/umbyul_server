-- 2차 QA BUG-14 확장: explicit(19금) 플래그를 평점 스냅샷에 denormalize.
-- 피드·차트·프로필은 ratings의 target_* 스냅샷을 읽으므로(Spotify 재조회 X) 여기 저장해야 배지 표시 가능.
-- 기존 행은 false(백필 불가 — 재평가 시 갱신됨).
alter table public.ratings add column if not exists target_explicit boolean not null default false;
