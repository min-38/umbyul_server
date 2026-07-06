-- 0038_comment_threads.sql (NON-40): 대댓글(2레벨 flatten) + 댓글 좋아요 + 댓글 신고 + 소프트삭제.

-- 1) review_comments: 부모 참조(2레벨 — 답글의 답글도 최상위 댓글에 평평하게) + 소프트삭제(모더레이션 블라인드).
alter table public.review_comments
    add column if not exists parent_id      uuid references public.review_comments (id) on delete cascade,
    add column if not exists deleted_at      timestamptz,
    add column if not exists deleted_by      uuid,
    add column if not exists deleted_reason  text;

create index if not exists review_comments_parent_idx on public.review_comments (parent_id);

-- 2) 댓글 좋아요(좋아요만 — 싫어요 없음, NON-40 결정). 1인 1좋아요.
create table if not exists public.comment_likes (
    comment_id uuid not null references public.review_comments (id) on delete cascade,
    user_id    uuid not null references public.users (id) on delete cascade,
    created_at timestamptz not null default now(),
    primary key (comment_id, user_id)
);
create index if not exists comment_likes_comment_idx on public.comment_likes (comment_id);
alter table public.comment_likes enable row level security; -- 정책 없음 = 직접 접근 차단, 서버(service_role)만

-- 3) 신고 대상에 comment 추가(0006의 인라인 CHECK 이름 = reports_target_type_check).
alter table public.reports drop constraint if exists reports_target_type_check;
alter table public.reports add constraint reports_target_type_check
    check (target_type in ('rating', 'user', 'comment'));
