-- NON-150: 유저별 선호 장르(추천 신호). 장르 사전(0034)에서 다대다 선택. 온보딩·설정에서 설정.
-- 추천(NON-155)은 이 선호를 우선 신호로, 리뷰 이력 장르(genre_tags)와 함께 사용.
create table if not exists public.user_genre_preferences (
    user_id    uuid not null references public.users (id) on delete cascade,
    genre_id   int  not null references public.genres (id) on delete cascade,
    created_at timestamptz not null default now(),
    primary key (user_id, genre_id)
);

create index if not exists user_genre_preferences_user_idx
    on public.user_genre_preferences (user_id);

-- 다른 소유 테이블과 동일: RLS 활성화 + 정책 없음 = PostgREST 직접 접근 차단, .NET Api(service_role)만 접근.
alter table public.user_genre_preferences enable row level security;
