-- QA6-7 (NON-199): /feed 의 review_reactions 전면 해시 집계(rx CTE, WHERE 없음, 매 요청 전체 테이블) 제거를 위해
-- 좋아요/싫어요 수를 ratings 에 비정규화하고 트리거로 자동 유지.
-- graceful degradation: 코드(ReactionEndpoints)는 컬럼을 참조하지 않고 트리거가 유지 → 미적용 스키마에서도 반응 토글 안전.
--                       피드는 이 컬럼을 참조하되 미적용 시(42703) 기존 rx 쿼리로 폴백(코드에서 처리).
alter table public.ratings add column if not exists likes_count    integer not null default 0;
alter table public.ratings add column if not exists dislikes_count integer not null default 0;

-- 백필
update public.ratings r set
    likes_count    = coalesce(c.likes, 0),
    dislikes_count = coalesce(c.dislikes, 0)
from (
    select rating_id,
           count(*) filter (where value = 'like')    likes,
           count(*) filter (where value = 'dislike') dislikes
    from public.review_reactions group by rating_id
) c
where c.rating_id = r.id;

-- 트리거: 반응 insert/delete/update 시 해당 rating 카운터 재계산(정확·자기유지).
create or replace function public.sync_review_reaction_counts() returns trigger as $$
declare rid uuid := coalesce(new.rating_id, old.rating_id);
begin
    update public.ratings set
        likes_count    = (select count(*) from public.review_reactions where rating_id = rid and value = 'like'),
        dislikes_count = (select count(*) from public.review_reactions where rating_id = rid and value = 'dislike')
    where id = rid;
    return null;
end;
$$ language plpgsql;

drop trigger if exists sync_review_reaction_counts_trg on public.review_reactions;
create trigger sync_review_reaction_counts_trg
    after insert or delete or update on public.review_reactions
    for each row execute function public.sync_review_reaction_counts();
