-- NON-52: 관리자에서 Api(.NET) 시스템 로그 조회. 로그를 Postgres에 싱크(권장안 A).
-- Admin이 service_role로 직접 조회하므로 새 인프라 없이 구현. 볼륨은 min_level 설정 + 30일 파기로 관리.

create table if not exists public.app_logs (
    id         bigint generated always as identity primary key,
    level      text        not null,   -- Trace|Debug|Information|Warning|Error|Critical
    message    text        not null,
    exception  text,                    -- 예외 스택(있으면)
    category   text,                    -- ILogger 카테고리(source)
    event_id   int,                     -- ILogger EventId (있으면)
    created_at timestamptz not null default now()
);
create index if not exists app_logs_created_idx on public.app_logs (created_at desc);
-- 레벨 필터 + 최신순 조회용.
create index if not exists app_logs_level_created_idx on public.app_logs (level, created_at desc);
-- 정책 없음 = anon 차단, .NET service_role만.
alter table public.app_logs enable row level security;

-- 단일 행 설정 — DB에 저장할 최소 로그 레벨. Admin에서 변경, Api가 15초 TTL로 캐싱해 게이트.
create table if not exists public.app_log_config (
    id         int         primary key default 1,
    min_level  text        not null default 'Warning',
    updated_at timestamptz not null default now(),
    constraint app_log_config_singleton check (id = 1)
);
insert into public.app_log_config (id, min_level) values (1, 'Warning')
    on conflict (id) do nothing;
alter table public.app_log_config enable row level security;
