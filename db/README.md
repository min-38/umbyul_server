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
