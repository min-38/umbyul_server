-- NON-36: 리뷰 댓글. 평면 구조(대댓글 없음). 작성/열람/삭제.
-- 쓰기·읽기는 .NET Api(postgres, BYPASSRLS)로만. 직접 PostgREST 접근은 RLS로 차단.
create table if not exists public.review_comments (
    id         uuid primary key default gen_random_uuid(),
    rating_id  uuid not null references public.ratings (id) on delete cascade,
    user_id    uuid not null references public.users (id) on delete cascade,
    body       text not null check (char_length(body) between 1 and 1000),
    created_at timestamptz not null default now()
);
create index if not exists review_comments_rating_idx on public.review_comments (rating_id, created_at);

-- RLS 활성화(정책 없음 = 직접 접근 차단). .NET 은 BYPASSRLS 로 동작.
alter table public.review_comments enable row level security;
