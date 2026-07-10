-- NON-159/169: 패치노트. patch_notes(버전·상태·게시) + patch_note_locales(로케일별 본문). 공지사항 패턴 미러.
create table if not exists public.patch_notes (
    id           uuid primary key default gen_random_uuid(),
    version      text not null default '',                 -- 예: v1.01 (in_progress면 비어있을 수 있음)
    status       text not null default 'released' check (status in ('released', 'in_progress')),
    released_at  date,                                      -- released의 릴리스일. in_progress면 null 가능
    published    boolean not null default false,
    created_at   timestamptz not null default now(),
    updated_at   timestamptz not null default now(),
    updated_by   uuid                                       -- admins.id (FK 미강제)
);

create table if not exists public.patch_note_locales (
    patch_note_id uuid not null references public.patch_notes (id) on delete cascade,
    locale        text not null,       -- ko/en/ja/es; en 정본 폴백
    body          text not null default '',  -- markdown (### 기능/### UI 등 자유 소제목)
    primary key (patch_note_id, locale)
);

-- 공개 목록 조회용(게시된 것, 작업중 우선 → 최신 릴리스).
create index if not exists patch_notes_pub_idx
    on public.patch_notes (published, status, released_at desc);

-- 정책 없음 = anon 차단, .NET service_role만.
alter table public.patch_notes enable row level security;
alter table public.patch_note_locales enable row level security;
