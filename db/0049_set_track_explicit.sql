-- 2차 QA BUG-14 확장: 믹스 트랙에도 explicit(19금) 스냅샷. 곡 담을 때 저장, 상세에서 배지 표시.
-- 기존 트랙은 false(백필 불가 — 재추가 시 갱신).
alter table public.set_tracks add column if not exists explicit boolean not null default false;
