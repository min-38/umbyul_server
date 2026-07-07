-- 0057_user_consents.sql
-- LEG-2/LEG-5 (NON-148): 약관/개인정보 동의를 게시 버전에 연결 + 재동의 근거.
-- 타입별로 사용자가 동의한 legal_versions(en 정본 기준) 버전을 append-only 로 기록 — 법적 증빙.
-- 재동의 판정(앱): 현재 en is_current 버전 id ≠ 사용자의 최신 동의 version_id → 재동의 필요.

create table if not exists public.user_consents (
    id          uuid primary key default gen_random_uuid(),
    user_id     uuid not null references public.users(id) on delete cascade,
    type        text not null check (type in ('terms', 'privacy')),
    version_id  uuid not null references public.legal_versions(id),
    accepted_at timestamptz not null default now()
);
create index if not exists user_consents_lookup_idx
    on public.user_consents (user_id, type, accepted_at desc);

alter table public.user_consents enable row level security; -- 정책 없음 = anon 직접 접근 차단. .NET service_role 로만.

-- 기존 회원 grandfather(결정 ④): 현재 en is_current 게시본에 동의한 것으로 타입당 1행씩.
-- en is_current 버전이 있는 타입만 대상. 이미 동의 행이 있으면 건너뜀(idempotent — 재실행 안전).
insert into public.user_consents (user_id, type, version_id)
select u.id, lv.type, lv.id
from public.users u
cross join (
    select type, id from public.legal_versions
    where is_current and locale = 'en' and type in ('terms', 'privacy')
) lv
where not exists (
    select 1 from public.user_consents c
    where c.user_id = u.id and c.type = lv.type
);
