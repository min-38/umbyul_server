-- 0056_set_tracks_position_unique.sql
-- set_tracks의 (set_id, position) 유니크 제약 (3차 QA DB-13). 트랙 순서의 중복/갭을 스키마로 방지.
-- LOG-A-1(reorder 트랜잭션화)과 짝: reorder는 한 트랜잭션에서 position을 전량 재기록하고,
-- 이 제약은 deferrable initially deferred 라 커밋 시점에만 검증 → 행별 임시 중복을 허용한다.
-- 먼저 기존 데이터의 중복/갭 position을 set별 0-based 로 정규화한 뒤 제약을 건다(기존 위반으로 실패 방지).

with ordered as (
    select set_id, spotify_id,
           row_number() over (partition by set_id order by position, spotify_id) - 1 as rn
    from public.set_tracks
)
update public.set_tracks t
set position = o.rn
from ordered o
where o.set_id = t.set_id and o.spotify_id = t.spotify_id and t.position <> o.rn;

alter table public.set_tracks drop constraint if exists set_tracks_pos_unique;
alter table public.set_tracks
    add constraint set_tracks_pos_unique unique (set_id, position) deferrable initially deferred;
