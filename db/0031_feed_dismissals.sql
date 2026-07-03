-- NON-114: 피드 "관심 없음" — 유저가 특정 리뷰를 자기 피드에서 숨긴다(인스타식).
-- 반응(review_reactions)과 무관, 단순 숨김. 리뷰/유저 삭제 시 함께 정리.
create table if not exists public.feed_dismissals (
    user_id    uuid not null references public.users (id)   on delete cascade,
    rating_id  uuid not null references public.ratings (id) on delete cascade,
    created_at timestamptz not null default now(),
    primary key (user_id, rating_id)
);

-- 피드 쿼리에서 "내가 숨긴 것 제외" 조회용.
create index if not exists feed_dismissals_user_idx
    on public.feed_dismissals (user_id);
