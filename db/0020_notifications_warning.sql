-- NON-58: 경고(warning)를 알림으로 전달. 관리자/시스템 발신은 actor(users)가 없음 → actor_id nullable.
-- type에 'warning' 추가, 사유를 담을 detail 컬럼 추가. target_id 엔 대상 rating id(신고 기반 경고).
alter table public.notifications alter column actor_id drop not null;
alter table public.notifications add column if not exists detail text;
alter table public.notifications drop constraint if exists notifications_type_check;
alter table public.notifications add constraint notifications_type_check
    check (type in ('follow', 'review_like', 'warning'));
