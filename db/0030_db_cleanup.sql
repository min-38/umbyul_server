-- NON-108: DB 정리 — 인덱스 보강 + 죽은 테이블 제거 + FK 보강.
-- AUDIT-2026-07-03.md §8 근거.

-- ── 1) 인덱스 보강 ───────────────────────────────────────────────
-- 피드 최신순·Rising·차트 기간 정렬이 created_at 풀스캔이던 것 → 활성 리뷰 부분 인덱스.
create index if not exists ratings_created_active_idx
    on public.ratings (created_at desc)
    where deleted_at is null;

-- 유저차트 좋아요·피드 rising(최근 24h) 집계용.
create index if not exists review_reactions_created_idx
    on public.review_reactions (created_at);

-- 신고 대상 역참조(관리자에서 특정 대상의 신고 조회).
create index if not exists reports_target_idx
    on public.reports (target_type, target_id);

-- ── 2) 죽은 테이블 제거 ──────────────────────────────────────────
-- albums / tracks 는 0001에서 만든 뒤 Api·Admin 어디서도 읽거나 쓰지 않음(캐시는
-- spotify_cache 원시 JSON + ratings 표시 메타로 대체). tracks.album_id FK 포함 통째로 제거.
drop table if exists public.tracks cascade;
drop table if exists public.albums cascade;

-- ── 3) FK 보강(감사 무결성) ─────────────────────────────────────
-- deleted_by / handled_by 는 관리자 id. admins 삭제 시 NULL 로(레코드 보존).
do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'ratings_deleted_by_fkey') then
        alter table public.ratings
            add constraint ratings_deleted_by_fkey
            foreign key (deleted_by) references public.admins (id) on delete set null;
    end if;
end $$;

do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'inquiries_handled_by_fkey') then
        alter table public.inquiries
            add constraint inquiries_handled_by_fkey
            foreign key (handled_by) references public.admins (id) on delete set null;
    end if;
end $$;
