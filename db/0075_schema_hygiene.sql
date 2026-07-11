-- QA6-6 (NON-198): 스키마 하이진 일괄.
-- ⚠️ 코드(view_count 미참조·announcement_views count(*) 읽기)가 배포된 뒤 적용 권장(view_count drop 때문).

-- 1) admins FK 일관성 — 0030이 ratings/inquiries에만 추가한 비대칭 해소. 대상 컬럼 전부 nullable uuid.
--    admin은 비활성화만 하고 hard delete 안 하나 on delete set null로 방어. drop-if-exists로 재실행 가능.
--    validate는 admin이 hard delete 되지 않음(=orphan 없음)을 전제 — 실패 시 해당 컬럼의 비-admin 값 확인 후 재적용.
alter table public.sets            drop constraint if exists sets_deleted_by_admin_fkey;
alter table public.sets            add  constraint sets_deleted_by_admin_fkey            foreign key (deleted_by)   references public.admins (id) on delete set null not valid;
alter table public.set_comments    drop constraint if exists set_comments_deleted_by_admin_fkey;
alter table public.set_comments    add  constraint set_comments_deleted_by_admin_fkey    foreign key (deleted_by)   references public.admins (id) on delete set null not valid;
alter table public.legal_documents drop constraint if exists legal_documents_updated_by_admin_fkey;
alter table public.legal_documents add  constraint legal_documents_updated_by_admin_fkey foreign key (updated_by)   references public.admins (id) on delete set null not valid;
alter table public.legal_versions  drop constraint if exists legal_versions_published_by_admin_fkey;
alter table public.legal_versions  add  constraint legal_versions_published_by_admin_fkey foreign key (published_by) references public.admins (id) on delete set null not valid;
alter table public.faq_items       drop constraint if exists faq_items_updated_by_admin_fkey;
alter table public.faq_items       add  constraint faq_items_updated_by_admin_fkey       foreign key (updated_by)   references public.admins (id) on delete set null not valid;
alter table public.announcements   drop constraint if exists announcements_updated_by_admin_fkey;
alter table public.announcements   add  constraint announcements_updated_by_admin_fkey   foreign key (updated_by)   references public.admins (id) on delete set null not valid;
alter table public.patch_notes     drop constraint if exists patch_notes_updated_by_admin_fkey;
alter table public.patch_notes     add  constraint patch_notes_updated_by_admin_fkey     foreign key (updated_by)   references public.admins (id) on delete set null not valid;
alter table public.user_sanctions  drop constraint if exists user_sanctions_admin_id_fkey;
alter table public.user_sanctions  add  constraint user_sanctions_admin_id_fkey          foreign key (admin_id)     references public.admins (id) on delete set null not valid;

alter table public.sets            validate constraint sets_deleted_by_admin_fkey;
alter table public.set_comments    validate constraint set_comments_deleted_by_admin_fkey;
alter table public.legal_documents validate constraint legal_documents_updated_by_admin_fkey;
alter table public.legal_versions  validate constraint legal_versions_published_by_admin_fkey;
alter table public.faq_items       validate constraint faq_items_updated_by_admin_fkey;
alter table public.announcements   validate constraint announcements_updated_by_admin_fkey;
alter table public.patch_notes     validate constraint patch_notes_updated_by_admin_fkey;
alter table public.user_sanctions  validate constraint user_sanctions_admin_id_fkey;

-- 2) ratings.review 길이 CHECK — 형제 콘텐츠 컬럼(review_comments.body 등, 0054)과 정합. 신규만 검사(not valid).
alter table public.ratings drop constraint if exists ratings_review_length;
alter table public.ratings add  constraint ratings_review_length check (review is null or char_length(review) <= 5000) not valid;

-- 3) ratings.updated_at — 내부 감사용(사용자 비노출; 사용자에겐 created_at만 노출). 재평가(upsert ON CONFLICT DO UPDATE) 시각 추적.
--    graceful degradation: 코드가 컬럼을 참조하지 않고 트리거로 자동 유지 → 마이그레이션 미적용 스키마에서도 upsert 안전.
alter table public.ratings add column if not exists updated_at timestamptz;

create or replace function public.ratings_set_updated_at() returns trigger as $$
begin
    new.updated_at = now();
    return new;
end;
$$ language plpgsql;

drop trigger if exists ratings_set_updated_at_trg on public.ratings;
create trigger ratings_set_updated_at_trg before insert or update on public.ratings
    for each row execute function public.ratings_set_updated_at();

-- 기존 행 백필(최초 시각). 이후 쓰기부터는 트리거가 now()로 갱신.
update public.ratings set updated_at = created_at where updated_at is null;

-- 4) announcements.view_count 제거 — announcement_views count(*)를 단일 진실원천으로(0066 카운터의 비트랜잭션
--    이중 기록 드리프트 제거). 코드는 이미 count(*)로 읽고 view_count를 참조하지 않음(graceful).
alter table public.announcements drop column if exists view_count;
