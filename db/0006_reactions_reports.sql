-- NON-23: 리뷰 반응(좋아요/싫어요) + 신고.
-- 반응/신고 쓰기·읽기는 .NET Api(postgres, BYPASSRLS)로만. 직접 PostgREST 접근은 RLS로 차단.

-- 좋아요/싫어요 — 1인 1리뷰당 1반응(중복 불가), 변경은 value update, 취소는 row 삭제.
create table if not exists public.review_reactions (
    id         uuid primary key default gen_random_uuid(),
    rating_id  uuid not null references public.ratings (id) on delete cascade,
    user_id    uuid not null references public.users (id) on delete cascade,
    value      text not null check (value in ('like', 'dislike')),
    created_at timestamptz not null default now(),
    constraint review_reactions_one_per_user unique (rating_id, user_id)
);
create index if not exists review_reactions_rating_idx on public.review_reactions (rating_id);

-- 신고 — 대상 폴리모픽(rating/user). 처리는 Admin(추후). 지금은 접수만(status=pending).
create table if not exists public.reports (
    id          uuid primary key default gen_random_uuid(),
    reporter_id uuid not null references public.users (id) on delete cascade,
    target_type text not null check (target_type in ('rating', 'user')),
    target_id   text not null,
    reason      text not null check (reason in ('not_music', 'inappropriate_profile', 'abuse', 'other')),
    detail      text,
    status      text not null default 'pending' check (status in ('pending', 'reviewed', 'resolved')),
    created_at  timestamptz not null default now(),
    constraint reports_one_per_target unique (reporter_id, target_type, target_id)
);
create index if not exists reports_status_idx on public.reports (status, created_at);

-- RLS 활성화(정책 없음 = 직접 접근 차단). .NET 은 BYPASSRLS 로 동작.
alter table public.review_reactions enable row level security;
alter table public.reports enable row level security;
