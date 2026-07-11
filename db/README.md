# DB 스키마

## 파일

| 파일 | 내용 |
|------|------|
| `schema.sql` | **최종 상태 통합 스키마(정본)** — 테이블·제약·인덱스·RLS·정책·grant·함수/트리거 + `genres`/단일행 시드 |
| `maintenance_orphan_oauth_purge.sql` | 운영용 정리 쿼리(마이그레이션 아님) |

## 적용 (fresh DB)

Supabase 대시보드 → SQL Editor 에 `schema.sql` 을 붙여넣어 실행한다. 대부분 `if not exists` 라 재실행 가능.

> ⚠️ **적용 전 fresh Supabase 프로젝트에서 반드시 테스트할 것.** `schema.sql` 하단 `-- CONSOLIDATION NOTES`
> 에 정리한 결정사항(원래 `NOT VALID` 였던 제약을 인라인 유효 제약으로 접음, `genres` 시드 재구성,
> add-then-drop 컬럼 제외, dropped 테이블 `albums`/`tracks` 제외 등)을 확인한다.

## 마이그레이션 이력

기존 번호 마이그레이션 `0001`~`0081` 은 이 `schema.sql` 하나로 접어 제거했다(NON-270). 개별 파일이 필요하면
git 이력(예: `develop` 브랜치의 이전 커밋)에서 복원할 수 있다. 앞으로 스키마 변경은 `schema.sql` 을 직접
수정하거나, 이미 배포된 DB 대상이면 별도 ALTER 스크립트로 적용한 뒤 `schema.sql` 에도 반영한다.

## 설계 메모

- **RLS = 방어선**: 데이터 흐름은 프론트 → .NET Api(service_role, RLS 우회) → DB. RLS/정책은 Supabase
  anon/authenticated 키의 PostgREST 직접 접근을 막는 용도. 정책이 있는 테이블은 `users`(공개 읽기)·
  `ratings`(공개 읽기, `deleted_at is null`)뿐이고, 나머지는 정책 없음 = 직접 접근 전면 차단.
- **email 미보관**: `auth.users` 가 단일 출처. .NET Api 는 JWT 클레임 또는 service_role 로 조회.
- **ratings.target 폴리모픽**: `target_type`(album/track) + `target_id`(ISRC/UPC, 없으면 spotify_id). 단일 FK 불가 → CHECK + 인덱스.
- **username 대소문자 무시 유니크**: `lower(username)` 유니크 인덱스 + trigram(GIN) 검색.

## 탈Supabase 시

`users_id_fkey`(→ `auth.users`) 제약과 RLS 를 자체 인증 모델 기준으로 교체한다(또는 앱 계층 인가로 일원화).
