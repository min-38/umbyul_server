-- 0002_supabase_rls.sql
-- ⚠️ Supabase 전용. 탈Supabase 시 이 파일은 적용하지 않거나 되돌리면 된다.
--   - auth.users FK: Supabase Auth 의 유저 테이블에 프로필을 묶는다.
--   - RLS: 우리 .NET Api 는 service_role 키로 RLS 를 우회한다. 아래 정책은
--     anon/authenticated 가 PostgREST 로 직접 접근할 때를 막는 *방어선*이다.
-- 재실행 가능(idempotent)하게 작성.

-- 1) users.id ↔ auth.users 연결 (인증 유저 삭제 시 프로필도 정리)
do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'users_id_fkey') then
        alter table public.users
            add constraint users_id_fkey foreign key (id)
            references auth.users (id) on delete cascade;
    end if;
end $$;

-- 2) RLS 활성화
alter table public.users   enable row level security;
alter table public.albums  enable row level security;
alter table public.tracks  enable row level security;
alter table public.ratings enable row level security;

-- 테이블 권한(RLS 와 별개로 필요). 정책 범위와 맞춰 명시. service_role 은 기본권한으로 전체 접근.
grant select on public.users, public.albums, public.tracks, public.ratings to anon, authenticated;
grant insert, update, delete on public.ratings to authenticated;

-- 읽기: 전면 공개 (비로그인 열람 허용). users 는 email 미보관이라 공개 안전.
drop policy if exists "public read" on public.users;
create policy "public read" on public.users for select using (true);

drop policy if exists "public read" on public.albums;
create policy "public read" on public.albums for select using (true);

drop policy if exists "public read" on public.tracks;
create policy "public read" on public.tracks for select using (true);

drop policy if exists "public read" on public.ratings;
create policy "public read" on public.ratings for select using (true);

-- 쓰기(평점·리뷰): 로그인 유저가 본인 user_id 로만.
drop policy if exists "own insert" on public.ratings;
create policy "own insert" on public.ratings
    for insert to authenticated with check (auth.uid() = user_id);

drop policy if exists "own update" on public.ratings;
create policy "own update" on public.ratings
    for update to authenticated using (auth.uid() = user_id) with check (auth.uid() = user_id);

drop policy if exists "own delete" on public.ratings;
create policy "own delete" on public.ratings
    for delete to authenticated using (auth.uid() = user_id);

-- users / albums / tracks 에는 쓰기 정책 없음 → service_role(.NET Api)만 기록 가능.
-- (users = 가입 프로비저닝, albums/tracks = Spotify 캐시)
