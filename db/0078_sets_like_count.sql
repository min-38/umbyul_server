-- QA6-13 (NON-205): 믹스 인기 정렬(order by like_count)이 like_count 상관 서브쿼리를 전체 세트에 대해
-- 정렬 전 실행 → sets.like_count 비정규화 후 컬럼으로 정렬. 트리거로 자동 유지(코드가 컬럼을 정렬에만 참조,
-- 미적용 시 서브쿼리 정렬로 폴백 → graceful).
alter table public.sets add column if not exists like_count integer not null default 0;

update public.sets s set like_count = (select count(*) from public.set_likes l where l.set_id = s.id);

create or replace function public.sync_set_like_count() returns trigger as $$
declare sid uuid := coalesce(new.set_id, old.set_id);
begin
    update public.sets set like_count = (select count(*) from public.set_likes where set_id = sid) where id = sid;
    return null;
end;
$$ language plpgsql;

drop trigger if exists sync_set_like_count_trg on public.set_likes;
create trigger sync_set_like_count_trg after insert or delete on public.set_likes
    for each row execute function public.sync_set_like_count();
