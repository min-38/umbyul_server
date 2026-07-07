-- 0054_constraints_indexes.sql
-- 무결성 제약·인덱스 보강 (3차 QA DB-6/10/11/12/13/14). 전부 재실행 가능.

-- DB-6: 아티스트 페이지는 target_type 없이 target_spotify_id = any(...) 로만 조회 → 기존
-- ratings_active_idx (target_type, target_spotify_id) 는 선두 컬럼 미지정이라 못 씀. 단독 부분 인덱스 추가.
create index if not exists ratings_spotify_active_idx
    on public.ratings (target_spotify_id) where deleted_at is null;

-- DB-10: user_sanctions.report_id → reports FK(무결성). admin_id 는 admins 별테이블이라 여전히 미검(문서화됨).
-- 지금까지 미강제라 레거시 dangling 값이 있을 수 있어 NOT VALID(신규/변경 행만 강제).
-- 정리 후 `alter table public.user_sanctions validate constraint user_sanctions_report_fkey;` 로 전체 검증 가능.
do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'user_sanctions_report_fkey') then
        alter table public.user_sanctions
            add constraint user_sanctions_report_fkey foreign key (report_id)
            references public.reports (id) on delete set null not valid;
    end if;
end $$;

-- DB-11: notifications.actor_id 는 0020 에서 nullable 이 됐으나 FK 는 여전히 on delete cascade →
-- 액터 계정 삭제 시 수신자의 알림까지 삭제됨. set null 로 교체(코드는 이미 actor null 을 left join 으로 허용).
do $$ begin
    if exists (select 1 from pg_constraint where conname = 'notifications_actor_id_fkey') then
        alter table public.notifications drop constraint notifications_actor_id_fkey;
    end if;
    alter table public.notifications
        add constraint notifications_actor_id_fkey foreign key (actor_id)
        references public.users (id) on delete set null;
end $$;

-- DB-12: 형제 테이블(review_comments)에 있는 본문 길이 CHECK 를 set_comments/sets 에도(직접 쓰기 대비 방어).
-- 앱은 이미 이 범위를 강제해 왔으므로 기존 행과 충돌 없음.
alter table public.set_comments drop constraint if exists set_comments_body_len;
alter table public.set_comments add constraint set_comments_body_len
    check (char_length(body) between 1 and 1000);
alter table public.sets drop constraint if exists sets_title_len;
alter table public.sets add constraint sets_title_len
    check (char_length(title) between 1 and 100);
alter table public.sets drop constraint if exists sets_note_len;
alter table public.sets add constraint sets_note_len
    check (note is null or char_length(note) <= 500);

-- DB-13: set_tracks.position 음수 방지. (unique(set_id, position) 는 현재 비트랜잭션 reorder(LOG-A-1)를
-- 깨므로 reorder 트랜잭션화 후 별도 추가 — 여기서는 보류.)
alter table public.set_tracks drop constraint if exists set_tracks_position_nonneg;
alter table public.set_tracks add constraint set_tracks_position_nonneg check (position >= 0);

-- DB-14: 중복 인덱스 제거 — PK(user_id, rating_id) 가 선두 user_id 를 이미 커버.
drop index if exists public.feed_dismissals_user_idx;
