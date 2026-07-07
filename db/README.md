# DB 스키마 (NON-4)

Phase 1 핵심 테이블 + 권한 정책. 벤더 중립 SQL 로 작성하고 Supabase 종속 부분을 분리했다.

## 파일

| 파일 | 내용 | 이식성 |
|------|------|--------|
| `0001_core_schema.sql` | users / albums / tracks / ratings + 인덱스·제약 | 표준 PostgreSQL, 어디서나 |
| `0002_supabase_rls.sql` | `auth.users` FK + RLS 정책 | **Supabase 전용** |

## 적용

Supabase 대시보드 → SQL Editor 에 순서대로 붙여넣어 실행한다. 둘 다 재실행 가능(idempotent).

```
0001_core_schema.sql   →   0002_supabase_rls.sql
```

> ⚠️ **`0001` 은 레거시 테이블을 먼저 DROP 한다.** 이전 실험용 스키마(MusicBrainz 시절)인
> `profiles`, `albums`, `tracks`, `reviews`, `follows`, `point_transactions`,
> `review_reactions`, `review_reports` 가 **CASCADE 로 삭제**된다 (데이터 포함). 실데이터가
> 없는 단계 기준. Phase 2 소셜/포인트 테이블은 해당 이슈에서 새로 만든다.

## 탈Supabase 시

`0001` 은 그대로 두고 `0002` 만 빼면 된다. 인증을 다른 방식으로 옮길 때:
- `users_id_fkey`(→ auth.users) 제약을 자체 인증 테이블 기준으로 교체
- RLS `auth.uid()` 정책을 새 인증 모델에 맞게 재작성 (또는 앱 계층 인가로 일원화)

## 설계 메모

- **email 미보관**: `auth.users` 가 단일 출처. .NET Api 는 JWT email 클레임 또는 service_role 로 조회.
- **RLS = 방어선**: 데이터 흐름은 프론트 → .NET Api(service_role, RLS 우회) → DB. RLS 는 anon 키 직접 접근 차단용.
- **ratings.target 폴리모픽**: `target_type`(album/track) + `target_id`. 단일 FK 불가 → CHECK + 인덱스.
- **username 대소문자 무시 유니크**: `lower(username)` 유니크 인덱스.
- **country**: 기획안 §6 외 추가 컬럼(통계 목적). nullable.

## 마이그레이션 이력·갭 (3차 QA DB-17)

- 파일은 `0001` 부터 순번대로 SQL Editor 에 적용한다. 위 표는 초기 2개만 기록 — 이후 파일(0003~)은 각 파일 상단 주석으로 목적을 설명한다.
- **번호 갭**: `0019` 는 NON-57 에서 만든 `0019_sanction_acknowledged.sql` 을 NON-58 에서 삭제(경고-알림 0020 으로 대체)해 생긴 의도적 빈 번호. `0022` 는 애초에 존재한 적 없이 건너뛴 번호. 둘 다 정상.
- **유령 컬럼 주의**: 위 삭제 전에 `0019` 를 이미 적용했다면 `user_sanctions.acknowledged_at` 컬럼이 prod 에 남아 있을 수 있다(코드 미참조). 확인 후 필요 시 `alter table public.user_sanctions drop column if exists acknowledged_at;`.
- **3차 QA 보안·무결성 보강 (0050~0055)**: RLS 누락 5테이블(0050), users 컬럼 grant 축소(0051), reports.target_id uuid CHECK(0052), ratings 직접쓰기 정책 제거(0053), 제약·인덱스(0054), sets/set_comments 소프트삭제 컬럼(0055). 각 파일 상단 주석 참고.
