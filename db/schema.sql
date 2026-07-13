-- schema.sql
-- UmByul (music rating service) — 최종 상태 통합 스키마.
-- server/db/0001..0081 마이그레이션을 하나로 접어 fresh Supabase Postgres 에서 재현.
-- 원본 스타일 유지: 한국어 주석, `if not exists`. NOT VALID 로 추가됐던 제약은
-- 신규 DB(데이터 없음)에서는 그냥 유효한 인라인 CHECK/FK 로 접음(아래 CONSOLIDATION NOTES 참고).
-- 앱 데이터 흐름은 .NET Api(service_role, BYPASSRLS). RLS 는 anon/authenticated PostgREST 직접접근 방어선.

-- ============================================================================
-- EXTENSIONS
-- ============================================================================
create extension if not exists pg_trgm;    -- 0077: users.username trigram(GIN) 검색/멘션 자동완성
-- gen_random_uuid() 는 PostgreSQL 13+ core(Supabase 기본 제공). 구버전이면 아래 주석 해제:
-- create extension if not exists pgcrypto;

-- ============================================================================
-- TABLES  (의존 순서: 참조되는 테이블 먼저)
-- ============================================================================

-- users: 앱 프로필. id 는 인증 유저 id(가입 프로비저닝 시 주입). Supabase auth.users 와 연결.
create table if not exists public.users (
    id                      uuid primary key,
    username                text not null,
    country                 text,                       -- ISO 3166-1 alpha-2 (온보딩 전 NULL 가능)
    avatar_url              text,
    is_artist               boolean not null default false,
    created_at              timestamptz not null default now(),
    terms_accepted_at       timestamptz,                -- 0003: 약관·개인정보 동의 시각(법적 증빙)
    birth_date              date,                       -- 0004: 만 14세 미만 가입 차단 근거
    gender                  text,                       -- 0004
    locale                  text,                       -- 0011/0037: ko|en|ja|es
    suspended_until         timestamptz,                -- 0018: 제재 집행용 비정규화
    banned                  boolean not null default false,  -- 0018
    demographics_updated_at timestamptz,                -- 0058: 국가/성별 마지막 변경(쿨다운)
    hide_level              boolean not null default false,  -- 0079: 레벨 뱃지 공개 옵트아웃
    constraint users_username_format check (
        char_length(username) between 2 and 30
        and username ~ '^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$'
    ),
    constraint users_country_format check (country is null or country ~ '^[A-Z]{2}$'),
    constraint users_gender_check   check (gender is null or gender in ('male', 'female', 'other', 'undisclosed')),
    constraint users_locale_check   check (locale is null or locale in ('ko', 'en', 'ja', 'es')),
    -- 0002: 인증 유저 삭제 시 프로필 정리 (Supabase 전용)
    constraint users_id_fkey foreign key (id) references auth.users (id) on delete cascade
);

-- admins: 관리자 계정(공개 users 와 완전 분리). 암호는 BCrypt 해시.
create table if not exists public.admins (
    id            uuid primary key default gen_random_uuid(),
    username      text not null,
    password_hash text not null,
    is_active     boolean not null default true,        -- 0033: 비활성화 시 로그인 차단
    created_at    timestamptz not null default now()
);

-- admin_actions: 관리자 감사 로그. admin_username 비정규화(관리자 삭제돼도 이력 유지).
create table if not exists public.admin_actions (
    id             bigint generated always as identity primary key,
    admin_id       uuid references public.admins (id) on delete set null,
    admin_username text not null,
    action         text not null,
    target         text,
    detail         text,
    created_at     timestamptz not null default now()
);

-- ratings: 평점·리뷰. target 폴리모픽(album/track). target_id=ISRC/UPC(집계 앵커), target_spotify_id=표시용.
create table if not exists public.ratings (
    id               uuid primary key default gen_random_uuid(),
    user_id          uuid not null references public.users (id) on delete cascade,
    target_type      text not null check (target_type in ('album', 'track')),
    target_id        text not null,             -- 0005: ISRC(track)/UPC(album), 없으면 spotify_id 폴백
    score            numeric(2, 1) not null check (
        score >= 0.5 and score <= 5.0 and (score * 2) = floor(score * 2)   -- 0.5 단위
    ),
    review           text,
    created_at       timestamptz not null default now(),
    target_spotify_id text,                      -- 0007: 표시용 라이브 조회 포인터
    target_name      text,                       -- 0013: 표시 메타 캐시
    target_artist    text,                       -- 0013
    target_image_url text,                       -- 0013
    target_artists   jsonb,                      -- 0029: [{Id,Name}] 개별 아티스트 링크
    target_explicit  boolean not null default false,  -- 0048: 19금 배지 스냅샷
    deleted_at       timestamptz,                -- 0017: 소프트 삭제
    deleted_by       uuid,                       -- 0017/0030: admins.id
    deleted_reason   text,                       -- 0017
    updated_at       timestamptz,                -- 0075: 재평가 시각(내부 감사, 트리거 유지)
    likes_count      integer not null default 0, -- 0076: 반응 카운터(트리거 유지)
    dislikes_count   integer not null default 0, -- 0076
    constraint ratings_one_per_target unique (user_id, target_type, target_id),   -- 1인 1평점/대상
    constraint ratings_review_length check (review is null or char_length(review) <= 5000),  -- 0075
    constraint ratings_deleted_by_fkey foreign key (deleted_by)                   -- 0030
        references public.admins (id) on delete set null
);
comment on column public.ratings.target_id is
    'ISRC(track) / UPC(album). 없으면 spotify_id 폴백.';   -- 0005

-- review_reactions: 리뷰 좋아요/싫어요. 1인 1리뷰당 1반응.
create table if not exists public.review_reactions (
    id         uuid primary key default gen_random_uuid(),
    rating_id  uuid not null references public.ratings (id) on delete cascade,
    user_id    uuid not null references public.users (id) on delete cascade,
    value      text not null check (value in ('like', 'dislike')),
    created_at timestamptz not null default now(),
    constraint review_reactions_one_per_user unique (rating_id, user_id)
);

-- reports: 신고. 대상 폴리모픽(rating/user/comment/set_comment/set), 모두 uuid.
create table if not exists public.reports (
    id          uuid primary key default gen_random_uuid(),
    reporter_id uuid not null references public.users (id) on delete cascade,
    target_type text not null check (target_type in ('rating', 'user', 'comment', 'set_comment', 'set')),  -- 0006/0038/0045/0046
    target_id   text not null,
    reason      text not null check (reason in ('not_music', 'inappropriate_profile', 'abuse', 'other')),
    detail      text,
    status      text not null default 'pending' check (status in ('pending', 'reviewed', 'resolved')),
    created_at  timestamptz not null default now(),
    -- 0052/0073: target_id uuid 형식 강제(원본은 NOT VALID → fresh DB 에선 유효 CHECK 로 접음)
    constraint reports_target_id_uuid check (
        target_id ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
    )
    -- 0070: reports_one_per_target(영구 unique) 제거 → 아래 부분 유니크 인덱스로 대체
);

-- follows: 팔로우.
create table if not exists public.follows (
    follower_id  uuid not null references public.users (id) on delete cascade,
    following_id uuid not null references public.users (id) on delete cascade,
    created_at   timestamptz not null default now(),
    primary key (follower_id, following_id),
    constraint follows_no_self check (follower_id <> following_id)
);

-- notifications: 알림. actor_id nullable(시스템 발신), 삭제 시 set null.
create table if not exists public.notifications (
    id           uuid primary key default gen_random_uuid(),
    recipient_id uuid not null references public.users (id) on delete cascade,
    actor_id     uuid references public.users (id) on delete set null,   -- 0020 nullable / 0054 set null
    type         text not null check (type in ('follow', 'review_like', 'warning', 'mention', 'announcement')),  -- 0009/0020/0039/0065
    target_id    text,
    read_at      timestamptz,
    created_at   timestamptz not null default now(),
    detail       text                                                     -- 0020
);

-- notification_prefs: 유저별 알림 설정. 없으면 전부 on.
create table if not exists public.notification_prefs (
    user_id     uuid primary key references public.users (id) on delete cascade,
    master      boolean not null default true,
    follow      boolean not null default true,
    review_like boolean not null default true,
    updated_at  timestamptz not null default now(),
    mention     boolean not null default true       -- 0039
);

-- review_comments: 리뷰 댓글. 2레벨 flatten(parent_id) + 소프트삭제.
create table if not exists public.review_comments (
    id             uuid primary key default gen_random_uuid(),
    rating_id      uuid not null references public.ratings (id) on delete cascade,
    user_id        uuid not null references public.users (id) on delete cascade,
    body           text not null check (char_length(body) between 1 and 1000),
    created_at     timestamptz not null default now(),
    parent_id      uuid references public.review_comments (id) on delete cascade,   -- 0038
    deleted_at     timestamptz,                     -- 0038
    deleted_by     uuid,                            -- 0038/0071: 관리자 삭제만 admins.id
    deleted_reason text,                            -- 0038
    edited_at      timestamptz,                     -- 0047
    constraint review_comments_deleted_by_fkey foreign key (deleted_by)             -- 0071
        references public.admins (id) on delete set null
);

-- spotify_cache: Spotify 원시 응답 캐시(429 완화).
create table if not exists public.spotify_cache (
    cache_key  text primary key,
    payload    text not null,
    fetched_at timestamptz not null default now()
);

-- spotify_status: Spotify 레이트리밋 상태(단일 행).
create table if not exists public.spotify_status (
    id                  int primary key default 1,
    blocked_until       timestamptz,
    retry_after_seconds int,
    updated_at          timestamptz,
    constraint spotify_status_single check (id = 1)
);

-- user_sanctions: 유저 제재 이력. admin_id/report_id 는 admins/reports 참조(set null).
create table if not exists public.user_sanctions (
    id             bigint generated always as identity primary key,
    user_id        uuid not null references public.users (id) on delete cascade,
    type           text not null check (type in ('warning', 'suspension', 'ban', 'unban')),
    until          timestamptz,
    reason         text,
    admin_id       uuid,                            -- 0075 FK
    admin_username text not null,
    report_id      uuid,                            -- 0054 FK
    created_at     timestamptz not null default now(),
    constraint user_sanctions_admin_id_fkey  foreign key (admin_id)  references public.admins (id)  on delete set null,   -- 0075
    constraint user_sanctions_report_fkey    foreign key (report_id) references public.reports (id) on delete set null    -- 0054/0073
);

-- legal_documents: 관리자 작성 약관/개인정보 초안(편집본). 로케일별 1개.
create table if not exists public.legal_documents (
    id         uuid primary key default gen_random_uuid(),
    type       text not null check (type in ('terms', 'privacy')),
    locale     text not null,
    content    text not null default '',
    published  boolean not null default false,
    version        text,                             -- 초안에 임시 저장하는 버전 라벨(게시 시 legal_versions로 확정)
    effective_date date,                             -- 초안에 임시 저장하는 시행일
    updated_at timestamptz not null default now(),
    updated_by uuid,                                 -- 0075 FK
    unique (type, locale),
    constraint legal_documents_updated_by_admin_fkey foreign key (updated_by)
        references public.admins (id) on delete set null
);

-- legal_versions: 약관/개인정보 게시 버전 이력(불변 스냅샷).
create table if not exists public.legal_versions (
    id             uuid primary key default gen_random_uuid(),
    type           text not null check (type in ('terms', 'privacy')),
    locale         text not null,
    content        text not null,
    published_at   timestamptz not null default now(),
    published_by   uuid,                             -- 0075 FK
    version        text,                             -- 0024: 사람이 지정하는 버전 라벨
    is_current     boolean not null default false,   -- 0025
    effective_date date,                             -- 0026: 시행일
    constraint legal_versions_published_by_admin_fkey foreign key (published_by)
        references public.admins (id) on delete set null
);

-- faq_items: 관리자 편집 FAQ.
create table if not exists public.faq_items (
    id         uuid primary key default gen_random_uuid(),
    locale     text not null,
    category   text not null default '',
    question   text not null,
    answer     text not null default '',
    sort_order int  not null default 0,
    published  boolean not null default false,
    updated_at timestamptz not null default now(),
    updated_by uuid,                                 -- 0075 FK
    constraint faq_items_updated_by_admin_fkey foreign key (updated_by)
        references public.admins (id) on delete set null
);

-- inquiries: 문의(Contact). 공개 접수 → 관리자 처리.
create table if not exists public.inquiries (
    id         uuid primary key default gen_random_uuid(),
    category   text not null default '',
    email      text not null,
    title      text not null,
    content    text not null,
    handled    boolean not null default false,
    handled_at timestamptz,
    handled_by uuid,                                 -- 0030 FK
    created_at timestamptz not null default now(),
    constraint inquiries_handled_by_fkey foreign key (handled_by)
        references public.admins (id) on delete set null
);

-- genres: 큐레이트 장르 사전. parent_id 로 상위/서브. name 은 영어 단일.
create table if not exists public.genres (
    id         serial primary key,
    slug       text not null unique,
    name       text not null,                        -- 0034 name_en → 0036 rename to name (name_ko drop)
    parent_id  int references public.genres (id),
    sort_order int not null default 0
);

-- genre_tags: 유저 장르 태깅(투표).
create table if not exists public.genre_tags (
    user_id           uuid not null references public.users (id) on delete cascade,
    target_type       text not null check (target_type in ('track', 'album')),
    target_spotify_id text not null,
    genre_id          int not null references public.genres (id) on delete cascade,
    created_at        timestamptz not null default now(),
    primary key (user_id, target_type, target_spotify_id, genre_id)
);

-- comment_likes: 댓글 좋아요(좋아요만).
create table if not exists public.comment_likes (
    comment_id uuid not null references public.review_comments (id) on delete cascade,
    user_id    uuid not null references public.users (id) on delete cascade,
    created_at timestamptz not null default now(),
    primary key (comment_id, user_id)
);

-- mention_mutes: 특정 트랙/앨범 멘션 알림 뮤트.
create table if not exists public.mention_mutes (
    user_id           uuid        not null references public.users (id) on delete cascade,
    target_type       text        not null check (target_type in ('track', 'album')),
    target_spotify_id text        not null,
    created_at        timestamptz not null default now(),
    primary key (user_id, target_type, target_spotify_id)
);

-- feed_dismissals: 피드 "관심 없음"(리뷰 숨김).
create table if not exists public.feed_dismissals (
    user_id    uuid not null references public.users (id)   on delete cascade,
    rating_id  uuid not null references public.ratings (id) on delete cascade,
    created_at timestamptz not null default now(),
    primary key (user_id, rating_id)
);

-- blocks: 유저 차단(상호 판정).
create table if not exists public.blocks (
    blocker_id uuid not null references public.users (id) on delete cascade,
    blocked_id uuid not null references public.users (id) on delete cascade,
    created_at timestamptz not null default now(),
    primary key (blocker_id, blocked_id),
    constraint blocks_no_self check (blocker_id <> blocked_id)
);

-- sets: DJ 세트(유저 큐레이션). 소프트삭제 + 좋아요 카운터.
create table if not exists public.sets (
    id             uuid        primary key default gen_random_uuid(),
    owner_id       uuid        not null references public.users (id) on delete cascade,
    title          text        not null,
    note           text,
    listen_url     text,
    created_at     timestamptz not null default now(),
    updated_at     timestamptz not null default now(),   -- 0043
    deleted_at     timestamptz,                          -- 0055
    deleted_by     uuid,                                 -- 0055/0075: admins.id
    deleted_reason text,                                 -- 0055
    like_count     integer not null default 0,           -- 0078: 트리거 유지
    constraint sets_title_len check (char_length(title) between 1 and 100),   -- 0054
    constraint sets_note_len  check (note is null or char_length(note) <= 500),  -- 0054
    constraint sets_deleted_by_admin_fkey foreign key (deleted_by)            -- 0075
        references public.admins (id) on delete set null
);

-- set_tracks: 세트 트랙(denormalized). youtube_url 은 0041 추가→0064 제거로 OMIT.
create table if not exists public.set_tracks (
    set_id     uuid not null references public.sets (id) on delete cascade,
    spotify_id text not null,
    position   int  not null,
    isrc       text,
    name       text not null,
    artist     text not null,
    image_url  text,
    album_id   text,                                 -- 0042
    album_name text,                                 -- 0042
    artists    jsonb,                                -- 0042: [{id,name}]
    explicit   boolean not null default false,       -- 0049
    primary key (set_id, spotify_id),
    constraint set_tracks_position_nonneg check (position >= 0),   -- 0054
    -- 0056: reorder 트랜잭션과 짝. 커밋 시점에만 검증.
    constraint set_tracks_pos_unique unique (set_id, position) deferrable initially deferred
);

-- set_comments: 믹스 댓글(평면). 소프트삭제.
create table if not exists public.set_comments (
    id             uuid        primary key default gen_random_uuid(),
    set_id         uuid        not null references public.sets (id) on delete cascade,
    user_id        uuid        not null references public.users (id) on delete cascade,
    body           text        not null,
    created_at     timestamptz not null default now(),
    deleted_at     timestamptz,
    edited_at      timestamptz,                       -- 0047
    deleted_by     uuid,                              -- 0055/0075: admins.id
    deleted_reason text,                              -- 0055
    constraint set_comments_body_len check (char_length(body) between 1 and 1000),   -- 0054
    constraint set_comments_deleted_by_admin_fkey foreign key (deleted_by)           -- 0075
        references public.admins (id) on delete set null
);

-- set_likes: 믹스 좋아요.
create table if not exists public.set_likes (
    set_id     uuid        not null references public.sets (id) on delete cascade,
    user_id    uuid        not null references public.users (id) on delete cascade,
    created_at timestamptz not null default now(),
    primary key (set_id, user_id)
);

-- user_consents: 약관/개인정보 동의를 게시 버전에 연결(append-only 법적 증빙).
create table if not exists public.user_consents (
    id          uuid primary key default gen_random_uuid(),
    user_id     uuid not null references public.users (id) on delete cascade,
    type        text not null check (type in ('terms', 'privacy')),
    version_id  uuid not null references public.legal_versions (id),
    accepted_at timestamptz not null default now()
);

-- user_genre_preferences: 유저 선호 장르(추천 신호).
create table if not exists public.user_genre_preferences (
    user_id    uuid not null references public.users (id) on delete cascade,
    genre_id   int  not null references public.genres (id) on delete cascade,
    created_at timestamptz not null default now(),
    primary key (user_id, genre_id)
);

-- daily_picks: Discover "오늘의 음악"(운영자 큐레이션). youtube_url 은 0062 추가→0063 제거로 OMIT.
create table if not exists public.daily_picks (
    id                uuid primary key default gen_random_uuid(),
    target_type       text not null check (target_type in ('track', 'album')),
    target_spotify_id text not null,
    note              text,
    pick_date         date not null unique,
    created_at        timestamptz not null default now()
);

-- target_youtube_links: 곡/앨범 음악 고유 ID(ISRC/UPC) 귀속 YouTube 링크.
create table if not exists public.target_youtube_links (
    target_type text not null check (target_type in ('track', 'album')),
    target_id   text not null,
    youtube_url text not null,
    updated_at  timestamptz not null default now(),
    primary key (target_type, target_id)
);

-- announcements: 공지사항. view_count 는 0066 추가→0075 제거로 OMIT(announcement_views count(*) 사용).
create table if not exists public.announcements (
    id           uuid primary key default gen_random_uuid(),
    published    boolean not null default false,
    published_at timestamptz,
    notified_at  timestamptz,
    created_at   timestamptz not null default now(),
    updated_at   timestamptz not null default now(),
    updated_by   uuid,                               -- 0075 FK
    constraint announcements_updated_by_admin_fkey foreign key (updated_by)
        references public.admins (id) on delete set null
);

-- announcement_locales: 공지 로케일별 본문.
create table if not exists public.announcement_locales (
    announcement_id uuid not null references public.announcements (id) on delete cascade,
    locale          text not null,
    title           text not null default '',
    body            text not null default '',
    primary key (announcement_id, locale)
);

-- announcement_views: 공지 조회 중복 제거(뷰어당 1회).
create table if not exists public.announcement_views (
    announcement_id uuid not null references public.announcements (id) on delete cascade,
    viewer          text not null,        -- 'u:{userId}' | 'ip:{hash}'
    created_at      timestamptz not null default now(),
    primary key (announcement_id, viewer)
);

-- patch_notes: 패치노트.
create table if not exists public.patch_notes (
    id          uuid primary key default gen_random_uuid(),
    version     text not null default '',
    status      text not null default 'released' check (status in ('released', 'in_progress')),
    released_at date,
    published   boolean not null default false,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),
    updated_by  uuid,                                -- 0075 FK
    constraint patch_notes_updated_by_admin_fkey foreign key (updated_by)
        references public.admins (id) on delete set null
);

-- patch_note_locales: 패치노트 로케일별 본문.
create table if not exists public.patch_note_locales (
    patch_note_id uuid not null references public.patch_notes (id) on delete cascade,
    locale        text not null,
    body          text not null default '',
    primary key (patch_note_id, locale)
);

-- app_logs: Api(.NET) 시스템 로그 Postgres 싱크.
create table if not exists public.app_logs (
    id         bigint generated always as identity primary key,
    level      text        not null,
    message    text        not null,
    exception  text,
    category   text,
    event_id   int,
    created_at timestamptz not null default now()
);

-- app_log_config: DB 저장 최소 로그 레벨(단일 행).
create table if not exists public.app_log_config (
    id         int         primary key default 1,
    min_level  text        not null default 'Warning',
    updated_at timestamptz not null default now(),
    constraint app_log_config_singleton check (id = 1)
);

-- ============================================================================
-- INDEXES
-- ============================================================================

-- users
create unique index if not exists users_username_lower_key on public.users (lower(username));
create index if not exists users_username_trgm_idx on public.users using gin (username gin_trgm_ops);   -- 0077

-- admins / admin_actions
create unique index if not exists admins_username_lower_key on public.admins (lower(username));
create index if not exists admin_actions_created_idx on public.admin_actions (created_at desc);

-- ratings
create index if not exists ratings_target_idx              on public.ratings (target_type, target_id);
create index if not exists ratings_active_idx              on public.ratings (target_type, target_spotify_id) where deleted_at is null;   -- 0017
create index if not exists ratings_created_active_idx      on public.ratings (created_at desc)                where deleted_at is null;   -- 0030
create index if not exists ratings_spotify_active_idx      on public.ratings (target_spotify_id)              where deleted_at is null;   -- 0054
create index if not exists ratings_user_target_active_idx  on public.ratings (user_id, target_type, target_spotify_id) where deleted_at is null;  -- 0060
create index if not exists ratings_target_artists_gin      on public.ratings using gin (target_artists);      -- 0069
create index if not exists ratings_anchor_active_idx       on public.ratings (target_type, target_id)         where deleted_at is null;   -- 0072

-- review_reactions
create index if not exists review_reactions_rating_idx  on public.review_reactions (rating_id);
create index if not exists review_reactions_created_idx on public.review_reactions (created_at);   -- 0030

-- reports  (0070: reports_one_per_target 제거 → 열린 신고만 유니크)
create index if not exists reports_status_idx on public.reports (status, created_at);
create index if not exists reports_target_idx on public.reports (target_type, target_id);          -- 0030
create unique index if not exists reports_one_open_per_target
    on public.reports (reporter_id, target_type, target_id) where status = 'pending';               -- 0070

-- follows
create index if not exists follows_following_idx on public.follows (following_id);
create index if not exists follows_created_idx    on public.follows (created_at);                    -- 0077

-- notifications
create index if not exists notifications_recipient_idx on public.notifications (recipient_id, created_at desc);
create index if not exists notifications_unread_idx    on public.notifications (recipient_id) where read_at is null;   -- 0077

-- review_comments
create index if not exists review_comments_rating_idx on public.review_comments (rating_id, created_at);
create index if not exists review_comments_parent_idx on public.review_comments (parent_id);        -- 0038

-- spotify_cache
create index if not exists spotify_cache_fetched_idx on public.spotify_cache (fetched_at);

-- user_sanctions
create index if not exists user_sanctions_user_idx on public.user_sanctions (user_id, created_at desc);

-- legal_versions
create index if not exists legal_versions_lookup_idx on public.legal_versions (type, locale, published_at desc);
create unique index if not exists legal_versions_one_current on public.legal_versions (type, locale) where is_current;   -- 0025

-- faq_items / inquiries
create index if not exists faq_items_public_idx  on public.faq_items (locale, published, sort_order);
create index if not exists inquiries_pending_idx on public.inquiries (handled, created_at desc);

-- genre_tags
create index if not exists genre_tags_target_idx  on public.genre_tags (target_type, target_spotify_id);
create index if not exists genre_tags_spotify_idx on public.genre_tags (target_spotify_id);          -- 0077

-- comment_likes
create index if not exists comment_likes_comment_idx on public.comment_likes (comment_id);

-- (feed_dismissals_user_idx: 0031 생성 → 0054 삭제(PK가 커버), OMIT)

-- blocks
create index if not exists blocks_blocked_idx on public.blocks (blocked_id);

-- sets
create index if not exists sets_owner_idx          on public.sets (owner_id, created_at desc);
create index if not exists sets_active_created_idx on public.sets (created_at desc) where deleted_at is null;   -- 0077

-- set_comments
create index if not exists set_comments_set_idx on public.set_comments (set_id, created_at desc);

-- user_consents / user_genre_preferences
create index if not exists user_consents_lookup_idx         on public.user_consents (user_id, type, accepted_at desc);
create index if not exists user_genre_preferences_user_idx  on public.user_genre_preferences (user_id);

-- daily_picks
create index if not exists daily_picks_date_idx on public.daily_picks (pick_date desc);

-- announcements / patch_notes
create index if not exists announcements_published_idx on public.announcements (published, published_at desc);
create index if not exists patch_notes_pub_idx         on public.patch_notes (published, status, released_at desc);

-- app_logs
create index if not exists app_logs_created_idx       on public.app_logs (created_at desc);
create index if not exists app_logs_level_created_idx on public.app_logs (level, created_at desc);

-- ============================================================================
-- RLS  (앱은 service_role/BYPASSRLS. 아래는 anon/authenticated PostgREST 방어선.)
-- ============================================================================
alter table public.users                   enable row level security;
alter table public.admins                  enable row level security;
alter table public.admin_actions           enable row level security;
alter table public.ratings                 enable row level security;
alter table public.review_reactions        enable row level security;
alter table public.reports                 enable row level security;
alter table public.follows                 enable row level security;
alter table public.notifications           enable row level security;
alter table public.notification_prefs      enable row level security;
alter table public.review_comments         enable row level security;
alter table public.spotify_cache           enable row level security;
alter table public.spotify_status          enable row level security;
alter table public.user_sanctions          enable row level security;   -- 0050
alter table public.legal_documents         enable row level security;
alter table public.legal_versions          enable row level security;
alter table public.faq_items               enable row level security;
alter table public.inquiries               enable row level security;
alter table public.genres                  enable row level security;   -- 0050
alter table public.genre_tags              enable row level security;   -- 0050
alter table public.comment_likes           enable row level security;
alter table public.mention_mutes           enable row level security;
alter table public.feed_dismissals         enable row level security;   -- 0050
alter table public.blocks                  enable row level security;   -- 0050
alter table public.sets                    enable row level security;
alter table public.set_tracks              enable row level security;
alter table public.set_comments            enable row level security;
alter table public.set_likes               enable row level security;
alter table public.user_consents           enable row level security;
alter table public.user_genre_preferences  enable row level security;
alter table public.daily_picks             enable row level security;
alter table public.target_youtube_links    enable row level security;
alter table public.announcements           enable row level security;
alter table public.announcement_locales    enable row level security;
alter table public.announcement_views      enable row level security;
alter table public.patch_notes             enable row level security;
alter table public.patch_note_locales      enable row level security;
alter table public.app_logs                enable row level security;
alter table public.app_log_config          enable row level security;

-- 정책이 있는 테이블 = users(공개읽기), ratings(공개읽기, 삭제 제외).
-- 그 외 모든 테이블: 정책 없음 = anon/authenticated 직접접근 전면 차단, service_role 만.

-- users: 공개 읽기(email 미보관). 쓰기 정책 없음 → service_role 만 기록.
drop policy if exists "public read" on public.users;
create policy "public read" on public.users for select using (true);

-- ratings: 공개 읽기이되 soft-deleted 제외(0081). 쓰기 정책은 0053 에서 전부 제거.
drop policy if exists "public read" on public.ratings;
create policy "public read" on public.ratings for select using (deleted_at is null);   -- 0081

-- ----------------------------------------------------------------------------
-- GRANTS  (service_role 는 기본권한으로 전체 접근. anon/authenticated 는 아래만.)
-- ----------------------------------------------------------------------------
-- users: 0051 로 공개 프로필 컬럼만 화이트리스트(민감 컬럼 birth_date/gender/... 차단).
grant select (id, username, country, avatar_url, is_artist, created_at)
    on public.users to anon, authenticated;

-- ratings: 공개 SELECT(테이블 전체). 쓰기 grant 는 0053 에서 revoke.
grant select on public.ratings to anon, authenticated;

-- ============================================================================
-- FUNCTIONS & TRIGGERS
-- ============================================================================

-- 0075: ratings.updated_at 자동 유지.
create or replace function public.ratings_set_updated_at() returns trigger as $$
begin
    new.updated_at = now();
    return new;
end;
$$ language plpgsql;

drop trigger if exists ratings_set_updated_at_trg on public.ratings;
create trigger ratings_set_updated_at_trg before insert or update on public.ratings
    for each row execute function public.ratings_set_updated_at();

-- 0076: review_reactions 변경 시 ratings.likes_count/dislikes_count 재계산.
create or replace function public.sync_review_reaction_counts() returns trigger as $$
declare rid uuid := coalesce(new.rating_id, old.rating_id);
begin
    update public.ratings set
        likes_count    = (select count(*) from public.review_reactions where rating_id = rid and value = 'like'),
        dislikes_count = (select count(*) from public.review_reactions where rating_id = rid and value = 'dislike')
    where id = rid;
    return null;
end;
$$ language plpgsql;

drop trigger if exists sync_review_reaction_counts_trg on public.review_reactions;
create trigger sync_review_reaction_counts_trg
    after insert or delete or update on public.review_reactions
    for each row execute function public.sync_review_reaction_counts();

-- 0078: set_likes 변경 시 sets.like_count 재계산.
create or replace function public.sync_set_like_count() returns trigger as $$
declare sid uuid := coalesce(new.set_id, old.set_id);
begin
    update public.sets set like_count = (select count(*) from public.set_likes where set_id = sid) where id = sid;
    return null;
end;
$$ language plpgsql;

drop trigger if exists sync_set_like_count_trg on public.set_likes;
create trigger sync_set_like_count_trg after insert or delete on public.set_likes
    for each row execute function public.sync_set_like_count();

-- ============================================================================
-- SEED  (참조 데이터)
-- ============================================================================

-- spotify_status: 단일 행.
insert into public.spotify_status (id) values (1) on conflict (id) do nothing;

-- app_log_config: 단일 행.
insert into public.app_log_config (id, min_level) values (1, 'Warning') on conflict (id) do nothing;

-- genres: 0034 시드 + 0036 리파인(name 영어 단일, kpop/ost 제외, 서브장르 심화) 반영 최종본.
-- 상위 장르
insert into public.genres (slug, name, sort_order) values
    ('pop',        'Pop',        10),
    ('rock',       'Rock',       20),
    ('hiphop',     'Hip-hop',    30),
    ('rnb',        'R&B',        40),
    ('ballad',     'Ballad',     50),
    ('indie',      'Indie',      60),
    ('electronic', 'Electronic', 70),
    ('dance',      'Dance',      80),
    ('metal',      'Metal',      90),
    ('jazz',       'Jazz',       100),
    ('soul',       'Soul',       110),
    ('funk',       'Funk',       120),
    ('blues',      'Blues',      130),
    ('folk',       'Folk',       140),
    ('country',    'Country',    150),
    ('classical',  'Classical',  160),
    ('latin',      'Latin',      170),
    ('trot',       'Trot',       180)
on conflict (slug) do nothing;

-- 서브 장르(0034 + 0036, parent_slug 로 연결)
insert into public.genres (slug, name, parent_id, sort_order)
select c.slug, c.name, p.id, c.sort_order
from (values
    ('hardrock',   'Hard Rock',          'rock',       210),
    ('altrock',    'Alternative Rock',   'rock',       211),
    ('progrock',   'Progressive Rock',   'rock',       212),
    ('postrock',   'Post-rock',          'rock',       213),
    ('punk',       'Punk',               'rock',       214),
    ('shoegaze',   'Shoegaze',           'rock',       215),
    ('grunge',     'Grunge',             'rock',       216),
    ('surfrock',   'Surf Rock',          'rock',       217),
    ('trap',       'Trap',               'hiphop',     220),
    ('boombap',    'Boom Bap',           'hiphop',     221),
    ('drill',      'Drill',              'hiphop',     222),
    ('lofi',       'Lo-fi',              'hiphop',     223),
    ('neosoul',    'Neo-soul',           'rnb',        230),
    ('house',      'House',              'electronic', 240),
    ('techno',     'Techno',             'electronic', 241),
    ('edm',        'EDM',                'electronic', 242),
    ('ambient',    'Ambient',            'electronic', 243),
    ('trance',     'Trance',             'electronic', 244),
    ('dnb',        'Drum & Bass',        'electronic', 245),
    ('dubstep',    'Dubstep',            'electronic', 246),
    ('synthwave',  'Synthwave',          'electronic', 247),
    ('synthpop',   'Synth-pop',          'pop',        250),
    ('citypop',    'City Pop',           'pop',        251),
    ('artpop',     'Art Pop',            'pop',        252),
    ('dreampop',   'Dream Pop',          'pop',        253),
    ('heavymetal', 'Heavy Metal',        'metal',      260),
    ('deathmetal', 'Death Metal',        'metal',      261),
    ('blackmetal', 'Black Metal',        'metal',      262),
    ('bossa',      'Bossa Nova',         'jazz',       270),
    ('swing',      'Swing',              'jazz',       271),
    ('gospel',     'Gospel',             'soul',       280)
) as c(slug, name, parent_slug, sort_order)
join public.genres p on p.slug = c.parent_slug
on conflict (slug) do nothing;


-- ============================================================================
-- CONSOLIDATION NOTES
-- ============================================================================
-- 원본: server/db/0001..0081 (0019, 0022 번호는 원래부터 결번). README.md / maintenance_orphan_oauth_purge.sql
-- 는 마이그레이션이 아니므로 제외.
--
-- [테이블에 접힌 ALTER — 대표]
--   users:  0003 terms_accepted_at / 0004 birth_date,gender / 0011+0037 locale(ko,en,ja,es)
--           / 0018 suspended_until,banned / 0058 demographics_updated_at / 0079 hide_level
--           / 0002 auth.users FK / 0051 컬럼 화이트리스트 grant.
--   ratings: 0005 target_id 의미(+comment) / 0007 target_spotify_id / 0013 표시메타 3컬럼
--           / 0017 소프트삭제 3컬럼 / 0029 target_artists / 0030 deleted_by FK→admins
--           / 0048 target_explicit / 0075 updated_at+trigger+review length check
--           / 0076 likes_count,dislikes_count+trigger. 쓰기정책·grant 는 0053 에서 제거,
--           공개읽기 정책은 0081 에서 deleted_at is null 로 최종.
--   reports: target_type 최종 5종(0006→0038 comment→0045 set_comment→0046 set) /
--           0052 target_id uuid CHECK(원본 NOT VALID, 0073 validate → 인라인 유효 CHECK 로 접음) /
--           0070 영구 unique 제거 후 partial unique(status='pending').
--   notifications: 0020 actor_id nullable+detail+type warning / 0039 mention / 0065 announcement
--           / 0054 actor_id FK on delete set null.
--   sets/set_tracks/set_comments: 0043 updated_at / 0055 소프트삭제 / 0078 like_count+trigger
--           / 0054 length·position CHECK / 0056 (set_id,position) deferrable unique
--           / 0049 explicit / 0042 album 메타 / 0075 deleted_by FK→admins.
--   review_comments: 0038 parent_id+소프트삭제 / 0047 edited_at / 0071 deleted_by FK→admins.
--   legal_versions: 0024 version / 0025 is_current(+one_current idx) / 0026 effective_date.
--   admins 0033 is_active. genres 0036 name_en→name & name_ko drop.
--   0075 admins FK 일괄(sets/set_comments/legal_documents/legal_versions/faq_items/
--        announcements/patch_notes/user_sanctions.admin_id) — 전부 on delete set null.
--   0054 user_sanctions.report_id FK→reports(set null). 0030 inquiries.handled_by FK→admins.
--
-- [ADD-THEN-DROP 컬럼 — 전면 OMIT]
--   set_tracks.youtube_url   : 0041 추가 → 0064 삭제.
--   daily_picks.youtube_url  : 0062 추가 → 0063 삭제(대신 target_youtube_links 테이블).
--   announcements.view_count : 0066 추가 → 0075 삭제(announcement_views count(*) 사용).
--   genres.name_ko           : 0034 추가 → 0036 삭제. genres.name_en → 0036 에서 name 으로 rename.
--
-- [DROP 된 테이블 — OMIT]
--   albums, tracks : 0001 생성 → 0030 삭제(캐시는 spotify_cache + ratings 표시메타로 대체).
--
-- [DROP 된 인덱스 — OMIT]
--   feed_dismissals_user_idx : 0031 생성 → 0054 삭제(PK 선두 user_id 가 커버).
--
-- [DROP 된 정책/권한 — 최종본만 반영]
--   ratings "own insert/update/delete" 정책 및 authenticated insert/update/delete grant: 0002 생성 → 0053 제거.
--   ratings "public read": 0002 using(true) → 0081 using(deleted_at is null).
--   users select grant: 0002 테이블 전체 → 0051 컬럼 화이트리스트.
--
-- [SKIP — 순수 데이터 백필 / 일회성 파괴적 마이그레이션(fresh DB엔 이관할 데이터 없음)]
--   0026 : effective_date 백필(update). (컬럼/인덱스는 포함)
--   0056 : 기존 position 0-based 정규화(update). (deferrable unique 제약은 포함)
--   0057 : user_consents grandfather 백필(insert). (테이블/인덱스는 포함)
--   0071 : deleted_by=user_id → null 백필(update). (FK 는 포함)
--   0073 : 비-uuid 레거시 reports 삭제 + dangling report_id null(파괴적). CHECK/FK 는 이미 유효로 접음.
--   0074 : target_artists jsonb 키 케이싱(id→Id) 정규화(update). 신규 코드는 PascalCase 로 씀 → 스키마 영향 없음.
--   0075 (일부): view_count 드롭·updated_at/카운터 백필 update 는 스킵, 나머지(FK·CHECK·컬럼·트리거)는 포함.
--   0076/0078 (일부): 카운터 백필 update 스킵, 컬럼·트리거는 포함.
--
-- [사람이 확인할 결정/모호점]
--   1) NOT VALID 제약 접기: 0052 reports_target_id_uuid, 0054 user_sanctions_report_fkey,
--      0075 admins FK 다수, 0071 review_comments_deleted_by_fkey 는 원본에서 NOT VALID(→후속 VALIDATE)로
--      들어갔으나, 데이터 없는 fresh DB 에선 처음부터 유효 인라인 제약으로 생성했다. 동작상 동일하되
--      원본의 "레거시 위반 허용" 뉘앙스는 사라짐(신규 DB엔 무관).
--   2) ratings SELECT grant: 태스크 지시문은 "ratings per 0081/0053 컬럼 화이트리스트"라 했으나,
--      실제 0053/0081 은 컬럼 화이트리스트를 하지 않음(0053=쓰기 revoke, 0081=정책 변경). 따라서
--      ratings 는 0002 의 테이블 전체 SELECT grant 를 유지했다. 컬럼 제한이 의도였다면 별도 확인 필요.
--   3) genres seed 최종본: 0036 이 kpop/ost 를 delete(cascade) 하므로 시드에서 제외했고, name_en 값을 name
--      으로 사용, 서브장르는 0034+0036 을 합쳐 슬러그당 1행으로 정리했다. sort_order 는 원본 값 유지.
--   4) 확장: pg_trgm 만 원본에 존재(0077). gen_random_uuid() 는 PG13+ core 로 가정(Supabase 기본).
--      구버전 대상이면 pgcrypto 확장 주석 해제 필요.
--   5) auth.users(Supabase Auth) 존재 전제(users_id_fkey). 비-Supabase Postgres 로 이식 시 이 FK 제거 필요.
--   6) 0005 의 target_id COMMENT 는 문서화 목적이라 포함했다(스키마 동작엔 영향 없음).
