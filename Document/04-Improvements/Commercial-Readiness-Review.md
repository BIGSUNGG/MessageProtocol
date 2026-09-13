---
project: DS_MessageProtocol
type: improve
status: stable
tags: [review, commercial-readiness, wire-compat]
updated: 2026-09-14
---

# 상용화 적합성 검토 (2026-09-08, v2.3.9 기준; 2026-09-09 개선 패스 반영; **2026-09-13 v3.0.0 재검증**; **2026-09-14 v3.1.0 재검증**)

> "실제 유니티 서버 상용화 서비스의 라이브러리로 사용해도 문제 없는가"에 대한 검토 기록.
> 빌드·테스트·퍼저·벤치마크 실측 + 와이어 호환성 실증 프로브(`artifacts/wire-probe/`) 기반.

## 3.1.0 재검증 (2026-09-14 — 독립 재실측)

HEAD(`62d1858`, 3.1.0)에서 문서 기록을 **코드·문서 읽기 + 실측으로 독립 재검증**했다. HEAD 는 검토된 3.0.0 상태에 버전 bump 만 추가한 것(`Source/` 델타 = KI-43 3차 수정 b75dc48, 이미 문서화됨)이라 결론 변동 없음. 실측: 클린 리빌드(`-t:Rebuild`) 오류 0·Source 경고 0(테스트 경고 8 = 기존 xUnit/nullable 세트), 테스트 **332/332 × 2 TFM 통과**, Sandbox 전체 PASS(exit 0), `NetStandardFixtures`(Unity 폴백 프로필) Release 빌드 오류 0. 코어 런타임 5파일(reader·writer·serializer 등록/캐시·contexts·PooledBuffer) 직접 감사 — 경계 검사(unsigned 랩어라운드 오버플로 안전)·엄격 UTF-8·decimal 비트 검증·깊이 가드 양방향·등록 클레임 선점/롤백/volatile 발행 전부 구현 확인. 생성 코드 실방출 검사(`EmitCompilerGeneratedFiles` 산출물) — 헤더 4바이트 검증(KI-5)·할당 전 컬렉션 가드(KI-13/17, `long 곱 × Remaining`)·null 규약·멤버 1회 스냅샷(KI-26) 확인. §1~5 제약(스키마 진화 미지원·KI-34 잔존·Unity Roslyn 버전·크로스 어셈블리 ID 유일성)은 그대로 유효.

## 결론

**채용 가능.** 직렬화 코어 자체는 상용 서비스 수준의 품질 기준(테스트·퍼저·스레드 감사·신뢰 경계 강화)을 충족한다. 단, **와이어 스키마 진화 미지원**이 구조적 제약이므로 라이브 운영 규칙(메시지 레이아웃 동결·신규 MessageId 확장)을 팀 컨벤션으로 반드시 정착해야 한다.

## 3.0.0 재검증 (2026-09-13)

v2.3.9 검토 이후 3개 커밋(`eee3797` [Message] 속성 + FNV-1a 해시 ID · `98d1662` 단일 속성 통합(3.0.0) · `483b9d7` NuGet 의존성 전파)이 본 검토를 통과하지 않은 채 HEAD 에 반영됐다. 이 델타를 상용 게임 서버 렌즈로 재검증했다.

### 실측 기준선 (3.0.0 + KI-43 수정 후)

| 항목 | 결과 |
| ---- | ---- |
| Release 빌드 (전체 sln) | 오류 0, Source 경고 0(테스트 프로젝트에만 기존 xUnit/nullable 경고 — v2.3.9 검토와 동일 세트) |
| 유닛 테스트 | **332 통과 / 0 실패** × 2 TFM (net8.0·net9.0) — 기준 323 + KI-43 회귀 9(1차 3 + 리뷰 라운드 2차 5 + 3차 크로스 어셈블리 1) |
| Sandbox 인수 시나리오 | **45/45 PASS**, 종료 코드 0 |

### 델타 감사 결과

| 검증 항목 | 결과 |
| ---- | ---- |
| FNV-1a 해시 ID | 알고리즘 동결 약속과 구현 일치(`MessageIdHash` — UTF-8 바이트 FNV-1a 32비트 → 24비트 마스크, 단일 소스). 생성기 `BuildFullName`(ns 점 + 중첩 `+` + `MetadataName` 차수)은 어셈블리·플랫폼 무관 결정적. MSGPROT016(해시 충돌)·MSGPROT017(Child 해시 0) 실동작 회귀 존재 |
| Kind 자동 추론 | 조상 [Message] → Child, 동일 컴파일 파생 → Parent, 나머지 Standalone 추론은 참조 어셈블리 베이스 제외 규칙과 함께 문서·구현 일치(`CollectMessageDescendantBases` 는 소스 있는 베이스만). MSGPROT003/004/018 진단 실동작 |
| 와이어 MessageId 유일성 | MSGPROT014/015 실동작(회귀 다수) — 단 **게이트가 거부 예정 선언을 세 count 하는 갭(KI-43)** 을 발견·수정: 정의 밖 kind 값(018)·계층 위반(003/004)·중첩 컨테이닝 non-partial(002)·해시 0 Child(017) 선언이 카운트되어 정상 타입에 거짓 양성 014/015. 게이트에 해당 거부 판정(Generate 와 같은 `ValidateRootHierarchy`·`IsNestedContainingTypesPartial`·`IsHashZeroGroupElement`) 추가, 구성 캐리러 방출 조건은 제네릭 게이트 판정으로 일원화 — 018 뿐 아니라 001·002·005·010·013 으로도 거부될 선언의 캐리러가 방출되어 CS0311 생성 코드가 나오던 결함류를 전부 차단(리뷰 라운드 2차 수정). **2차 일원화는 유효한 크로스 어셈블리 순수 캐리러를 거짓 MSGPROT008 으로 거부하는 회귀를 만들었다**(게이트의 partial 판정이 메타데이터 전용 선언 PE 에서 거짓 negative) — 캐리러 거부를 이 컴파일 소스 선언 한정으로 수정(3차, 외부 선언은 원 컴파일 게이트가 검증·중복은 ADR-0005 런타임 감지 유지) |
| 패키징(483b9d7) | 메타 패키지 nuspec 의존성 전파를 **독립 재실증** — 전역 패키지 캐시 클린 후 로컬 피드에서 `MessageProtocol` 단독 설치 소비자 프로젝트: 생성기 로드(`csc /analyzer:…messageprotocol.codegenerator\3.0.0\analyzers\dotnet\cs\…dll`)·`Ping.g.cs` 생성·왕복 라운드트립 실행 확인. `exclude="Build,Analyzers"` 속성은 PackTask 가 ProjectReference 유래 의존성에 강제로 붙이지만 전이 생성기 적용은 막지 않음([Packages](../03-Reference/Packages.md) 2026-09-13 실증과 독립 일치) |
| 수신 신뢰 경계 | 3.0.0 생성 코드가 기존 가드(헤더 검증 KI-5 — `DecomposeWireId` 단일 분해, 길이 접두사 할당 전 검증, 깊이 상한, 참조 태그 검증 KI-36, 엄격 UTF-8, decimal flags)를 그대로 방출 — Sandbox S14 + 퍼저 상주 회귀로 확인 |
| 스레드 안전 | 등록 레지스트리·`SerializerCache<T>` 의 KI-11/38/39 하드닝(클레임 선점·volatile 발행·롤백 순서)이 3.0.0 재작성 후에도 무결 — 해당 파일들의 3.0.0 델타는 플래그 재명명(`StandaloneOrGroup`→`IdMessage`)뿐 |
| GC/할당 | 핫 경로(제네릭 직렬화·역직렬화·디스패치)의 3.0.0 델타 없음 — 신규 코드는 속성·시작 시점(모듈 이니셜라이저 등록) 표면. `MessageIdHash.FromFullName` 의 `byte[]` 할당은 생성기(컴파일 타임) 전용 |

### 운영 주의(신규)

- **동일 버전 재팩 시 전역 캐시 함정**: NuGet 전역 캐시(`~/.nuget/packages/`)는 버전 폴더로 캐시하므로, 버전 번호를 올리지 않고 재 pack 하면 소비자 테스트가 **이전 레이아웃의 낡은 패키지**를 쉰 채로 소비할 수 있다(실측: 2026-09-11 번들 analyzer 레이아웃이 캐시에 남아 있었다). 패키지 동작을 검증할 때는 캐시 폴더 삭제 또는 버전 증가 후 할 것.
- **어셈블리 간 와이어 ID 유일성**: MSGPROT014/015/016 게이트는 컴파일 단위 한정이다. 프로토콜 DLL + 게임 DLL 분리(표준 구성)에서 서로 다른 어셈블리의 메시지가 같은 조립 ID 를 쓰면 컴파일에서 걸리지 않고 모듈 로드 시 `TypeInitializationException` 으로만 발견된다. 해시 ID 군이 크면 우연 충돌 확률도 커진다(생일 역설: 1,000개 기준 ~3%, 5,000개 기준 ~53%). 수동 ID 를 어셈블리별 대역으로 나누 쓰거나 해시 ID 충돌을 CI 에서 전체 조립 ID 집합으로 검사할 것.

## 검증 근거 (2026-09-08~09, v2.3.9 기준 — 역사 기록)

| 항목 | 결과 |
| ---- | ---- |
| Release 빌드 (전체 sln) | 오류 0, 경고 8(테스트 코드만) |
| 유닛 테스트 | **309 통과 / 0 실패** × 2 TFM (net8.0·net9.0) — 2026-09-09 개선 패스 후(기준 296 + 13: KI-42 크로스 어셈블리 2 + IVT 페어 2, `DeserializeExact` 5 + netstandard2.1 폴백 2, 진입 계약 2) |
| Sandbox 인수 시나리오 | **42/42 PASS**, 종료 코드 0 |
| 차등 퍼저 | 상주 회귀 + 15배 캠페인(42만 판독) 위반 0 — Known-Issues KI-41 |
| 벤치마크 | Serialize 62ns/104B · Deserialize 77–97ns/192B · `SerializePooled` 32B |
| 스레드 안전성 | KI-11/33/38/39 감사·해결 완료(ARM 메모리 모델 포함) |
| 불신 입력 방어 | 길이 접두사 할당 전 검증(KI-13/17), 중첩 깊이 상한 양방향(KI-14/25), decimal flags 검증(KI-15), 엄격 UTF-8, 참조 태그·헤더 검증(KI-5/36), 예외 분류 계약(KI-41) |

## 상용화 제약 (채용 시 반드시 인지)

### 1. 와이어 스키마 진화 미지원 — 최대 리스크 (실증)

> **정책 확정 (2026-09-09)** — [ADR-0006](../05-Decisions/ADR-0006-No-Schema-Evolution.md): 스키마 진화는 미지원으로 확정. 아래 운영 규칙이 공식 컨벤션이다.

와이어는 **멤버 선언 순서 기반 포지셔널 포맷**이다. 필드 태그·옵셔널 멤버·미지원 필드 스킵·잔여 바이트 검사가 없다. 같은 MessageId 의 레이아웃이 배포 피어 간 어긋나면:

| 변경 | 구형 프레임 → 신형 리더 | 위험도 |
| ---- | ---------------------- | ------ |
| 끝에 필드 추가 | `EndOfStreamException` (fail-fast) | 낮음 — 크게 실패 |
| **필드 제거** | **조용히 성공, 데이터 유실** | **높음** |
| **멤버 순서 변경** | **조용히 성공, 값 교차 복원** | **높음** |

프로브 실측(`artifacts/wire-probe/`): v1 `(Id=7, Gold=500)` 프레임을 순서만 바꾼 v2 `(Gold, Id)` 로 읽으면 `Gold=7, Id=500` 으로 예외 없이 복원. 제거하면 `Id=7` 만 복원되고 나머지는 폐기.

**운영 규칙 (필수)**: 한 번 배포된 메시지의 멤버 레이아웃은 동결한다. 스키마 변경은 **새 MessageId 의 신규 타입 추가**(예: `LoginMsgV2`)로만 하고 구형 타입은 등록 유지한다. 이 경우 헤더 MessageId 검증(KI-5)이 구형 프레임을 신형 타입으로 재해석하는 것을 진입에서 막아준다.

**라이브러리 측 완화 (제공됨, 2026-09-09)**: `MessageSerializer.DeserializeExact<T>(ReadOnlySpan<byte>)`·`DeserializeExact(ReadOnlySpan<byte>)` — 최상위 역직렬화에서 프레임 전체를 정확히 소비했는지 검사하고, 남은 바이트가 있으면 `InvalidDataException` 으로 크게 실패시킨다(와이어 무영향 — "제거" 케이스를 조용한 손실에서 fail-fast 로 전환). [Public-API](../03-Reference/Public-API.md) 참고.

### 2. 혼합 버전 피어 결합 (문서화된 제약)

디스패치·위임 멤버 위치의 백레퍼런스는 2.2.0+ 생성 코드부터 발생한다(KI-9). 공유 참조 그래프를 주고받는 피어는 **양측 모두 동일 버전으로 재생성** 필요. 배포 시 패키지 버전·생성 코드 버전을 함께 롤링할 것.

### 3. KI-34 잔존 — 조용한 타입 좁힘 (정책 결정 대기)

구체 베이스 멤버가 먼저 기록한 인스턴스를 파생 타입 멤버의 백레퍼런스가 읽으면 파생 필드가 예외 없이 유실될 수 있다. `MSGPROT012` 경고를 무시하지 말고, 다형 멤버는 베이스를 `abstract` 로 선언한다.

### 4. Unity 측 배포 확인 항목

- 런타임은 netstandard2.1·정적 추상 미사용·ModuleInitializer polyfill — Unity 호환 설계이며 `Test/MessageProtocol.NetStandardFixtures`가 Unity 프로필 생성 코드를 실행으로 검증한다.
- 단, **소스 생성기는 Microsoft.CodeAnalysis.CSharp 4.14 기준** — 대상 Unity 에디터 내장 Roslyn 보다 새로우면 분석기 로드 실패 가능. Unity 측 사용 전 실제 에디터에서 생성 동작 확인. 실패 시 수동 구현 경로 또는 서버 측에서 생성한 코드를 디스크로 고정해 사용.
- Unity 에 UPM/DLL 복사로 가져올 때 `System.Memory`·`System.Buffers` 어셈블리 중복 참조 충돌 여부 확인(Unity 자체 포함 어셈블리와 버전 정렬).

### 5. 기타 (경미)

- 등록 레지스트리는 프로세스 전역 — 다중 프로토콜 세트 공존은 ID 충돌로 크게 실패(컴파일러 MSGPROT014/015가 사전 차단).
- 페이로드 상한 단일 `byte[]` ≈ 2GB — 게임 메시지 범위에서 무관.
- 퍼저 외 엔진 단 방어(프레임 크기 상한·레이트 제한)는 DS_Communication 측 책임(범위 밖 명시).
- KI-10: 생성기 CPU가 편집마다 재실행됨(측정·연기) — IDE 빌드 체감만, 런타임 무관.

## 권고 조치

1. 팀 컨벤션 문서에 "와이어 스키마 동결·신규 MessageId 확장" 규칙 명문화 (이 노트 §1).
2. ~~최상위 소비 완료 검사 옵션 API 검토~~ — **완료 (2026-09-09)**: `DeserializeExact` 제네릭·object dispatch 진입 제공(회귀 테스트 5개). 스키마 변경 사고를 조용한 손상에서 큰 실패로 전환.
3. Unity 에디터에서 소스 생성기 로드 검증 (§4).
4. 배포 절차에 "패키지 버전 + 피어 재생성 동시 롤링" 포함 (§2).

## 관련

- [Feature-Spec](../02-Architecture/Feature-Spec.md) — 와이어 멤버 순서 호환 규정(F3)
- [Known-Issues](../06-Troubleshooting/Known-Issues.md) — KI-5/9/14/25/34/36/41
- [Public-API](../03-Reference/Public-API.md) — 예외·계약
- 프로브 소스: `artifacts/wire-probe/` (gitignored, 재현: dotnet build + run)
- [Performance-Comparison](./Performance-Comparison.md) — MemoryPack·MessagePack 대비 속도·할당·와이어 실측 (2026-09-09)
