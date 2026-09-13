# Changelog

문서 변경 기록. 최신이 위.

## 2026-09-14 (패키지 3.1.0 릴리스)

- `Source/Directory.Build.props` 버전 3.0.0 → 3.1.0. 배포는 태그 `v3.1.0` 로 GitHub Actions(nuget-publish)가 게시 — 파이프라인이 태그에서 버전을 추출해 pack·nuget push. 내용: KI-43 충돌 판정 게이트 완결(3차 수정·회귀 테스트 9개). 와이어·공개 API·의존성 무변경.
- `Packages`·`CONTEXT` 버전 서술 동기화.

## 2026-09-13 (4) (KI-43 3차 — 2차 일원화 회귀: 크로스 어셈블리 순수 캐리러 오탐)

- **KI-43 3차 수정** (`GenericConstruction.cs`): 리뷰 라운드 2가 2-컴파일레이션 재생으로 확정한 2차 회귀 — 캐리러 방출 조건 일원화가 메타데이터 전용 선언(참조 어셈블리 PE, `DeclaringSyntaxReferences` 없음 → 게이트의 partial 판정 거짓 negative)을 "생성 거부될 선언"으로 오판해, 프로토콜 DLL(선언+생성) + 게임 DLL(순수 캐리러) 표준 구성(KI-42)이 거짓 MSGPROT008 으로 깨졌다. `ValidateConstructionEntries` 의 캐리러 가드를 이 컴파일 소스 선언 한정으로 적용 — 외부 선언은 원 컴파일 게이트가 검증, 크로스 어셈블리 중복은 ADR-0005 런타임 감지 계약 유지(게이트 자체 미수정). 015 피어 체크는 가드 결과를 재사용해 게이트 중복 호출 제거. 회귀 테스트 1개 추가(`RunGeneratorWithMetadataBase` 2-컴파일레이션: 베이스 PE 방출 + 소비자 순수 캐리러 → 진단 0·캐리러 방출·컴파일 오류 0. 이빨 확인: 가드를 2차 형태로 되돌리면 실패), 331→332×2 TFM.
- `Known-Issues` KI-43 3차 기록, `Feature-Spec` MSGPROT008 사유 서술에 소스 선언 한정 계약 반영, `Commercial-Readiness-Review` 실측 기준선 갱신(332×2 TFM·Source 경고 0 정정 서술).

## 2026-09-13 (3) (KI-43 2차 — 리뷰 라운드 발견 잔존 트리거)

- **KI-43 2차 수정** (`GenericConstruction.cs`·`MessageCodeGenerator.cs`·`TypeMetadata.cs`): 리뷰 라운드에서 같은 결함류 잔존 3건 확인·수정. ① 게이트가 MSGPROT002(중첩 컨테이닝 non-partial)로 거부될 타입을 카운트 → 같은 ID 의 정상 타입 거짓 양성 014/015(`IsNestedContainingTypesPartial` internal 승격으로 Generate 와 판정 공유). ② 구성 캐리러 가드가 018 만 보아 001·002·005·010·013 으로 거부될 선언의 캐리러가 방출 → CS0311 — 방출 조건을 제네릭 게이트 판정으로 일원화(3곳 조각 복제 제거). ③ MSGPROT017(해시 0 Child) 게이트 제외(규약 완결 — `TypeMetadata.IsHashZeroGroupElement` 단일화). 회귀 테스트 5개 추가(이빨 확인: 2차 수정 되돌리면 4개 실패 × 양 TFM), 326→331×2 TFM.
- `Commercial-Readiness-Review`: 1차 “유일한 실결함” 서술 정정, 테스트 331 반영, 운영 주의에 **어셈블리 간 와이어 ID 유일성**(014/015 게이트는 컴파일 단위 한정 — 프로토콜 DLL+게임 DLL 분리 시 조립 ID 충돌은 로드 시에만 발견, 해시 ID 1,000개 기준 생일 충돌 ~3%) 추가.
- `Known-Issues` KI-43 항에 2차 수정 기록, `Feature-Spec` MSGPROT008 사유 목록 갱신(선언부 생성 거부 사유 전부 + 캐리러 방출 조건 일원화).

## 2026-09-13 (2) (3.0.0 상용화 재검증 패스 — KI-43)

- **KI-43 해결** (`Source/MessageProtocol.CodeGenerator/GenericConstruction.cs`·`MessageCodeGenerator.cs`): KI-31 “실제로 등록될 형태만 센다” 충돌 판정 게이트가 3.0.0 신규 거부 경로를 반영하지 않았다. 정의 밖 kind 값(MSGPROT018, decode 실패→Automatic 폴백)과 계층 위반(MSGPROT003/004) 선언이 카운트되어, 같은 조립 ID/런타임 키의 정상 타입이 거짓 양성 MSGPROT014/015 로 생성·캐리러를 잃었다. 게이트에 018 정합성 검사(비제네릭·제네릭 양쪽) + 계층 판정(심볼 전용 `ValidateRootHierarchy` 를 internal 승격해 Generate 와 판정 공유) 추가. 제네릭 변형 부수 결함: 018 불일치 선언의 구성 캐리러가 방출되어 CS0311 컴파일 불가 생성 코드 — `ValidateConstructionEntries` 가 해당 구성을 MSGPROT008 로 거부. 회귀 테스트 3개(이빨 확인: 수정 전 전부 실패), 테스트 323→326×2 TFM.
- `Commercial-Readiness-Review` 를 3.0.0 기준으로 재검증 갱신: Release 빌드 경고 0·테스트 326×2 TFM·Sandbox 45/45, 3.0.0 델타 감사표(FNV-1a 결정성·kind 추론·와이어 ID 유일성·패키징 의존성 전파 독립 재실증·신뢰 경계 가드 유지·스레드 안전·GC), 운영 주의(동일 버전 재팩 시 전역 캐시 오염) 추가.
- `Known-Issues` KI-43 등록, `Feature-Spec` MSGPROT008 사유 목록에 “선언부 018 불일치 구성” 추가.

## 2026-09-13 (MessageProtocol 단일 설치로 CodeGenerator 전파)

- `Source/MessageProtocol/MessageProtocol.csproj`: CodeGenerator `ProjectReference`에서 `ReferenceOutputAssembly="false"` 제거 — NuGet pack 이 nuspec 의존성 `MessageProtocol.CodeGenerator` 를 생성, 이제 `MessageProtocol` 단일 설치로 Core·CodeGenerator 가 함께 설치된다. 기존 `analyzers/dotnet/cs` DLL 동봉 타깃(`IncludeCodeGeneratorAnalyzerInPackage`) 제거(동봉 → 의존성 전파로 전환, 이중 적용 방지).
- 실증: 로컬 피드 3패키지 → 소비자 프로젝트에서 `MessageProtocol` 만 참조해 복원 그래프에 Core·CodeGenerator 포함 + 생성기 전용 멤버(`MessageId`) 컴파일 성공. 주의점 2건 기록: ① `ReferenceOutputAssembly=false` 면 의존성 미생성, ② PackTask 가 의존성에 강제하는 `exclude="Build,Analyzers"` 는 전이 소스 생성기 적용을 막지 않음. 검증 시 글로벌 NuGet 캐시의 구버전 nupkg 가 로컬 피드를 가려 결과를 오염시킬 수 있음 — `RestorePackagesPath` 로 격리(`Packages.md` 패키지 관계 참고).
- 솔루션 빌드 오류 0, 테스트 323×2 TFM 통과, Sandbox 45 시나리오 통과.
- 3패키지 NuGet `<Description>` 을 영어 기능 중심 설명으로 전면 보강(검색 노출용 `<PackageTags>` 추가) — 컴파일 타임 직렬화·자가 등록·pooled 경로·Unity 호환 등 README 근거 내용만 기술.

## 2026-09-11 (4) (3.0.0 배포)

- 버전 2.4.0 → **3.0.0** (`Source/Directory.Build.props`) — 파괴 변경 릴리스: `MessageCategoryAttribute`·4종 종류 속성 제거, `[Message(MessageKind, id, category)]` 단일화, `MessageFlag` 멤버 개명(Parent/Child/IdMessage). 와이어 형식 불변.
- 분석기 릴리스 컷: `AnalyzerReleases.Unshipped.md`(010–018 신규 + 007 제거) → `Shipped.md` `## Release 3.0.0` 으로 이관, Unshipped 는 스켈리턴으로 리셋.
- `CONTEXT` 패키지 요약·`Packages` 버전 표기·`Feature-Spec` MSGPROT007 히스토리 주석을 3.0.0 으로 동기화.
- 태그 `v3.0.0` 푸시로 nuget-publish 파이프라인(빌드→테스트 양 TFM→Sandbox→3패키지 pack→발행)이 3.0.0 을 nuget.org 에 발행한다. 발행 후 실재 확인 절차는 `Packages.md` 참조.

## 2026-09-11 (3) (MessageKind 멤버 개명 ParentMessage→Parent · ChildMessage→Child)

- `MessageKind.ParentMessage`→`Parent`, `MessageKind.ChildMessage`→`Child` 개명(와이어 비트·진단 ID 불변) — `MessageFlag.Parent`/`Child` 와 이름을 맞춘다. 사용처 전부 동기화: 생성기 비교식·진단 문구(MSGPROT016/017), Test·Sandbox 속성 문법 47곳, README·GLOSSARY·Feature-Spec·Public-API 표기. 코드 개명은 순수 리네임이라 왕복·와이어 단언은 전부 무수정 통과(의미 불변).

## 2026-09-11 (2) ([Message] 단일 선언 속성 · MessageKind 통합)

- 메시지 종류 선언을 `[Message(MessageKind kind = Automatic, uint id = 0, MessageCategory category = Category0)]` 단일 속성으로 통합 — `StandaloneMessageAttribute`·`GroupRootMessageAttribute`·`GroupElementMessageAttribute`·`NonIdMessageAttribute` 제거(공개 API 파괴 변경).
- 신규 공개 열거 `MessageKind`(Automatic/Standalone/ParentMessage/ChildMessage/NonId, `Source/Shared/MessageKind.cs` 단일 소스 — Core·CodeGenerator 링크). Automatic 은 기존 [Message] 추론(조상 메시지 → Child, 동일 컴파일 파생 → Parent, 나머지 → Standalone). id 생략(0) = FullName 해시(모든 Id 종류 공통), 명시 = 수동(Automatic+수동 조합 가능, 수동 0 은 표현 불가).
- 공개 `MessageFlag` 멤버 개명: GroupRoot→Parent, GroupElement→Child, StandaloneOrGroup→IdMessage — 와이어 비트 값 불변(테스트가 헤더 바이트로 고정).
- 신규 진단 `MSGPROT018`([Message] 인자·종류 불일치): NonId 에 id·category 인자, 정의 밖 MessageKind 값. `AnalyzerReleases.Unshipped.md` 등록(KI-12: 마크다운 자동 서식이 구분 행을 바꿔치기해 sed 로 복구 — 재발 확인). `MSGPROT007`(속성 중복) 은 단일 속성화로 발생 불가 — 디스크립터·검사 코드 제거, Shipped 히스토리 보존, Feature-Spec 히스토리 주석 처리.
- 생성기: `TypeMetadata.TryDecodeMessageAttribute`(MessageKind/MessageCategory/정수 인자 해독), `TypeMetadataValidator.TryValidateMessageAttributeConsistency`, `ValidateRootHierarchy` 메타데이터 체인 기반으로 교정(추론 Parent 도 조상-Parent 검사에 포착), 제네릭 구성 검증이 NonId 선언 거부.
- 테스트 319→323: MSGPROT018 3종(NonId+id, NonId+category, 정의 밖 kind 값 5·99), kind×id 매트릭스(kind Standalone 해시==Automatic 해시 동일 FullName 비교, Automatic+수동 id 300 → 0x2000012C, ChildMessage id 0 → 해시 해석·헤더 0x80), 기존 왕복·와이어 단언은 속성 문법만 이전해 무수정 통과(의미 불변). Sandbox 전 시나리오 통과. 전 솔루션 빌드 오류 0.
- `README.md` 전면(속성 표→MessageKind 표, 예제·[Message] 서브섹션 재제목, 진단 표 MSGPROT016/017/018 갱신), `Feature-Spec` F2·F5, `Public-API`, `GLOSSARY` 동기화. 버전 2.4.0 유지.
- 범위 밖: DS_RPC 마이그레이션(사용자 지시 — 별도 작업, 이 시점부터 DS_RPC 컴파일 깨짐), Legacy/.

## 2026-09-11 (MessageCategoryAttribute 제거 · 속성 생성자 통합)

- `MessageCategoryAttribute` 제거(공개 API 파괴 변경) — category 니블은 이제 각 메시지 속성 생성자의 `MessageCategory` 인자로 지정한다. `[Message(MessageCategory)]` 오버로드 신규.
- `StandaloneMessage`/`GroupRootMessage`/`GroupElementMessage` 에 4개 생성자 오버로드: 무인수(= FullName 해시 ID, `Category0`), `(MessageCategory)`(해시 ID + category), `(uint id)`(수동, 기존 호환), `(uint id, MessageCategory)`. 해시 ID 는 `[Message]` 와 동일한 `MessageIdHash.FromFullName`(FNV-1a 32→24비트). Id 프로퍼티는 수동 할당 시에만 값(`uint?`, null 이면 해시).
- 진단 확장 — 해시 ID 충돌(`MSGPROT016`)·요소 해시 0(`MSGPROT017`)·카테고리 범위(`MSGPROT013`)가 무인수 explicit 속성에도 동일 적용. 와이어 형식 불변(헤더 니블 배치 동일).
- `NonIdMessage`·`GenericMessageAttribute` 는 변경 없음 — NonId 헤더(1바이트)도 category 니블을 실리지만 실사용 사례가 없어 속성 추가는 보류(필요 시 생성자 오버로드로 확장).
- 생성기 `TypeMetadata`(`DecodeAttributeArguments` — 정수 인자=수동 Id, enum 인자=category)·`TypeMetadataValidator`(범위 검사 신규 위치 이전)·`AttributeReferences`·`MetadataNames` 정리.
- 테스트 316→319: 무인수 `[StandaloneMessage]` 가 `[Message]` 와 동일 해시 MessageId 를 생성(동일 FullName 두 컴파일 비교), 수동 id+category 오버로드 와이어 반영(`0x23000005`·헤더 `0x23`), category 전용 생성자 루트 헤더(`0x42`)·해시 0 아님. 기존 진단 테스트 전부 신규 문법으로 이전(왕복·와이어 단언은 무수정 통과 — 의미 불변 확인). Sandbox 통과.
- `Feature-Spec` F2·F5, `Public-API`, `GLOSSARY`, 루트 `README.md` 동기화. 버전 2.4.0 유지(사용자 지시).

## 2026-09-10 ([Message] 자동 선언 · 크로스 어셈블리 파생)

- `[Message]` 무인수 자동 선언 속성 신규 (`MessageProtocol.Core`) — 종류(Standalone/GroupRoot/GroupElement)는 상속 계층에서 자동 추론(조상 메시지 → 요소, 동일 컴파일 `[Message]` 파생 → 루트, 나머지 → 독립), ID 는 타입 FullName 의 FNV-1a 32비트 → 24비트 마스크 해시. 알고리즘·FullName 형식(BCL `Type.FullName` 관례)은 런타임·생성기 공유 단일 소스 `Source/Shared/MessageIdHash.cs` 로 동결.
- 제네릭 선언부에 `[Message]` 적용 시 선언 MessageId 만 해시 대체 — 닫힌 구성 등록은 기존 `[GenericMessage(typeof(…), ClassId)]` 수동 방식 유지(`GenericConstruction` 수용 확장).
- 크로스 어셈블리 파생 지원 — 다른 프로젝트(참조 어셈블리)의 메시지 베이스를 상속한 클래스에 속성을 붙이면 참조 베이스 계층까지 심볼 추적해 생성·등록. `[Message]` 조상은 그룹 요소의 루트 요건을 만족(참조 베이스는 선언부 어셈블리에서 확정된 플래그 유지 — 소비 컴파일에서 재해석 없음).
- 신규 진단: `MSGPROT016`(FullName 해시 MessageId 충돌 — 이름 변경·명시적 속성 전환 안내, 자동 재해시 없음), `MSGPROT017`(그룹 요소 위치 해시 0 거부). `AnalyzerReleases.Unshipped.md` 등록.
- 생성 partial 선언부가 원본 접근성(`internal` 등)을 따르도록 수정(`TypeMetadata.AccessibilityKeyword`) — public 아닌 메시지 타입 지원.
- 테스트 309→316: `[Message]` 추론 왕복(독립/그룹 object dispatch), 해시 ID 알고리즘 핀(리터럴 단언), 제네릭 선언부 해시 구성 왕복, 크로스 어셈블리 상속 요소 왕복(NetStandardFixtures 베이스), `MSGPROT016`/`MSGPROT017` 진단. 전 솔루션 빌드 0 오류, Sandbox 통과.
- `Feature-Spec` F2·F5, `Public-API`, `GLOSSARY` 동기화. 버전 2.3.9 → 2.4.0.
- 루트 `README.md` 에 `[Message]` 사용법 추가 — QuickStart 팁(무설정 선언 안내), 속성 표 행, 전용 서브섹션(종류 추론 규칙·FullName 해시·동결 고지·충돌 정책·크로스 어셈블리 파생·제네릭 선언부·internal 지원·`MessageIdHash` 헬퍼), 진단 표 `MSGPROT016`/`MSGPROT017` 행.
- Sandbox S15 신규 — `[Message]` 자동 선언 실행 검증: Standalone 추론 round-trip·MessageId=FullName 해시 조립 단언, GroupRoot/GroupElement 추론 object dispatch, 요소 헤더 플래그(GroupElement 니블)·해시 ID 3바이트 빅엔디언 와이어 검증. 4체크 통과.

## 2026-09-09 (README 전면 재작성)

- 루트 `README.md` 벤치마크 섹션에 경쟁제 비교 상세 확장 — `Performance-Comparison` 전체 이관: 4종 형태 × 직렬화/역직렬화 속도 8행(플랫·문자열 헤비·그래프·대형, MemoryPack 1.21.4 / MessagePack-CSharp 3.1.4, 동일 머신·동일 BDN Job), GC 카운터 할당 4행, 와이어 크기 4행, 그래프 형태 상이·프레이밍 포함 여부·변동성 공정성 주의, 라이브러리별 특성 노트. 결론 문구(전 영역 최속~동급, 참조 추적 수행하며 트리 변형보다 빠름) 포함.
- 루트 `README.md` 를 영어 사용자용 문서로 전면 재작성 — 기존 한국어 개요(54줄)를 교체. 필수 섹션: QuickStart(설치 + `[StandaloneMessage]` 최소 예제, `SerializePooled` 풀링 경로)·F1–F10 전 기능 사용 가이드(와이어 헤더, 메시지 종류/카테고리, 멤버 타입, 멤버 제어, 컴파일 타임 코드 생성, 런타임 `MessageSerializer`, 성능 계약, 호환성, 패키지 구성, 검증 산출물)·주의 사항(`PooledBuffer` 정확 1회 반납, 불신 입력 진입 거부, 중첩 깊이 상한 64, Unity 폴백 프로필)·벤치마크 실측(`Performance-Baseline` 큐레이션 표 + bench-compare 경쟁제 비교, 측정 환경 명기, raw 아티팩트 덮어쓰기 주의 공개). 작성 서브에이전트 + 신규 관점 리뷰어 서브에이전트 2라운드 반복(round 1 ISSUES: serializer 스니펫 using 누락·벤치마크 출처 불명확 → 수정 → round 2 CLEAN), 리뷰 아티팩트 `artifacts/readme-review-round-1.md`·`-round-2.md`·`readme-review.md`.

## 2026-09-09 (성능 비교 실측)

- [Performance-Comparison](../04-Improvements/Performance-Comparison.md) 신규 — DS_MessageProtocol vs MemoryPack 1.21.4 · MessagePack-CSharp 3.1.4 동일 머신·동일 BDN Job 실측(4종 메시지 형태 × 직렬화/역직렬화 24항목). 결론: 전 영역 DS 최속~동급(경쟁이 더 빠른 항목 없음), 기본 할당·와이어 동급, 공유 그래프 참조 추적·`SerializePooled`·헤더 내장은 DS 유일 이점. 그래프 비교 시 경쟁 2종의 참조 추적 미지원으로 트리 변형 측정임을 명시. 측정 프로젝트는 gitignored 일회성(`artifacts/bench-compare/`), 저장소 추적 파일 무변경·테스트 309/309 × 2 TFM 유지 확인.

## 2026-09-09 (기능 비교 — 미지원 기능 후보 등록)

- `Feature-Spec` 범위 밖에 "추후 재검토 후 구현 예정" 절 신설 — MessagePack·MemoryPack 기능 비교(2026-09-09)에서 미지원으로 확인된 4가지(`Dictionary`·nullable 멤버 타입, varint, 스트리밍 I/O, 기존 인스턴스 역직렬화)를 구현 후보로 등록하고 구현 시 고려 사항(와이어 호환 여부 등)을 명시. 스키마 진화(ADR-0006 확정)·압축·Typeless 등 나머지는 여전히 범위 밖 유지.

## 2026-09-09 (상용화 개선 패스)

- KI-42 해결 — 교차 어셈블리 파생의 `new` 수식어 오남용: 메타데이터(참조 DLL) abstract 베이스에서도 무조건 `new` 를 붙여 CS0109 4건/타입, 구체 메타데이터 베이스에서도 internal `Initialize()` 로 1건/타입 — `TreatWarningsAsErrors` 소비자(프로토콜 DLL + 서버/클라이언트 DLL 분리 표준 구성) 빌드 실패. `BaseEmitsStaticContract` 가 abstract 를 메타데이터만으로 먼저 걸러내고 `Initialize` 의 `new` 는 소스 베이스에서만(`isModuleInitializer` 플래그). 회귀 테스트 4개(베이스를 별도 어셈블리로 컴파일하는 `RunGeneratorWithMetadataBase` 헬퍼 — 실패 사전 확인, IVT 개방/비개방 쌍 포함). Serialize 오버로드는 첫 인자 타입이 타입마다 달라 가릴 수 없음을 확정.
- `DeserializeExact` 추가 — 전체 소비 검사 역직렬화 옵션 API(제네릭·object dispatch): 남은 바이트가 있으면 `InvalidDataException` — [Commercial-Readiness-Review](../04-Improvements/Commercial-Readiness-Review.md) 권고 조치 2 이행. 스키마 표류(ADR-0006 레이아웃 동결 위반·필드 제거)를 조용한 데이터 유실 대신 fail-fast 로 전환, 와이어 무영향. 회귀 테스트 5개(깨끗한 프레임 왕복·남은 바이트 거부·기본 `Deserialize` 의 접미 허용 대조군·제네릭 구성 프레임).
- 리뷰 라운드 보강 — KI-42 IVT 고리: 베이스 어셈블리가 `[InternalsVisibleTo]` 로 소비자에게 internal 을 열면 Initialize `new` 를 빼도 CS0108 이 생성 코드에 떠 빌드가 깨진다(이번 패스가 도입한 퇴행 고리). `GivesAccessTo` 로 접근 판별해 개방 베이스는 `new` 유지로 교정(회귀 2). `DeserializeExact` 커버리지 맹점 폐쇄: netstandard2.1(Unity) 폴백 프로필 실행 검증 2·빈 span `ArgumentException` 계약 1·NonId 1바이트 헤더 프레임 계약 1. `Public-API` frontmatter 날짜 정정. 테스트 296→309(net8.0·net9.0), 클린 리빌드 경고 0(기존 테스트 분석기 경고만), Sandbox 42 통과. `Public-API`·`Commercial-Readiness-Review` 갱신.

## 2026-09-09 (ADR — 스키마 진화 미지원)

- **ADR-0006: 와이어 스키마 진화 미지원 확정** — 버전 내성 스키마 제안([Proposal-VersionTolerantSchema](../02-Architecture/Proposal-VersionTolerantSchema.md))을 기각. Unity·서버가 같은 라이브러리(메시지 계약)를 함께 소비하므로 버전 혼합 피어가 없어 진화가 불필요. 위치 기반 와이어 유지, 메시지 레이아웃 동결·스키마 변경은 신규 MessageId 타입 추가로 하는 운영 규칙을 공식 컨벤션으로 확정. 제안 노트 기각 상태 전환, PENDING major 결정 항목 종결, [Commercial-Readiness-Review](../04-Improvements/Commercial-Readiness-Review.md) §1 에 ADR 링크 추가. 재검토 조건: 외부 배포(제3자 클라이언트) 전환 시 새 ADR.

## 2026-09-08 (상용화 검토)

- 상용화 적합성 검토 실시 ([Commercial-Readiness-Review](../04-Improvements/Commercial-Readiness-Review.md)) — 빌드·테스트 296×2·Sandbox 42·벤치마크 실측 및 와이어 호환성 실증 프로브. 결론: 채용 가능, 단 스키마 진화 미지원(멤버 제거·순서 변경 시 조용한 손상 실증)에 따른 운영 규칙(레이아웃 동결·신규 MessageId 확장) 정착 필요. Unity 에디터 소스 생성기 Roslyn 호환 검증 항목 추가.

## 2026-09-08 (2.3.9 릴리스)

- **2.3.9 릴리스** (태그 `v2.3.9`) — 패치: 적대적/QA 계약 스위트(패키지 바이트는 2.3.8 과 동일 — 제품 코드 무변경). 목적: ① 계약 스위트의 소비자 가시화 ② **새 테스트들의 CI(Ubuntu) 환경 검증** — 동시 스트레스트가 다른 하드웨어·OS에서, 퍼저가 CI 러너에서 굴러보는 것은 로컬 검증을 대체하는 독립 증거다. 포함 6커밋: 폴백 프로파일 퍼징(`de14402`)·캠페인 노브+15배 캠페인(`827955b`·`525e9cb`)·중단 경로 풀 무결성(`2189e77`)·이형 소비자 형태 행렬(`2d39342`). 테스트 286→296, Sandbox 42 통과, DS_RPC 로컬 팩 빌드+테스트 통과 후 태그 푸시. — **CI(Ubuntu) 검증 완료**: 게이트 통과(동시 스트레스·퍼저·이형 행렬 포함 296×2 TFM) 후 3패키지 푸시 확인 — 새 스위트의 독립 환경 증거 확보.

## 2026-09-09 (사이클 32) — 게시 아티팩트 검증 완료

- **nuget.org 게시 2.3.9 소비 검증 완료** — 색인 전파 확인(flatcontainer 목록·직접 nupkg GET 200, 50KB) 후 DRPC 전체 솔루션을 순수 nuget.org 소스로 2.3.9 복원(전역 캐시 제거·`--no-cache --force` — 캐시 히트가 아닌 진짜 다운로드 강제): 복원·빌드 0 오류, 테스트 58/58 통과. 게시 아티팩트의 소비자 이행 검증 완료 — 로컬 팩 회귀·CI 게이트에 이어 마지막 검증 고리. 함께: 발행 후 실재 확인 절차(flatcontainer 목록 + 직접 GET 200 으로 릴리스 종결)를 Packages.md 릴리스 절차에 정식화(`b414f73`), PENDING 해당 항목 종결.

## 2026-09-08 (사이클 31) — 게시 아티팩트 검증 시도

- **nuget.org 게시 2.3.9 소비 검증 시도 — 인덱스 전파 대기로 이행(PENDING 등록)**: DS_RPC 를 순수 nuget.org 소스로 2.3.9 복원 시도(전역 캐시 제거·`--no-cache --force`). 결과: 인덱스가 아직 2.3.8 까지만 나열(NU1102, "가장 가까운 버전: 2.3.8") — 업로드 수락(Actions "Your package was pushed" x3)과 색인 전파의 시차. 검증 명령을 PENDING 에 기록해 전파 후 재실행. 이 시도가 밝힌 것: 그간의 "DS_RPC 회귀"는 전부 로컬 팩 기반이었고 게시 아티팩트 직접 소비는 이번이 첫 시도였다 — 캐시 히트를 진짜 다운로드로 오인할 뻔한 함정도 캐시 제거로 방어됐다.

## 2026-09-08 (사이클 30) — 생성기: 이형 소비자 형태 행렬

- **엑조틱 멤버/타입 모양 행렬 7건** — 합법적이지만 희귀한 소비자 형태가 깨끗한 진단(AD0001 아님)·정상 생성 중 하나로만 끝나는지 고정: 랭크-2 배열·`Span<int>`(ref struct)·튜플·`IntPtr`·`Task<int>` 멤버 → MSGPROT006, 가변 struct 메시지 → 정상 생성(값타입 루트 경로), `static`/`const` 멤버 → 와이어 제외(멤버 접근 한정 검증). 과정에서 계약 정정 2건: **readonly struct 는 메시지가 될 수 없다**(무매개변수 생성·할당 가능 멤버 요건이 모순 — init 세터는 MSGPROT011), 진단 발생 시 스텁 방출 여부는 구현 세부(계약 아님). 테스트 289→296, Sandbox 42 통과.

## 2026-09-08 (사이클 29) — 쓰기 측: 중단 경로 풀 무결성

- **직렬화 중단(게터 예외) 후 풀 무결성 통합 검증 3건** — 대여 버퍼가 부분 기록 상태로 풀에 돌아가는 예외 경로(finally-dispose)에서 이중 반납·누수·오염이 있으면 후속 직렬화가 조용히 망가진다. 중단(원인 예외 무포장 전파 확인) 직후 연속 왕복 20×10(byte[] 경로)·10(PooledBuffer 경로)·캐시 스며듬 없음을 고정 — 풀 순환 아래 실동작 검증(단위 가드의 합이 아님). 결과: **오염 0**(측정 기록). 테스트 286→289, Sandbox 42 통과.

## 2026-09-08 (사이클 28) — 퍼저: 심층 캠페인

- **퍼저 캠페인 노브 + 15배 심층 캠페인** — `MSGPROT_FUZZ_SCALE` 환경변수(기본 1, CI 속도 유지)로 온디맨드 심층 탐사 추가. 15배 캠페인 실행: 7시드×3만 변이×2진입 ≈ **42만 판독, 양 TFM 위반 0** — 상시 회귀(2천/시드)가 잡지 못하는 결함 꼬리의 깊은 구간까지 정화 확인(측정 기록). 테스트 286, Sandbox 42 통과.

## 2026-09-08 (사이클 27) — 퍼저: 폴백 프로파일

- **퍼저 코퍼스에 netstandard2.1(Unity) 폴백 생성 코드 추가** — Tests(net8/9) 코퍼스는 CollectionsMarshal 경로만 변이시켰고, 인덱서 루프 폴백 판독기는 변이 하에서 굴러본 적이 없었다. `FallbackCollections`(폴백 List·IList·배열 5형태 + NaN/-0.0/∞ 페이로드)를 7번째 시드로 추가 — 7×2,000×2 변이에서 **위반 0**(폴백 경계 유지 확인, 측정 기록). 테스트 286, Sandbox 42 통과.

## 2026-09-08 (2.3.8 릴리스)

- **2.3.8 릴리스** (태그 `v2.3.8`) — 패치: 릴리스 파이프라인 게이트 + 테스트 인프라. 패키지 바이트는 2.3.7 과 동일(제품 코드 무변경)하나, **새 CI 게이트(빌드→테스트 양 TFM→Sandbox 인수→pack→push)를 실동작 검증**하는 것이 이 릴리스의 목적이다 — 파이프라인 결함은 코드 릴리스 시점이 아니라 지금 발견되어야 한다. 포함: 퍼저 심화(`ca498e0`)·경계값 왕복 계약(`cd88644`)·동시 항패스 스트레스(`db155cc`)·버전 내성 스키마 제안 노트(`1d71a43`)·CI 게이트(`1f3e58a`). 테스트 286(246→286), Sandbox 42 통과, DS_RPC 로컬 팩 빌드+테스트 통과 후 태그 푸시.

## 2026-09-08 (사이클 26) — 릴리스 파이프라인 강화

- **nuget-publish.yml 에 게이트 추가** — 태그 푸시 파이프라인이 빌드만 하고 **테스트 없이** 패키지를 발행했다(로컬 게이트는 절차를 지켰음을 보장하지만 파이프라인 자체가 스스로 검증해야 한다). `dotnet test`(양 TFM)·Sandbox 인수 시나리오 단계를 Pack 앞에 삽입, 런타임에 net8.0 도 설치(테스트가 멀티 TFM). 함께 3개 과잉 길이 라인 래핑·LF 정규화. 다음 태그 푸시부터 게이트가 실동작한다. 로컬 검증: YAML 파싱·단계 순서 확인, 전체 스위트 286×2·Sandbox 42 통과. — **v2.3.8(2026-09-08) 태그로 실동작 검증 완료**: CI 로그에 양 TFM 테스트 실행·게이트 통과 후 푸시 확인.

## 2026-09-08 (사이클 25) — 기능 영역: 제안 노트

- **버전 내성 스키마 제안 노트 작성** — 기능 영역의 스펙 규정 산출물 형식("Document 제안 노트에 기록")으로 `02-Architecture/Proposal-VersionTolerantSchema.md` 신설: 문제 정의(롤링 배포·위치 기반 와이어의 구조적 취약), 옵션 3종(필드 태그+Skip 권장 / 프레임 버전 / 부가 전용 규약), 마이그레이션(양쪽 형식 공존 판독기), 검증 계획(퍼저 코퍼스·신규 진단·기준선 재측정). IBufferWriter 진입은 대안 후보로 병기. major 결정 2건 PENDING.md 등록 — 코드 무변경.

## 2026-09-08 (사이클 24) — 스레드 안전성: 동시 핫패스 스트레스

- **혼합 타입 동시 왕복 스트레스 상주화** — KI-38·39 는 추론으로 고쳤지만 기능적 오염(캐시/디스패치의 타입 경계 누수 — 가장 파국적 무음 손상)을 잡는 지속 트래픽 그물이 없었다. 6스레드×2만 왕복(제네릭 진입 4모드: 기본형·참조 그래프+공유·전체 원시형 매트릭스 / object 디스패치 교대)으로 스레드별 메시지가 자기 타입·자기 값으로만 왕복함을 검증, 스레드별 고정 시드로 재현 가능. 결과: **오염 0**(x86 — ARM 순서 결함은 KI-39 추론이 담당). 테스트 285→286, Sandbox 42 통과.

## 2026-09-08 (사이클 23) — 정확성: 경계값 왕복

- **IEEE-754·decimal·char 경계값 왕복 계약 고정(39 이론)** — 값 동일성(==)이 지나치는 무음 손상 클래스를 비트 오라클로 검증: 부호 있는 0(-0.0 vs +0.0 — == 로 구분 불가), NaN 페이로드(quiet/signaling·음수 페이로드), ±∞, denormal 최소·최대, float/double MaxValue, decimal 스케일 보존(후행 0·scale 28·최대 유효숫자, GetBits 왕복), NUL 포함 문자열·서로게이트 쌍·U+FFFF. 결과: **전 패턴 비트 보존 확인**(측정된 부정 — 와이어 원시 비트 복사가 정규화 없이 유지됨을 계약화). 테스트 246→285, Sandbox 42 통과.

## 2026-09-08 (사이클 22) — 퍼저 심화

- **차등 퍼저 심화(코퍼스 3→6·변이 3→5·진입 1→2)** — NonId·그룹 요소·깊이 60 체인 시드와 다중 비트(길이 접두 동시 타격)·가비지 접미 변이 추가, 제네릭 진입 경로(이형 헤더 → KI-5 검증) 병행 압박. 6×2,000×2 변이에서 신규 위반 0 — KI-41 수정 후 경계가 심화 코퍼스에서도 유지됨을 확인(측정된 부정 결과로 기록). 테스트 246, Sandbox 42 통과.

## 2026-09-08 (2.3.7 릴리스)

- **2.3.7 릴리스** (태그 `v2.3.7`) — 패치: 퍼저 발견 신뢰 경계 교정 2건(KI-41). 런타임 디스패치 판독의 신규 객체 분기 블라인드 캐스트를 안내 `InvalidDataException` 검사로 교정(불신 헤더가 다른 등록 타입으로 라우팅될 때 원인 없는 `InvalidCastException` 방지), NonId 플래그 프레임 거부를 `InvalidCastException` → `InvalidDataException` 으로 유형 교정(와이어 내용 불법 분류 — 모니터링 소비자는 이 유형을 따라가야 함). 역직렬화 차등 퍼저 상주 회귀화. **호환 주보**: NonId 프레임 object dispatch 거부 예외 유형이 바뀌었다 — `InvalidCastException` 을 잡던 소비자는 `InvalidDataException` 으로 갱신. 커밋 `7e3cecd`. 테스트 246, Sandbox 42 통과, DS_RPC 로컬 팩 빌드+테스트 통과 후 태그 푸시.

## 2026-09-08 (사이클 21) — 신뢰 경계 퍼징

- **역직렬화 차등 퍼저 도입 + 결함 2건 발견·수정(KI-41)** — 결정적 변이(3종 프레임×1,500)로 통합 불변식 검증(깨끗한 거부 예외 유형 한정·성공 판독 멱등 왕복). iter 116: 디스패치 판독 신규 객체 분기의 블라인드 캐스트가 불신 헤더 라우팅에서 원인 없는 `InvalidCastException`(KI-34 미커버 분기) → 안내 검사로 교정. iter 291: NonId 플래그 거부가 `InvalidCastException` 오유형 → `InvalidDataException`(계약 재고정: 테스트 2·Sandbox S2·Public-API). 퍼저 상주 회귀화. 테스트 245→246, Sandbox 42 통과, DS_RPC 로컬 팩(2.3.6-ki41) 통과.

## 2026-09-08 (2.3.6 릴리스)

- **2.3.6 릴리스** (태그 `v2.3.6`) — 패치: 진단 품질·소비자 문서. 오류형 `ClassId = <error>` 표현식의 "missing 'ClassId'" 2차 오안내 제거(1차 컴파일 오류가 원인 지목)·MSGPROT005 폴백 속성명 수정(`f82b244`). README 에 게임 서버 운영 참고 섹션 추가 — `SerializePooled` 권장(측정 104B→32B)·역직렬화 신뢰 경계 계약·기준선 문서 안내; README 는 패키지에 포함되어 배포됨(`0614965`). 아티팩트 실물 감사(분석기 배치·Core 이중 TFM 검증)는 Packages.md 에 기록. 커밋 `f82b244`·`0614965`. 테스트 245, Sandbox 42 통과, DS_RPC 로컬 팩 빌드+테스트 통과 후 태그 푸시.

## 2026-09-08 (사이클 20) — 패키징·소비자 문서

- **nupkg 실물 감사(이 루프 미실시 영역)** — 2.3.5 패키지 압축 해제로 F8·F9·Packages.md 주장을 아티팩트 수준에서 검증: 분석기 `analyzers/dotnet/cs` 배치·README 포함·Core 이중 TFM(유일한 `#if` 는 ModuleInitializer 폴백이라 net6.0 자산의 존재 이유가 명확)·파사드 NS2.1 단일은 의도 — **불일치 없음**, 검증 결과를 Packages.md 에 기록.
- **README 운영 참고 섹션 추가** — 이 루프에서 확립된 소비자 계약을 NuGet 랜딩 페이지에 반영: `SerializePooled` 권장(측정 수치 104B→32B·사본 반납 1회 보장), 역직렬화 신뢰 경계 계약(불신 입력 진입 거부·깊이 상한 탈출구), 기준선 문서 안내. README 는 패키지에 포함되므로 다음 릴리스부터 소비자에게 배포됨.

## 2026-09-08 (사이클 19) — 진단 품질

- **메타데이터 감사 LOW 2건 종결(원장 잔여 실행 가능 항목 소진)** — 오류형 `ClassId = <error>`(선언되지 않은 상수)가 "missing 'ClassId'" 2차 오안내를 보태던 것을 항목 건너뛰기로 교정(컴파일러 1차 오류가 원인 지목, 회귀 테스트 1개 — CS0103 존재·오안내 부재 단언), MSGPROT005 폴백 속성명을 "MessageAttribute" 로 수정(사실상 사 경로). 테스트 244→245, Sandbox 42 통과. PENDING.md 실행 가능 항목 0.

## 2026-09-08 (2.3.5 릴리스)

- **2.3.5 릴리스** (태그 `v2.3.5`) — 패치: KI-40 힌트 이름 충돌 크래시 수정. 중첩 타입(`Ns.A+B`)과 밑줄 조인 이름의 탑레벨 타입(`Ns.A_B`)이 같은 힌트 이름을 만들어 `AddSource` 중복 예외 → **AD0001(해당 컴파일 생성 소스 전체 유실)** 를 `+` 보존으로 차단. 중첩 메시지 타입의 힌트 파일명만 변경(`Ns.A_B.g.cs` → `Ns.A+B.g.cs`), 생성 내용·와이어 불변. 커밋 `c2824f8`. 테스트 244, Sandbox 42 통과, DS_RPC 로컬 팩 빌드+테스트 통과 후 태그 푸시. 함께 포함(미릴리스였던 테스트 2건): 벤치마크 대형 컬렉션 기준선(`8a13809`), Sandbox S14 신뢰 경계 시나리오(`310c9e3`).

## 2026-09-08 (사이클 18) — 생성기 강건성

- `Known-Issues` KI-40 해결 — 메타데이터 레이어 감사(마지막 미스윕 영역)에서 발견된 유일한 크래시 결함: 힌트 이름 `+`→`_` 치환이 중첩 타입과 밑줄 이름 타입의 충돌을 만들어 AddSource 중복 예외 → AD0001(컴파일 생성 소스 전체 유실). `+` 허용으로 단사성 확보(중첩 메시지 힌트 파일명만 변경, 생성 내용 불변). 회귀 테스트 1개, 테스트 243→244, Sandbox 42 통과. — 루프 종료 시점 커밋(미릴리스, 다음 릴리스에 포함).

## 2026-09-08 (사이클 17) — 성능 영역

- **대형 컬렉션 기준선 측정(기준선 갭 잔여 1건 폐쇄)** — `List<int>` 10만(벌크 복사 경로) + `string[]` 1천(요소별 경로): 직렬화 71µs/411KB·역직렬화 134µs/448KB. 바이트 당 ~0.18ns 로 벌크 경로가 지배함을 수치로 확인, 대형 배치 송신에서 `SerializePooled` 로 결과 복사를 회피하는 안내를 수치와 함께 기록. 잔여 갭은 net8.0 대조뿐(낮은 가치로 명시). 벤치마크 시나리오 10개 전체 재측정·저장.

## 2026-09-08 (사이클 16) — 인수 조건

- **Sandbox S14 신뢰 경계 거부 시나리오 3개** — 인수 하네스가 이번 루프의 신뢰 경계 강화를 아직 고정하지 않았던 갭 폐쇄: 이형 타입 바이트 헤더 거부(KI-5)·위조 NonId 헤더 거부(KI-5 우회 시도)·규격 밖 참조 태그 즉시 거부(KI-36). 시나리오 42개 전체 통과. 함께 F10 문서-코드 불일치(시나리오 개수 38 표기 vs 실제) 수정 — 감사 원장 마이너 지적 종결.

## 2026-09-08 (사이클 15) — 성능 영역

- **기준선 갭 폐쇄(풀링 경로·참조 그래프)** — 벤치마크 3개 추가: `SerializePooledFlat`(동일 워크로드에서 byte[] 경로 104B 대비 **32B**, 속도 동급 — `SerializePooled` 권장의 수치 근거 확보), `SerializeSharedGraph`·`DeserializeSharedGraph`(깊이 5 체인 + 공유 서브트리로 백레퍼런스 강제 — 그래프 경로는 단순 메시지 대비 약 5배, 그래프 모양 설계 참고치). `Performance-Baseline.md` 표 갱신·갭 목록에서 해당 2건 제거(잔여: 대형 컬렉션·net8.0 대조).

## 2026-09-08 (2.3.4 릴리스)

- **2.3.4 릴리스** (태그 `v2.3.4`) — 패치: 스레드 안전성 2건 + 계약 고정. 델리게이트 등록 경로의 TOCTOU(검증→prefill→클레임 순서)로 같은 타입 동시 등록 시 패자의 델리게이트가 캐시에 잔류하던 결함을 클레임 선점으로 차단(KI-38), `SerializerCache<T>` 필드 volatile 화로 ARM(Unity) 지연 가시성 해소(KI-39), 제네릭 reader 제거 순서 미러링. 진입점 계약 가드 테스트 13개(직렬화 null 가드·미등록 안내·`Create(<=0)`·`FromRented`). 커밋 `8b101f6`·`c3908b7`·`2bb2eaa`. 테스트 230→243, Sandbox 38 통과, DS_RPC 로컬 팩 빌드+테스트 통과 후 태그 푸시.

## 2026-09-08 (사이클 14) — 테스트 갭 폐쇄

- **진입점 계약 가드 테스트 일괄 폐쇄(13개)** — 2026-09-08 감사 목록화 건: 직렬화 진입점 null 가드 6개(제네릭 ref-writer·byte[]·Pooled + object 3종, `ArgumentNullException`·ParamName 고정), `SerializeToWriter` 미등록 타입 안내 예외(실제 계약은 `RegisterType` 지연 경유의 `IMessageSerializable` 안내 — 테스트 작성 중 실제 메시지로 교정), `Create(0/-1/int.MinValue)` 빈 버퍼 시작→첫 쓰기 증설 왕복(3), `PooledBuffer.FromRented` 인자 검증 3개(null·길이 초과·음수). 구현은 이미 정상이라 코드 변화 없음 — 계약을 실행으로 고정. 테스트 230→243, Sandbox 38 통과. (도중 1건 오탐 메시지 가정으로 실패 — 실제 예외 경로 확인 후 교정, 전체 통과.)

## 2026-09-08 (사이클 13) — 스레드 안전성

- `Known-Issues` KI-39 해결 — `SerializerCache<T>` 복구 블록의 묶음 발행(`Volatile.Write(Serialize)`)이 같은 위치를 읽는 독자하고만 짝이 되어, `Deserialize`/`MessageId` 만 읽는 핫 경로가 ARM(Unity)에서 등록 완료 후에도 오래된 null/0 을 읽을 수 있던 지연 가시성. 캐시 필드 6개 전체 volatile 화(위치별 release/acquire 쌍) + 복구 블록 자연순 단순화. 관찰 불가결(ARM 필요)이라 메모리 모델 추론으로 인자화, 게이트는 전체 스위트·Sandbox·DS_RPC 로컬 팩(2.3.3-ki39) 통과.

## 2026-09-08 (사이클 12) — 스레드 안전성

- `Known-Issues` KI-38 해결 — 델리게이트 등록 경로의 TOCTOU(검증→prefill→클레임 순서)로 같은 타입 동시 등록 시 패자의 델리게이트가 `SerializerCache<T>` 에 잔류, 거부된 등록의 직렬화기가 조용히 실행되던 결함. 클레임 선점 후 prefill(+실패 롤백)로 교정, 제네릭 reader 제거 순서를 등록 역순으로 미러링. 회귀 테스트 2개(동시 등록 경쟁·클레임 롤백), 테스트 228→230, Sandbox 38, DS_RPC 로컬 팩(2.3.3-ki38) 통과. KI-39(캐시 복구 블록 ARM 지연 가시성, LOW) 원장 등록 — 다음 사이클 후보. 테스트 갭 4건 목록화(직렬화 null 가드·`Create(<=0)`·`FromRented` 검증 — 코드는 이미 정상, 테스트만 부재).

## 2026-09-08 (2.3.3 릴리스)

- **2.3.3 릴리스** (태그 `v2.3.3`) — 패치: 생성기 내부 구조 클러스터(2026-09-08 구조 감사 FINDING 1~5 전항목, 행동 보존). 참조 추적 방출 골격 단일 사실원화, 원시형 와이어 표 4→1 통합, 제네릭 구성 하위 기능 분리 + 충돌 상태 캡슐화, 와이어 MessageId 바이트 분해 공유 — **모두 골든 비교로 생성 바이트 불변 검증**(29개 `.g.cs` 바이트 동일). 공개 API·와이어 형식 무변화. `MessageCodeGenerator` 878→483줄. 커밋 `185f426`·`748715c`·`1da1e5b`·`967a225`. 테스트 228 유지·전체 통과, Sandbox 38 통과, DS_RPC 로컬 팩 빌드+테스트 통과 후 태그 푸시.

## 2026-09-08 (사이클 11) — 구조 영역

- **와이어 MessageId 바이트 분해 단일 사실원화** — `Method.cs` 에서 `EmitSerialize` 가 바이크하고 `EmitDeserialize` 가 검증하던 4바이트 분해를 `DecomposeWireId` 하나로 통합(구조 감사 FINDING 5, 마지막 잔여 항목). id 레이아웃 변경 시 한쪽만 고쳐 쓰는 바이트를 읽는 쪽이 거부하는 자기부정 버그 클래스 제거. **골든 비교 29개 `.g.cs` 바이트 동일**, 테스트 228 통과, Sandbox 38 통과. — 구조 감사(2026-09-08) FINDING 1~5 전항목 완료.

## 2026-09-08 (사이클 10) — 구조 영역

- **제네릭 구성 하위 기능 분리 + 충돌 상태 캡슐화** — `MessageCodeGenerator`(878줄)에서 제네릭 구성 등록 하위 기능(파싱·검증·충돌 수집·등록 캐리어 방출, ~420줄)을 `GenericConstruction.cs` 로 추출(구조 감사 FINDING 3). MSGPROT008/014/015·KI-27/31/32 규약이 하나의 단위로 찾아지고, `Generate` 는 4개 진입점 호출로만 결합. 함께 `ConstructionConflicts` 의 공개 가변 사전 4개를 비공개 필드 + 내부 뮤테이터로 캡슐화(FINDING 4) — KI-31 "실제 등록될 형태만 센다" 불변식의 사후 우회를 구조적으로 차단. **골든 비교 29개 `.g.cs` 바이트 동일**(순수 이동·행동 보존), 테스트 228 통과, Sandbox 38 통과. 잔여: FINDING 5(MessageId 바이트 베이킹 공유).

## 2026-09-08 (사이클 9) — 구조 영역

- **원시형 와이어 표 단일 사실원화** — `Member.cs` 의 4개 독립 스위치(읽기 식·쓰기 호출·고정 크기·벌크 크기)를 `PrimitiveWireTable` 하나로 통합(구조 감사 FINDING 2). 프리미티브 추가·수정이 4곳 조율이 아닌 1행 편집이 되고, 읽기·쓰기 조용한 불일치(와이어 표류) 버그 클래스가 구조적으로 불가능해진다. 불리언(패킹 불가)·decimal(20바이트 표현)·문자열(가변)은 고정/벌크 열로 의미를 명문화. **골든 비교 29개 `.g.cs` 바이트 동일**(스태시 왕복 A/B). 잔여: FINDING 3(MessageCodeGenerator SRP 분리)·4(ConstructionConflicts 캡슐화)·5(MessageId 바이트 베이킹 공유).

## 2026-09-08 (사이클 8) — 구조 영역

- **생성기 참조 추적 방출 단일 사실원화** — `Member.cs` 의 참조 추적 3경로(그래프 내부·밖 위임·런타임 디스패치) × 쓰기·판독 6곳 수작업 복제(~350줄, KI-34·KI-36 안내 메시지 문자열 3중 복제, 등록 문장 순서 이미 표류)를 골격 헬퍼 2개(`EmitTrackedReferenceWrite`·`EmitTrackedReferenceRead`) + 경로별 차이 전달 래퍼로 통합. **골든 비교로 생성 바이트 불변 검증**(Tests 27 + NetStandardFixtures 폴백 2 = 29개 `.g.cs` 바이트 동일 — 스태시 왕복 A/B 빌드). 향후 참조 프로토콜 변경(예: ReferenceKind 추가)은 6곳이 아닌 1곳 수정. 구조 감사(2026-09-08 스카우트) FINDING 1; 잔여 FINDING 2~5(원시형 스위치 표 4중 복제·MessageCodeGenerator SRP 분리·ConstructionConflicts 캡슐화·MessageId 바이트 베이킹 공유)는 다음 사이클 후보. Feature-Spec F1–F10 전수 대조: 불일치 없음(수 시나리오 개수 표기 마이너).

## 2026-09-08 (2.3.2 릴리스)

- **2.3.2 릴리스** (태그 `v2.3.2`) — 패치: 역직렬화 진입 검증 + 성능 기준선. 생성 `Deserialize(ref reader)` 가 헤더를 타입 MessageId 와 비교해 다른 타입 바이트·위조 NonId 헤더·변조 id 의 조용한 재해석을 진입에서 `InvalidDataException` 으로 거부(KI-5, 원장 최초 등록 항목 해소 — 와이어 불변, 합법 프레임 그대로 복호). 성능: 직렬화 핫 경로 기준선 최초 기록(문자열 많은 시나리오 포함 5개, `03-Reference/Performance-Baseline.md`), WriteString ASCII 사전 스캔 가설 측정 기각(+68% 회귀, 재시도 금지 근거 명시), 참조 추적 사전 초기 용량 8. 커밋 `a2eb7b9`·`a1eb829`. 테스트 224→228, Sandbox 38 통과, DS_RPC 로컬 팩 빌드+테스트 통과 후 태그 푸시.

## 2026-09-08 (사이클 7) — 성능 영역

- **성능 기준선 최초 기록** — `Test/MessageProtocol.Benchmarks` 에 문자열 많은 시나리오 2개 추가(`StringHeavyMessage` — ASCII 100자×4, 게임 서버 실제 프로파일)하고 5개 시나리오 기준선을 `03-Reference/Performance-Baseline.md` 에 최초 기록(이전까지 기록된 수치 없음).
- **측정된 부정 결과 확정** — `WriteString` ASCII 사전 스캔(정확 용량 산정) 가설을 실험해 **기각**: 144→243 ns(+68%), 이중 스캔이 회피한 Grow 비용보다 컸다. 되돌림 후 139 ns 회복. 재시도 금지 근거를 기준선 노트에 명시(SIMD 재시도는 양수 수치 동반 시에만).
- **컨텍스트 사전 초기 용량 8** — 참조 추적 승격 사전의 리사이즈(1→3→7) 제거(핫패스 감사 FINDING 3, 할당 변화 없음). 테스트 228 유지·전체 통과.

## 2026-09-08 (사이클 6)

- `Known-Issues` KI-5 해결(원장 최초 등록 항목) — 생성 `Deserialize(ref reader)` 의 헤더 무비교 skip 이 다른 타입 바이트를 조용히 재해석하던 결함. `EmitSerialize` 와 같은 MessageId 4바이트 상수를 바이크해 프레임 진입에서 비교·거부(런타임 프레임 바이트 기준 분기 유지 — 위조 NonId 헤더도 1바이트 비교로 차단). 호출 경로 전수 확인 후 적용, 수동 구현은 계약 문서로 권장화. 회귀 테스트 4개, 테스트 224→228, Sandbox 38 통과, DS_RPC 로컬 팩(`2.3.1-ki5`) 빌드+테스트(58/58) 통과.

## 2026-09-08 (2.3.1 릴리스)

- **2.3.1 릴리스** (태그 `v2.3.1`) — 패치: 역직렬화 신뢰 경계 2건. 참조 태그 바이트 3–255 를 NewObject 로 조용히 해석하던 검증 우회 차단(KI-36, 생성 판독 3경로 — 와이어 불변, 합법 프레임 그대로 복호), `PooledBuffer`(struct) 사본의 이중 `ArrayPool.Return`(다음 대여자가 남의 데이터를 봄)을 참조형 소유 홀더 공유로 원천 차단(KI-37, 공개 API 표면 불변). 짝 수정: `FromRented` 0길이 배열 풀 반납 제외, `GetSpan(음수)` 계약 예외. 커밋 `7b9f173`·`fb6e814`. 테스트 214→224(net8.0·net9.0), Sandbox 38 통과, DS_RPC 를 로컬 2.3.1 팩으로 빌드+테스트 통과 후 태그 푸시.

## 2026-09-08 (사이클 5)

- `Known-Issues` KI-37 해결 — `PooledBuffer`(struct) 사본의 독립 Dispose 상태가 이중 `ArrayPool.Return`(다음 대여자가 남의 데이터를 봄)을 허용하던 소유권 결함. 소유 상태를 참조형 `Owner` 홀더로 옮겨 사본 전체가 공유 — 반납 정확히 1회, 이후 사본 뷰는 빈 상태(공개 API 표면 불변, 하위호환). 짝 수정: `FromRented` 0길이 배열 풀 반납 제외, `GetSpan(음수)` 계약 예외 명문화. 회귀 테스트 6개, 테스트 218→224(net8.0·net9.0), Sandbox 38 통과, DS_RPC 빌드+테스트 회귀 통과. `Public-API` 사본 의미 문서화.

## 2026-09-08 (사이클 4)

- `Known-Issues` KI-36 신규 해결 — 참조 태그 바이트 3–255 가 생성 판독 코드에서 NewObject 로 조용히 해석되던 검증 우회(그래프 내부·그래프 밖 위임·런타임 디스패치 3경로). 세 `else` 앞에 `NewObject(1)` 검사 분기를 추가해 규격 밖 태그를 즉시 `InvalidDataException` 으로 거부 — 와이어 불변(합법 0/1/2 프레임은 그대로 복호). 회귀 테스트 4개(3경로 태그 3·0xFF 거부 + 정상 왕복 가드), 테스트 214→218(net8.0·net9.0), DS_RPC 빌드+테스트 회귀 통과. 병렬 서브에이전트 감사(컬렉션 가드·writer·참조 그래프 3방향)에서 발견.

## 2026-09-08 (2.3.0 릴리스)

- **2.3.0 릴리스** (태그 `v2.3.0`) — 마이너: `PooledBuffer.Empty`·`Memory`·`UnsafeArraySegment` 의 `[Obsolete(error:false)]` 마킹이 공개 API 표면 변화(경고 추가)라 patch 가 아닌 minor 로 승격. 함께 포함: 진단 중복제거 키 수식(사유·위치 포함), 생성 Deserialize 헤더 규칙 단일 사실원화(와이어 불변), 그래프 헬퍼 방출 순서 결정화, RS2007/RS2008 빌드 게이트. 커밋 `a1cad8a`·`f0353e4`·`b8971cc`·`2af21f8`·`515027c`. 테스트 212→214, Sandbox 38 통과, DS_RPC 를 로컬 2.3.0 팩으로 빌드+테스트(77/77, 경고 0) 통과 후 태그 푸시.

## 2026-09-08 (사이클 3)

- 진단 품질·구조 LOW 클러스터 — `EmitState` 진단 중복제거 키에 사유(kind)·위치 포함(같은 멤버의 MSGPROT006+MSGPROT011 동시 보고), 생성 `Deserialize` 헤더 규칙을 `MessageWireFormat.HasEmbeddedMessageId` 호출로 통일(와이어 불변), 그래프 헬퍼 방출 순서를 타입 이름 정렬로 결정화(BCL Dictionary 열거 의존 제거), `AnalyzerReleases.*.md` 파손(RS2007)을 `WarningsAsErrors` 로 게이트(재발 이력 2회).
- 죽은 API 감사 — `PooledBuffer.Empty`·`Memory`·`UnsafeArraySegment` [Obsolete] 마킹(무참조, 다음 major 제거 후보), `GetSpan` 은 DS_RPC 사용 중으로 유지(+증설 후 별칭 주의 문서화), `PatchInt32`·`Capacity` 는 테스트 사용 중으로 원장 주장 정정, `CategoryMask` 는 F2 함정 문서화로 보류.
- 정책 게이트 종결 — KI-4 파셜 순서 잔여(Known-Issues 꼬리 명문화), KI-10 증분 파이프라인(측정 기반 연기), MSGPROT012 탐지 범위 제한(F5 명문화), 솔루션 Rebuild CS0006 회피법(Packages.md), [Flags] 카테고리 함정(F2) — 원장 5건 종결.

## 2026-09-08 (2.2.1 릴리스)

- **2.2.1 릴리스** (태그 `v2.2.1`) — 모듈 로드 강건성 수정 3건을 담은 패치 릴리스: 등록 검증을 캐시 prefill 앞으로 재배치(거부된 등록의 `SerializerCache<T>` 잔류 차단, KI-11 잔존 해소), HasId id 의 NonId 플래그 조용한 반쪽 등록을 등록 시점 거부로 승격, `RegisterGenericConstruction` 발행 순서를 classId 먼저로 재배치(경쟁 시 ClassId 0 예외 제거, KI-33). 공유 백레퍼런스 판독의 블라인드 캐스트를 안내 `InvalidDataException` 으로 완화(KI-34, 와이어 무영향). 커밋 `765e30b`·`e80cb86`·`cfe7aa8`. 테스트 205→212(net8.0·net9.0), Sandbox 38 시나리오 통과, DS_RPC 를 로컬 2.2.1 팩으로 빌드+테스트(77/77) 통과 후 태그 푸시.

## 2026-09-08

- `Known-Issues` KI-11 잔존 해소·KI-33 신규 해결 — 등록 검증을 prefill 앞으로 재배치(거부된 등록의 캐시 잔류 차단)하고, HasId id 의 NonId 플래그(조용한 반쪽 등록)를 등록 시점 거부로 승격, `RegisterGenericConstruction` 발행 순서를 classId 먼저로 재배치(경쟁 시 ClassId 0 예외 제거). `Public-API` 예외 계약 갱신.
- `Known-Issues` KI-34 신규 완화 + KI-24 꼬리 갱신 — 공유 백레퍼런스 판독의 블라인드 캐스트를 실험으로 확정(`InvalidCastException`)하고 안내 `InvalidDataException` 으로 완화(와이어 무영향). 베이스 멤버 2곳의 조용한 타입 좁힘 복원은 제약으로 고정.
- `Public-API` 갭 4건 보강 — 제네릭 와이어 표면(`GenericIdHeaderSize`·`MessageFlag.Generic`), 속성 표 `GenericMessageAttribute` 행, `RegisterGenericConstruction<T>`·`GetGenericClassId<T>`, `DeserializeFromReader(ref MessageBufferReader)`.

## 2026-09-07 (2.2.0 릴리스)

- **2.2.0 릴리스** (태그 `v2.2.0`) — 신규 진단 규칙 추가(`MSGPROT015` 제네릭 구성 런타임 키 중복)와 디스패치·그래프 밖 위임 멤버의 공유 참조 복원(KI-9)을 포함하는 마이너 릴리스. 2.1.0 이후 3 커밋: `9b9e6b1`(MSGPROT015 컴파일 승격), `ab4053f`(공유 참조 백레퍼런스 복원 — 혼합 버전 피어 주의: 디스패치 멤버 위치의 백레퍼런스는 2.2.0 생성 코드부터 발생하므로 공유 참조 그래프를 주고받는 피어는 양측 재생성 필요), `edc33ee`(문서 정리). 테스트 205개(net8.0·net9.0)·Sandbox 38 시나리오 전체 통과. `Packages.md` 버전 기준 2.2.0 으로 갱신(2.1.0 때 갱신이 누락돼 2.0.0 으로 정체였던 것을 함께 수정).

## 2026-09-07 (추가)

- `Known-Issues` KI-32 신규·해결 — 서로 다른 제네릭 선언의 (MessageId, ClassId) 런타임 키 충돌을 `MSGPROT015` 로 컴파일 승격(모듈 로드 `TypeInitializationException` 예방). `Feature-Spec` F2 유일성 규칙(제네릭 런타임 키 담당 진단 분리)·F5 진단 목록(MSGPROT015) 갱신.
- `Known-Issues` KI-9 해소 — 런타임 디스패치 멤버(`T`·추상 메시지 타입)·그래프 밖 위임 멤버를 통한 공유 참조가 호출측 참조 추적 컨텍스트로 백레퍼런스 기록되어 참조 동일성 복원(와이어는 기존 참조 인코딩 재사용). 잔존 제약(프레임 경계 공유·혼합 버전 피어)은 KI-9 상세로 명문화. KI-25 꼬리 종결 갱신. `Feature-Spec` F3 세 경로 설명·범위 밖 제약 갱신.
- 탑승 LOW 정리 — `Known-Issues` 도입부 통과 기준선을 실측치(테스트 205개·Sandbox 38 시나리오)로 갱신, `AGENTS.md` 문서동기화 트리거에서 존재하지 않는 `Examples/`·`TemplateSource/` 제거, `Feature-Spec` F10 에서 존재하지 않는 'Standalone round-trip 콘솔 샘플' 정정(`Sandbox` 가 실행 검증을 담당).

## 2026-09-07

- **2.1.0 릴리스** (태그 `v2.1.0`) — v2.0.0 이후 26 커밋의 수정을 반영한 유지보수 릴리스. 코드 변경 없음, `Source/Directory.Build.props` 버전만 승격. 주요 포함 내용: 제네릭 구성 관련 수정(ClassId 범위 컴파일 타임 검증 `c60e0df`, 제네릭 구성 헬퍼 이름 충돌(CS0111) 방지 `6629f54`, 고유 접미사 공유 `88d78cd`, 직렬화기 캐시 회복 가능화 `76dc232`), 와이어 안전(중첩 깊이 상한 `01deafd`·`d6bd38a`, UTF-8 엄격 검증 `86859e1`, 음수 접두사·Skip/Advance 거부 `85c9e31`·`d8b75ef`, WriteString 버퍼 long 산술 `93831a8`), 생성기 진단(MSGPROT010/011 `98947ce`, 파생 멤버 유실 경고 MSGPROT012 `09155d9`, 중복 와이어 MessageId MSGPROT013·014 승격 `eb80aa9`·`7341873`), 참조 추적 null 거부(KI-30 `42ed369`), List 선할당 가드 `db86d60`, 인덱서 제외 `dffc137`, abstract 멤버 런타임 디스패치 `6cffea3`, 결정론적 생성(이름 스코핑 `a5e1da2`, 멤버 순서 고정 `b1b6e6f`, 컬렉션 스냅샷 `13dc4af`), netstandard2.1 폴백 경로 테스트 `6292ef9`, 생성 텍스트 안정성 고정 `36ebda9`. 테스트 197(net8.0·net9.0) 전체 통과 확인 후 태그 푸시 → CI(`nuget-publish.yml`)가 2.1.0 패키지 세 개를 게시. 소비 측 동기: DS_RPC 제네릭 프로시저 기능이 GenericMessage 다중 구성 선언에 직접 의존.

## 2026-09-05

- KI-31 해결 (KI-8 작업 중 발견) — **동일 와이어 MessageId 충돌이 모듈 로드 실패로만 드러나던** 사각지대: `[StandaloneMessage(7)]` 두 개처럼 id 를 복사해 붙여도 충돌은 모듈 이니셜라이저의 `_registeredMessageIds` 에서만 발견되어 `InvalidOperationException: Message type with ID … is already registered by '…'` → `TypeInitializationException`(**어셈블리 로드 실패**)이 됐고, 제네릭 구성의 (MessageId, ClassId) 충돌만 MSGPROT008 로 승격돼 있던 비대칭이었다. 컴파일 전체에서 **실제로 등록될 형태**만 골라(`TryGetRegisteredWireMessageId`: 제네릭 선언·NonId·partial 아님·기본 생성 불가·abstract 그룹 루트·MSGPROT005/013 거부 대상 제외) 조립된 ID 별 소유자를 모아(`ConstructionConflicts.MessageIdOwners`) 새 진단 **`MSGPROT014`(Error)** 로 각 타입에 보고·생성 건너뜀 — 메시지에 16진 와이어 ID 와 상대 타입 정규 이름을 실어 원인을 가리키게 했다. 부수: `TypeMetadata.Members` 를 지연 계산으로 전환(속성만 필요한 컴파일 전체 패스에서 후보 전부의 멤버 순회를 하지 않음 — KI-10 측정에서 남은 비용으로 지목된 생성기 CPU 에도 유리). 회귀 테스트 4개(동일 id 두 메시지 + 역방향 가드 3: 카테고리 다르면 무충돌·제네릭은 ClassId 다르면 공존·abstract 루트 제외), 이빨 확인: 게이트 무력화 돌연변이에서 1/4 실패. **지난 턴 KI-8 테스트가 이 변경의 연쇄 오탐(이미 거부될 타입을 소유자로 세어 문제없는 상대까지 차단)을 잡아냈고, 테스트를 약화시키지 않고 게이트를 고쳤다.** 테스트 193→197(net8.0·net9.0), 프로젝트별 클린 리빌드 6/6 경고 0·오류 0, Sandbox 전체 통과. `AnalyzerReleases.Unshipped.md` MSGPROT014 등록(마크다운 자동 서식이 구분 행을 다시 망가뜨려 RS2007 함정 재발 → sed 로 복구 후 무결성 확인), `Known-Issues` KI-31 섹션 추가, `Feature-Spec` F2 MessageId 유일성·F5 MSGPROT014 명문화. 남은 꼬리: 서로 다른 두 제네릭 선언이 같은 MessageId 값 + 같은 ClassId 를 쓰면 런타임 키가 충돌하는데 `CollectConstructionConflicts` 키는 (선언, ClassId) 라 못 잡는다 — 원장 신규 등록.
- KI-8 해결 — 범위 밖 `MessageCategory` 값이 `(byte)(value & 0x0F)` 로 **조용히 마스킹**되던 결함(`MessageCategoryAttribute` 에는 `GenericMessageAttribute.ClassId` 에 있는 검증이 없었다): `TypeMetadataValidator.TryValidateCategoryRange`(상한 = `MessageWireFormat.NibbleMask`) + 새 진단 **`MSGPROT013`(Error)** 로 컴파일 승격, 생성 건너뜀. 실험 검증(저장소 밖 소비자 프로젝트): `[StandaloneMessage(7)] [MessageCategory((MessageCategory)99)]` 와 `[StandaloneMessage(7)] [MessageCategory(Category3)]` 가 99 & 0x0F = 3 이라 **동일 와이어 MessageId 0x23000007** 을 만들고 모듈 로드 시 `InvalidOperationException: Message type with ID 587202567 is already registered by '…'` 로 **어셈블리 로드 실패**(TypeInitializationException) — 오류 메시지는 상대 타입만 지목하고 원인(카테고리 99)은 가리켰다. 회귀 테스트 6케이스(16·99·255 거부 + 실험 재현 시나리오 + 역방향 가드 Category0→`0x20`·Category15→`0x2F` 헤더 니블 확인), 이빨 확인: 상한 무력화 돌연변이에서 4/6 실패(역방향 가드 2개는 양쪽 통과). 신규 규칙 `AnalyzerReleases.Unshipped.md` 등록(KI-12 규약, sed 편집 후 구분 행 무결성 확인), RS1032(다문장 마침표) 한 번 더 밟아 수정. 테스트 187→193(net8.0·net9.0), 프로젝트별 클린 리빌드 6/6 경고 0·오류 0, Sandbox 전체 통과. `Known-Issues` KI-8 해결 섹션 승격, `Feature-Spec` F2 카테고리 범위·`[Flags]` 조합 함정(`Category1|Category4` = 5)·F5 MSGPROT013 명문화. 남은 꼬리: 동일 와이어 MessageId 를 가진 두 메시지 타입에 대한 컴파일 진단 부재(id 복사만 해도 같은 모듈 로드 실패) — 원장 신규 등록.
- KI-7 해결 — `MessageBufferWriter` 의 버퍼 산술·경계 3건: ① `EnsureCapacity` 의 `_position + additional` 을 **long 비교**로(int 합산이 GB 급 요구에서 음수 오버플로 → 증설 가드 거짓 통과 → 원인을 가리는 `AsSpan`·`CopyTo` 예외; 감사 원장 LOW 동일 항목). ② `Grow` 의 `Math.Max(_buffer.Length * 2, required)` 를 long 산술 + 배열 상한 clamp 로 교체하고 internal 헬퍼 `ComputeGrowCapacity` 로 분리 — 버퍼가 1GB 를 넘으면 배증값이 음수(`1_500_000_000 * 2 = -1_294_967_296`, 소비자 프로세스에서 확인)가 되어 매 증설이 **여유 없는 정확 요구량 대여 + 전체 복사**로 퇴보(성장 비용 제곱, 그 크기면 풀링도 안 됨)하던 것 차단. 페이로드 상한 `0X7FEFFFFF`(약 2.1GB)는 `WriteString` 이 지원하는 범위(KI-22)라 실제 회귀. 상한 초과 요구는 할당 시도 없이 `InvalidOperationException`(정확한 바이트 수 안내) — 수정 전 `EnsureCapacity(int.MaxValue)` 는 2GB 할당을 시도해 `OutOfMemoryException`. ③ `PatchInt32` 가 오프셋을 **기록된 구간**(`0 .. Length-4`)으로 검증 — 수정 전 `Length=4` 인 writer 에 `PatchInt32(60)` 이 수용되어 대여 배열의 미기록 바이트에 썼고(그 배열은 풀로 반환), 빈 writer·음수 오프셋도 내부 `ArgumentException` 으로 모호하게 실패했다. 회귀 테스트 15개(케이스, `WriterGrowthTests` — KI-22 의 `GetStringBufferRequirement` 와 같은 internal 헬퍼 검증 패턴, `ref struct` 라 람다 포획이 안 되어 `ref` 인자 헬퍼로 예외 관찰), 이빨 확인: 헬퍼를 수정 전 int 산술로 되돌리는 돌연변이에서 1/15 실패. 테스트 172→187(net8.0·net9.0), 프로젝트별 클린 리빌드 6/6 경고 0·오류 0, Sandbox 전체 통과. `Known-Issues` KI-7 해결 섹션 승격(잠재 결함 표에서 이동), `Public-API` writer 버퍼 계약·`Feature-Spec` F7 성장 계약 명문화.
- KI-10 측정·고정 (수정 아님 — 근거 기반 연기 결정) — 증분 파이프라인을 `GeneratorDriverOptions(trackIncrementalSteps: true)` 로 관측해 "편집마다 전체 재방출" 이라는 막연한 성능 주장을 숫자로 바꿨다. 무관한 편집(비메시지 클래스 본문 한 줄) 후 **생성 텍스트는 파일별 완전 동일**(7,571자, 2개 파일)하지만 `Compilation` 스텝이 항상 `Modified`, `result_ForAttributeWithMetadataName` 이 `Modified, Modified`(transform 출력 = 컴파일별 새 `INamedTypeSymbol` 인스턴스)라 **`SourceOutput` 은 `Modified`** — 즉 생성기 본문은 매 편집 재실행된다. 대신 Roslyn 이 출력 `SourceText` 를 비교하므로 **생성 트리 교체·재컴파일(비싼 쪽)은 이미 차단**돼 있고, 그 성질을 보장하는 것이 KI-3 였음이 확인됐다. 남은 비용은 편집당 생성기 CPU(후보 전체의 `TypeMetadata`·`SerializationGraph`·문자열 방출)뿐이고, 근본 해결은 transform 출력을 값 동등 스냅샷 모델로 바꾸고 출력 스텝의 `Compilation` 의존을 제거하는 대규모 재작성(이미터 전체가 `ISymbol` 소비)이라 **측정 근거로 연기**한다. 신규 회귀 테스트 1개(드라이버 수준 `무관한_편집에도_생성_파일별_텍스트는_변하지_않는다` — 힌트 이름별 텍스트 동일 + vacuous 방지 단언) — **이빨 확인**: KI-3 의 두 파일(`EmitState.cs`·`Member.cs`)만 `02824c2` 상태로 되돌리자 이 테스트 실패(1/1), 복원 후 통과. 테스트 171→172(net8.0·net9.0), 클린 리빌드 경고 0·오류 0, Sandbox 전체 통과. `Known-Issues` KI-10 행을 측정 결과로 교체 + "KI-10 측정 기록" 섹션 신설(스텝별 reason 표·해석·연기 근거), `Feature-Spec` F5 측정 결과 반영.
- KI-30 해결 — 참조 추적 컨텍스트의 `_firstObject is null` 센티널 결함: `RegisterObject(null)` 이 **id 1 을 발급하고도 슬롯을 채우지 않아** 이은 객체 등록도 id 1 을 받고(실험 확인: 쓰기·읽기 양쪽 `null → 1, real → 1`), 읽기 쪽에서는 `_objects[1]` 덮어쓰기로 `GetObject(1)` 이 백레퍼런스를 **다른 인스턴스**로 돌려줬다 — 예외 없는 조용한 객체 그래프 손상. 생성 코드는 `is null` 을 먼저 보지만 세 메서드는 모두 **공개 API** 라 수동 구현자가 밟을 수 있었고 계약은 어디에도 적혀 있지 않았다. `RegisterObject`·`TryGetObjectId`·`RegisterNewObject` 가 null 을 `ArgumentNullException` 으로 거부(공용 `ThrowNullReferenceValue`, `[DoesNotReturn]` — 이 주석이 없으면 가드 뒤에도 nullable 흐름 분석이 maybe-null 로 봐서 CS8601·CS8604 20건이 남). sentinel 구조는 유지 — null 이 못 들어가면 모호성이 사라지므로 첫 슬롯 최적화(Dictionary 지연 할당)를 지킬 수 있다. 회귀 테스트 5개(null 거부 3 · 거부 후 상태 오염 없음 · 정상 경로 id 1·2·3/승격/`GetObject` 동일 인스턴스 고정), 이빨 확인: `Contexts.cs` 만 되돌리자 4/5 실패. 테스트 166→171(net8.0·net9.0), 클린 리빌드 경고 0·오류 0, Sandbox 전체 통과. `Public-API` 참조 추적 계약(수동 구현 필수) 명문화로 "수동 구현 계약이 SerializeContext·DeserializeContext·ReferenceKind 미언급" 원장 MEDIUM 도 함께 종료, `Feature-Spec` F3 순환 참조 행 갱신, `Known-Issues` KI-30 해결 섹션 추가.
- KI-29 부분 해결 — 파생 메시지 타입이 있는 **구체** 메시지 베이스를 멤버 정적 타입으로 쓰면 선언 타입 기준으로 직렬화되어 파생 멤버가 예외·진단 없이 유실되던 형태(실험 확인: `EventBase` 멤버에 `LoginEvent` 대입 → 13바이트 프레임 성공, `User` 미기록, 복원 타입 `EventBase`)를 새 경고 진단 **`MSGPROT012`(Warning)** 로 가시화. 컴파일 전체에서 "파생 메시지 타입이 있는 non-abstract 메시지 타입" 집합을 한 번 구해(`CollectPolymorphicMessageBases`), 생성 성공한 메시지의 와이어 멤버(상속 포함·컬렉션은 요소 타입)를 검사(`ReportPolymorphicMembers`) — 생성은 막지 않으며(베이스 필드만 보내는 설계도 유효) `#pragma warning disable MSGPROT012` 로 억제 가능, 안내 문구는 해결책 3가지(베이스 `abstract` 선언 → KI-24 디스패치 · 구체 요소 타입으로 선언 · `Serialize(object)` 전송)를 제시. 추상 베이스는 제외, 타 어셈블리 전용 파생은 미보고(한계 명시). 신규 규칙이라 `AnalyzerReleases.Unshipped.md` 에 행 추가(KI-12 규약). 회귀 테스트 5개(드라이버 4 + 현재 동작 고정 실행 테스트 1) — 역방향 가드 2개 포함(추상 루트 멤버·파생 없는 구체 타입 멤버는 경고 없음). 테스트 161→166(net8.0·net9.0), 클린 리빌드 경고 0·오류 0, Sandbox 전체 통과. **손실 자체는 와이어 형식 변경(기존 피어 호환 파괴)이 필요해 감사 원장 HIGH 로 열린 채 유지**. 부수: 진단 메시지 규칙 RS1032(다중 문장은 마침표로 끝나야 함) 준수, `?`(nullable 주석)가 "'EventBase?' 를 abstract 로 선언하라" 처럼 읽히던 문구 정리, 마크다운 자동 서식이 `AnalyzerReleases.Unshipped.md` 구분 행을 망가뜨려 RS2007 을 유발한 함정 재확인·복구. `Known-Issues` KI-29 섹션 추가, `Feature-Spec` F3·F5 명문화.
- KI-3 해결 — 생성 코드의 비결정성 제거: 로컬 이름(`__item3`·`__coll1`·`__refKind7` …) 번호가 **컴파일러 프로세스 전역 정적 카운터**(`_uniqueIdCounter` + `Interlocked`)에서 나와, 같은 소스라도 그 프로세스가 이전에 몇 개의 메시지를 이미트했는지에 따라 **동일 입력 → 다른 출력**이 됐다. 그 결과 Roslyn 의 생성 출력 비교가 매번 깨져 무관한 편집에도 생성 트리가 교체·재컴파일되고(증분 빌드 이점 상실), 빌드 재현성·생성 코드 감사 diff 가 흔들렸다. 번호를 이미트 단위 상태인 `EmitState.NextUniqueId()` 로 옮기고(`TryEmit` 이 타입별로 새 상태 생성), 번호를 쓰는데 `state` 를 받지 않던 헬퍼 6개에 전달하도록 정리 — 전역 상태가 사라지면서 `Interlocked`·`using System.Threading;` 도 함께 제거(이미트는 타입별 단일 스레드). 회귀 테스트 1개(A→B→A→B 두 번씩 이미트 후 A₁==A₂·B₁==B₂, vacuous 방지를 위해 번호 사용 로컬 5종 존재 먼저 확인) — 이빨 확인: 이미터·EmitState 만 되돌리자 실패. 부수 증거: 클린 리빌드 2회의 생성 텍스트 전체 md5 동일. 테스트 160→161(net8.0·net9.0), Sandbox 전체 통과, 클린 리빌드 경고 0·오류 0. `Known-Issues` KI-3 해결 섹션 승격(잠재 결함 표에서 이동), `Feature-Spec` F5 결정적 생성 텍스트 명문화. 남은 꼬리: 헬퍼 방출 순서의 `Dictionary.Values` 의존(원장 LOW).
- KI-28 해결 — `GetStaticHidingModifier` 가 베이스의 **메시지 속성 유무**만으로 `new` 를 결정해, 상속 전용이라 정적 계약을 방출하지 않는 abstract `[GroupRootMessage]` 의 파생 요소마다 **CS0109** 가 뜨던 결함: 소스 베이스는 `IsPartial && IsConstructibleMessageType`(= 생성기가 실제로 방출하는 조건)일 때만 `new` 를 붙이고, 메타데이터(다른 어셈블리) 베이스는 판정 불가라 기존대로 유지(내리면 CS0108/CS0114 로 역전). `Define.cs`·`Method.cs` 에 복제돼 있던 동일 함수 2벌을 이미터 공용 헬퍼로 통합하고, 판정 근거(`IsPartial`·`IsConstructibleMessageType`)를 `internal` 승격해 생성 거부 조건과 한 사실 출처를 공유. 효과: 클린 리빌드(`-t:Rebuild`) 기준 저장소 **CS0109 64건 → 0건** — 증분 빌드에서는 컴파일이 건너뛰어져 보이지 않던 경고(KI-12 와 같은 함정)이며 `TreatWarningsAsErrors` 소비자에서는 빌드 실패가 된다. 회귀 테스트 2개(추상 루트 파생 `new` 미방출 + 역방향 가드로 구체 루트 파생 `new` 유지), 이빨 확인: 옛 규칙 돌연변이에서 1/2 실패. 테스트 158→160(net8.0·net9.0), Sandbox 전체 통과, Release 클린 리빌드 경고 0·오류 0. `Known-Issues` KI-28 해결 섹션 추가(검증 습관 교정: 빌드 경고 주장은 `-t:Rebuild` 로).
- 폴백 생성 코드 실행 검증 추가 — 신규 프로젝트 `Test/MessageProtocol.NetStandardFixtures`(**netstandard2.1** = Unity 호환 프로필, 솔루션 등록·Tests 가 참조): 이 TFM 에는 `CollectionsMarshal` 이 없어 생성기가 `List<T>` 고속 경로 대신 폴백 변형(인덱서 루프)을 방출하는데, Tests 는 net8.0/net9.0 라 항상 고속 경로만 실행되어 폴백 코드는 지금까지 **생성 텍스트 단언에만** 의존했다(KI-17 가드·KI-26 스냅샷 모두 해당). 픽스처 `FallbackCollections`(List 고정/가변·IList·배열 가변/고정 5형태)·`FallbackNode`(자기참조 + 중첩 컬렉션)로 `NetStandardFallbackTests` 6개가 폴백 경로를 실행으로 검증 — 컬렉션 5형태 왕복, null·빈 컬렉션 규약, **KI-17 폴백 벌크 할당 가드 실행**(악성 길이 접두 → `EndOfStreamException`), **KI-25 쓰기 깊이 가드 실행**(64 초과 체인 → `InvalidOperationException`, 10단계는 정상 왕복), **KI-14 읽기 깊이 가드 실행**(writer 상한만 올려 만든 100단계 프레임을 기본 reader 가 거부, 양쪽 상한을 올리면 복호), 그리고 픽스처 어셈블리의 TFM 자체를 고정(`.NETStandard,Version=v2.1` — retarget 으로 커버리지가 조용히 사라지는 것 차단). 생성 코드 확인: 해당 어셈블리의 `.g.cs` 에 `CollectionsMarshal` 0건, `var __coll`/`int __count` 스냅샷·`(long)__c * 4 > reader.Remaining` 가드 존재. 테스트 152→158(net8.0·net9.0). `CONTEXT`·`README`·`Feature-Spec` F10 검증 산출물 갱신.
- 부수 확인(다음 작업으로 이관): 클린 리빌드(`-t:Rebuild`) 기준 **CS0109 경고 64건** — abstract `[GroupRootMessage]` 의 파생 요소마다 `new` 수식어 6개가 붙는데 그 베이스는 상속 전용이라 정적 멤버를 방출하지 않는다(`GetStaticHidingModifier` 가 실제 방출 여부가 아니라 베이스의 속성만 본다). 증분 빌드에서는 컴파일이 건너뛰어져 보이지 않으며, `TreatWarningsAsErrors` 소비자에서는 빌드 실패가 된다.
- KI-27 해결 — `[GenericMessage(typeof(구성), ClassId = n)]` 의 **상한 미검증**: `ValidateConstructionEntries` 가 `n == 0`(누락)만 거부해 2^24 이상 값이 진단 없이 생성기를 통과하고, 생성된 등록 캐리어가 `[ModuleInitializer]` 안에서 `RegisterGenericConstruction` 의 `ArgumentOutOfRangeException` 을 터뜨려 CLR 이 `TypeInitializationException`(**모듈 로드 실패**)으로 감쌌다 — 속성 값 오타 한 자리의 증상이 "어셈블리 로드 실패"로 나타나고 원인 단서는 없는 형태. ClassId 는 MessageId 와 같은 3바이트 와이어 슬롯이므로 `classId > TypeMetadata.MaxMessageAttributeValue`(2^24-1) 검증을 추가해 기존 `MSGPROT008` 사유로 승격(새 진단 ID 없음 → 분석기 릴리스 추적 파일 변경 없음), 누락 안내 메시지의 하드코딩 `16777215` 도 상수로 교체. 런타임 검증 2중 방어는 유지. 회귀 테스트 4개(케이스) — 2^24·`uint.MaxValue` 거부(진단 보고·등록 캐리어 미방출·컴파일 오류 0) + 역방향 가드로 경계값 1·2^24-1 허용(과잉 차단 방지), 이빨 확인: 수정 전 거부 케이스 2개 실패·경계 케이스 통과. 테스트 148→152(net8.0·net9.0), Sandbox 전체 통과(38 체크), Release 빌드 경고 0·오류 0. `Known-Issues` KI-27 해결 섹션 추가, `Feature-Spec` F2 ClassId 범위·F5 MSGPROT008 사유 목록 갱신.
- KI-26 해결 — 생성 컬렉션 쓰기 루프가 멤버 표현식을 요소마다 재평가하던 문제: 배열·`List<T>`·`IList<T>` 쓰기 템플릿 6변형 모두가 멤버를 **한 번만** 평가해 로컬로 스냅샷하고(`__arr`/`__coll`/`__list` → `__count`/`__span`) null 판정도 그 로컬로 한다. 효과는 두 가지 — ① 비용: 게터 호출이 요소 N개당 **2N+2회 → 1회**(`IList<T>` 는 인터페이스 `Count` 호출도 루프에서 사라짐), 특히 `CollectionsMarshal` 이 없는 **Unity/netstandard2.1** 의 `List<T>`·`IList<T>` 경로에 해당(기존에는 `CollectionsMarshal` 있는 `List<T>` 만 스냅샷). ② 일관성: 길이 접두와 요소가 서로 다른 평가에서 나오지 않으므로 계산형 프로퍼티(`=> Build()`)의 프레임 자기모순·두 번째 평가 null 시 `else` 분기 NRE(TOCTOU) 차단. 회귀 테스트 4개 — 실행 2개(`CollectionSnapshotTests`, 게터 호출 수 픽스처 `SnapshotCollectionMessage`)+생성 텍스트 2개(`hasCollectionsMarshal` false/true Theory), 이빨 확인: 이미터만 되돌리자 3/4 실패. 테스트 144→148(net8.0·net9.0), Sandbox 전체 통과(38 체크), Release 빌드 경고 0·오류 0. `Known-Issues` KI-26 해결 섹션 추가, `Feature-Spec` F7 컬렉션 스냅샷 계약 명문화.
- KI-4 해결 — 와이어 페이로드 멤버 순서를 `Dictionary.Values` 열거(BCL 구현 세부 — 삽입 순서는 규약이 아님)에서 명시적 규칙으로 옮기고, 이미터·그래프에 **복제**되어 있던 동일 병합 로직(19줄 ×2)을 `TypeMetadata.GetWireMembers` 하나로 통합(호출부 5곳 — 페이로드 기록·populate·고정 크기 합산·그래프 수집). 규칙: 베이스 체인 루트 쪽부터 선언 순서, 파생의 그림자 제거 멤버는 **베이스 위치 유지 + 파생 심볼**(와이어 형식이라 기존 동작 바이트 단위 보존). 고정 테스트 2개(루트 베이스+그림자 제거 순서·중첩 페이로드 헬퍼 순서) — 이빨 확인: 결과를 뒤집는 돌연변이에서 2/2 실패, 복원 후 144/144. 테스트 142→144(net8.0·net9.0), Sandbox 전체 통과, Release 빌드 경고 0·오류 0. `Known-Issues` KI-4 해결 섹션 승격(잠재 결함 표에서 이동), `Feature-Spec` F3 와이어 멤버 순서 규칙 명문화. 남은 꼬리: `partial` 이 여러 파일로 갈라질 때 한 타입 내부 멤버 순서가 Roslyn 파트(구문 트리) 순서 = 와이어 정책 결정 필요로 별도 추적.
- KI-11 해결 — 등록 캐시(`SerializerCache<T>`)가 영구 오염되던 두 형태 차단. ① cctor 비던짐: 리플렉션 실패 시 null 로 남기고 사용·등록 지점에서 `ThrowMissingSerialize<T>()` 가 안내 예외를 던짐(CLR 이 타입별로 영구 캐싱하는 `TypeInitializationException` 제거 — 실험: 수정 전 회귀 테스트에서 16건 관측). ② `readonly` 해제 + `PrefillSerializerCache` 의 **사후 복구**: 등록 전 조기 접근으로 cctor 가 먼저 돌았더라도(`Serialize is null`) 델리게이트 등록이 캐시를 직접 채움 — 중복 등록이 델리게이트를 조용히 갈아끼우는 불일치는 만들지 않음. ③ 발행 순서: Prefill 홀더 `IsSet` 을 `volatile`(release store)로, 복구 경로는 핫 경로가 먼저 읽는 `Serialize` 를 `Volatile.Write` 로 마지막에 — 동시 cctor 가 찢어진 상태(델리게이트 null)를 고정하는 것 차단(Unity ARM store-store 재배열 대응). 부수: object dispatch 델리게이트의 캐시 직접 호출(`!`)을 공용 `Serialize<T>`·`Deserialize<T>` 경유로 교체(NRE → 안내 예외). 회귀 테스트 4개(`SerializerCacheTests` + 픽스처 `UnregisteredContractMessage`·`LateBoundMessage`) — 수정 전 3개 실패 확인, 역방향 가드(수동 구현 리플렉션 등록·왕복 불변) 포함. 테스트 138→142(net8.0·net9.0), Sandbox 전체 통과, Release 빌드 경고 0·오류 0. `Known-Issues` KI-11 해결 섹션 승격(잠재 결함 표에서 이동)·KI-9 행 갱신(순환 스택 오버플로는 KI-25 가드가 담당), `Feature-Spec` F6 등록 캐시 규약, `Public-API` 예외 계약 명문화. 남은 꼬리: 거부된 등록의 Prefill 잔존(`MessageId`·`HasId`)은 원장 MEDIUM 으로 별도 추적.
- KI-25 해결 — 쓰기 측 중첩 재귀에 깊이 상한 도입(KI-14 읽기 가드와 대칭): `MessageBufferWriter` 가 `EnterNestedObject`(상한 도달 시 `InvalidOperationException` — writer 쪽 한계 위반은 호출자 데이터 문제라 reader 의 `InvalidDataException` 과 구분)·`LeaveNestedObject`(0 아래 클램프)·`MaxNestingDepth`·`NestingDepth` 를 노출하고, 기본 상한 `MessageBufferWriter.DefaultMaxNestingDepth` 는 **reader 상한을 참조**해 구조적으로 동일하게 고정(기본 설정으로 쓴 것은 기본 설정으로 읽힌다). 계상 지점은 생성기 방출 2곳(그래프 내부 중첩 객체 기록·그래프 밖 메시지 위임) + 런타임 경유 지점 `MessageSerializer.SerializeToWriter`(타입 매개변수·추상 메시지 멤버·수동 구현, `try/finally`), 상한 상향 탈출구 `Create(initialCapacity, maxNestingDepth)`. 실험 검증: 저장소 밖 소비자 프로젝트에서 수정 전 **20,000노드 자기참조 연결 리스트 `Serialize` 와 디스패치 멤버 순환 그래프(`batch.Head = new WrapCommand { Inner = batch }`)가 모두 `Stack overflow.` 로 프로세스 사망**(exit 127, catch 불가, 10,000노드는 생존) → 수정 후 20,000·100,000노드·순환 세 경우 모두 `InvalidOperationException` catch·프로세스 생존(exit 0). 부수적으로 KI-14 이후의 **송수신 비대칭**(수신은 64 초과 거부, 송신은 무제한 기록)이 사라져 원인 추적이 송신 측에서 끝난다. 회귀 테스트 9개(케이스, `NestingDepthTests` 쓰기 섹션 + 순환 픽스처 `WrapCommand`(130)). 테스트 129→138(net8.0·net9.0), Sandbox 전체 통과(38 체크), Release 빌드 경고 0·오류 0. `Known-Issues` KI-25 해결 섹션 추가, `Feature-Spec` F3(양방향 가드)·F7, `Public-API` writer 중첩 깊이 계약 명문화.
- KI-24 해결 — 추상 메시지 타입 멤버가 **무진단 CS0117** 생성 코드를 내던 결함: `abstract [GroupRootMessage]`(상속 전용이라 생성기가 의도적으로 정적 멤버를 만들지 않음)를 멤버로 쓰면 `AbstractEvent.Serialize(…)` 위임 코드가 방출되어 소비자 빌드가 깨졌다. 생성기가 멤버 타입 `IsAbstract` 를 봐서 추상 메시지 멤버에는 정적 위임 대신 **런타임 메시지 디스패치**(`SerializeToWriter`·`DeserializeFromReader`, `T` 멤버와 공용 — 이미터 `EmitRuntimeDispatch*` 개명)를 방출하므로, 구체 요소의 헤더가 실려 수신 측이 **구체 타입과 파생 멤버를 그대로 복원**한다(다형 멤버 지원 + 베이스 페이로드로 쓸 때의 파생 필드 유실 차단). 실험 검증: GeneratorDriver 로 수정 전 생성기 진단 0건·`CS0117` 2건 확인, 이미터만 되돌려 테스트 빌드가 깨지는 것(회귀 테스트 이빨)도 확인. 회귀 테스트 5개(생성기 2 — 역방향 가드 포함, 런타임 3)·픽스처 `AbstractCommand` 계열, Sandbox S13(3체크). 테스트 124→129(net8.0·net9.0), Sandbox 전체 통과(38 체크), Release 빌드 경고 0·오류 0. `Known-Issues` KI-24 해결 섹션 추가, `Feature-Spec` F3 메시지 타입 멤버 3경로(그래프 내부 · 그래프 밖 구체 위임 · 추상 디스패치)와 백레퍼런스 미추적 제약 명문화. 남은 꼬리(구체 베이스 타입 멤버의 파생 필드 유실)는 원장 HIGH 로 별도 추적.
- KI-14 해결 — 중첩 객체 역직렬화 재귀에 깊이 상한 도입: `MessageBufferReader` 가 `EnterNestedObject`·`LeaveNestedObject`·`MaxNestingDepth`·`NestingDepth`(기본 상한 `DefaultMaxNestingDepth = 64`)를 노출하고, 생성기가 재귀 지점 2곳(그래프 내부 중첩 객체 판독·그래프 밖 메시지 위임)에서 쌍을 방출하며, 런타임 경유 지점 `MessageSerializer.DeserializeFromReader`(타입 매개변수 멤버·외부 호출자 재귀)는 `try/finally` 로 자체 계상 — 상한 초과 시 `InvalidDataException`. 깊은 합법 그래프용 탈출구는 reader 단위 상한 `new MessageBufferReader(buffer, maxNestingDepth)`. 실험 검증: 소비자 프로젝트에서 자기참조 멤버 1개 메시지의 **20,005바이트 적대 프레임이 `Stack overflow.`(exit 127, catch 불가)로 프로세스를 죽이던 것**이 수정 후 catch 가능한 `InvalidDataException` 거부로 바뀌었다(5,005바이트는 수정 전에도 생존 — 임계는 스택 크기·빌드 구성에 따라 내려감). 회귀 테스트 10개(케이스, `NestingDepthTests` + 픽스처 `ChainMessage`·`WideChainMessage`). 테스트 114→124(net8.0·net9.0), Sandbox 전체 통과, Release 빌드 경고 0·오류 0. `Known-Issues` KI-14 해결 섹션 승격(잠재 결함 표에서 이동), `Feature-Spec` F3 깊이 가드·F7 비용, `Public-API` 중첩 깊이 계약 명문화.
- KI-6 해결 — `MessageBufferReader.ReadString` 음수 길이 접두사 검증: `-1` 만 null 로 복호하고 나머지 음수(`-2`…`int.MinValue`)는 `InvalidDataException` 거부(KI-15·KI-20 와이어 무결성 엄격 기조 동일, 경계 `EndOfStreamException` 과 구분). 회귀 테스트 4개(`WireAndBufferTests`). 테스트 93→97. `Known-Issues` KI-6 해결 섹션 승격(잠재 결함 표에서 이동), `Feature-Spec` F3 문자열 길이 접두 규약 명문화.
- KI-21 해결 — `MessageBufferReader.Skip`·`MessageBufferWriter.Advance` 음수 `count` 거부(`ArgumentOutOfRangeException`, 공개 경계에서 검증) — forward-only 규약 강제. 되돌림으로 소비한 바이트 재읽기·기록한 페이로드 덮어쓰기가 불가능해졌고, 경계 위반(`EndOfStreamException`)과 호출자 프로그래밍 오류가 분리됐다. 회귀 테스트 7개(케이스). 테스트 97→104. `Known-Issues` KI-21 해결 섹션 승격, `Public-API` 버퍼 I/O 계약 명문화.
- KI-22 해결 — `MessageBufferWriter.WriteString` 용량 산술 오버플로 제거: `4 + GetMaxByteCount(int)`(약 7.15억 자에서 음수 오버플로 → `EnsureCapacity` 증설 누락 → `GetBytes` 내부 실패) 대신 long 산술 내부 헬퍼 `GetStringBufferRequirement` 사용, 버퍼(배열) 상한 `0X7FEFFFFF` 초과 문자열은 명확한 `ArgumentException` 으로 거부. 임계값 미만 동작 불변(회귀 테스트로 고정). 회귀 테스트 9개(케이스). 테스트 104→113. `Core` 에 `InternalsVisibleTo(MessageProtocol.Tests)` 추가, `Known-Issues` KI-22 해결 섹션 승격, `Public-API` 버퍼 상한 계약 명문화.
- KI-12 해결 — 분석기 릴리스 추적 도입: `Source/MessageProtocol.CodeGenerator/AnalyzerReleases.Shipped.md`(릴리스 2.0 = `v2.0.0` 태그 기준 MSGPROT001~008, 007 만 Warning) · `AnalyzerReleases.Unshipped.md`(태그 이후 추가된 MSGPROT010·011) 추가 — SDK 자동 `AdditionalFiles` 포함에 맡기고 csproj 중복 선언은 제거. `dotnet build MessageProtocol.sln -t:Rebuild` 클린 리빌드 경고 16개(RS2008)→0개, 오류 0개. 테스트 113개(net8.0·net9.0)·Sandbox 전체 통과로 동작 불변 확인. `Known-Issues` KI-12 해결 섹션 승격(구분 행 공백 시 RS2007·중복 `AdditionalFiles` 함정 기록), `Packages` 진단 규칙 추가 시 추적 파일 갱신 규약 명문화.
- KI-23 해결 — 공개 인덱서가 직렬화 멤버로 선택돼 `message.this[]` 형태 문법 오류 코드가 무진단 생성되던 결함: `TypeMetadata` 멤버 선택에 `IPropertySymbol.IsIndexer` 제외 추가(그래프 수집측 `GetAllMembers` 도 같은 `Members` 를 써서 일괄 차단). 회귀 테스트는 수정 전 실패(생성 코드에 `this[` 존재)를 확인 후 통과. 테스트 113→114. `Known-Issues` KI-23 해결 섹션 추가, `Feature-Spec` F3 멤버 선택 규칙 명문화.
- 백로그 소진 후 신규 감사 패스 수행(생성기·Core 런타임·문서 드리프트 3개 영역 병렬) — HIGH 6건·MEDIUM 11건·LOW 9건 신규 발견. KI-3·KI-4·KI-11(2형태)·KI-14 는 현재 코드에서 잔존 재확인, 그 외 신규 결함(추상 그룹 루트 멤버 CS0117·백레퍼런스 InvalidCastException·ClassId 상한 미검증·등록 경쟁·Public-API 제네릭 표면 누락·죽은 공개 API 등)은 감사 루프 원장에 미해결로 등록.

## 2026-09-04

- KI-16 해결 — 동일 제네릭 페이로드의 두 구성이 한 그래프에 공존할 때 헬퍼 메서드 이름 충돌(소비자 CS0111 컴파일 실패). `SerializationGraph` 헬퍼 접미사에 타입 인자·중첩 타입 체인 반영 + 그래프 단위 유일성 구분자. 회귀 픽스처·왕복 테스트로 수정 전 재현 검증. 테스트 83→84. `Known-Issues` KI-17~KI-22 추가(감사 발견 미해결: `CollectionsMarshal` 미지원 벌크 가드 누락·생성 불가 페이로드 무진단·캐리어 접미사 충돌·UTF8 관대 폴백·음수 Skip/Advance·WriteString 오버플로).
- KI-17 해결 — `CollectionsMarshal` 미지원 타깃의 `List<T>` 벌크 판독 분기 할당 전 가드 격상(`개수 ≤ Remaining` → `개수×요소크기 ≤ Remaining`, long 산술, `EmitListRead`). `InternalsVisibleTo` 추가 후 `TryEmit(hasCollectionsMarshal: false)` 직접 구동 회귀 테스트(약한 가드로 역전 시 실패 검증). 테스트 84→85. `Known-Issues` KI-17 해결 승격.
- KI-18 해결 — 생성 불가 페이로드 진단 승격: 루트 메시지 추상·매개변수 없는 생성자 없음 → `MSGPROT010`, 중첩 페이로드 기본 생성 불가(추상·포지셔널 레코드) → 그래프 제외 후 멤버 단위 `MSGPROT006`, 대입 불가 멤버(get-only·init-only·읽기전용 필드) → `MSGPROT011` (`SerializationGraph.IsSerializableObjectType`·`MessageCodeGenerator.IsConstructibleMessageType`·`Member.IsDeserializableMember`, `EmitState` 사유 열거 확장). 회귀 테스트 5개. 테스트 85→90. `Feature-Spec` F5 진단 목록 갱신.
- KI-19 해결 — 제네릭 구성 등록 캐리어 접미사 충돌: `MakeCarrierSuffix` 제거 후 KI-16과 동일 전략의 공용 `SymbolNaming.MakeUniqueSuffix`(네임스페이스·중첩 체인·제네릭 인자 + 사용 접미사 집합 구분자)로 교체 — 그래프 헬퍼·캐리어 이름 체계 단일화. 동명 중첩 캐리어 회귀 테스트(수정 전 충돌 검증). 테스트 90→91.
- KI-20 해결 — 문자열 엄격 UTF-8 정책 확정: `WriteString`·`ReadString` 이 엄격 폴백(`EncoderFallback.ExceptionFallback`·`DecoderFallback.ExceptionFallback`) 사용 — 고립 서로게이트 쓰기 인코딩 예외 거부, 무효 UTF-8 읽기 `InvalidDataException` 거부(KI-15 와이어 무결성 기조 동일). 회귀 테스트 2개. 테스트 91→93. `Feature-Spec` F3 엄격 UTF-8 규칙 명문화.

## 2026-09-01

- KI-15 해결 — `MessageBufferReader.ReadDecimal` flags 검증(스케일 >28·예약 비트 → `InvalidDataException`), 무효 스케일 `decimal`이 덧셈·뺄셈에서 일으키던 원격 프로세스 크래시 경로 차단. 회귀 테스트 3개(`BufferIOTests`). 테스트 80→83. `Known-Issues` KI-15 해결 섹션 승격, `Feature-Spec` F3 decimal 검증 명문화.
- `Known-Issues` KI-14·KI-15 추가 — 온라인 게임 공격 표면 검토: 중첩 객체 재귀 판독 깊이 미제한(스택 오버플로 가능성), `ReadDecimal` 무검증 비트 재해석. 엔진 단 권고(프레임 크기 상한·레이트 제한·핸들러 인가)는 패키지 범위 밖.
- KI-15 심각도 격상(실험 검증) — 공격자 제어 `decimal` 플래그(스케일 ≥78)가 덧셈·뺄셈에서 `DecCalc` 스택 버퍼 오버플로 유발, **SIGSEGV 프로세스 종료(try/catch 불가)**. 스케일 ≤77은 안전, 곱·비교는 생존. 원격 킬 스위치 수준의 DoS.
- KI-13 해결 — 컬렉션 길이·개수 접두사 할당 전 남은 버퍼 가드: `MessageSerializeCodeEmitter.Member`의 `EmitArrayRead`·`EmitListRead` 5 변형 전부(고정 크기 `길이×요소크기 ≤ Remaining` 정확 검증, 가변 크기 `개수 ≤ Remaining` 상한, 초과 시 `EndOfStreamException`, 정책 옵션 없음). 회귀 테스트 4개 신규(악성 길이 3 + 정상 왕복 1). 테스트 76→80. `Feature-Spec` F3 컬렉션 가드 명문화, `Known-Issues` KI-13 해결 처리.
- `Known-Issues` KI-13 추가 — 프로덕션 적합성 검토: 생성 역직렬화의 길이 접두사가 남은 바이트 검증 전 컬렉션 할당 → 불신 피어 OOM DoS 가능성, 채용 전 상한 가드 권고.

## 2026-08-31

- 제네릭 구성 선언 속성 통합 ([ADR-0005](../05-Decisions/ADR-0005-Generic-Attribute-Unification.md), ADR-0004 선언 모델 대체): `[GenericMessage(typeof(닫힌 구성), ClassId)]` 단일 속성(선언부·캐리어 무관), `GenericConstructionAttribute` 제거, 제네릭+스탠드얼론=항상 제네릭 와이어(구성 선언 필수, 미선언 직렬화 예외+안내), 동일 컴파일 내 구성 중복 선언 컴파일 진단 승격, 미바운드 제네릭 거부, `MSGPROT009` 삭제. 테스트 74→76, 픽스처·Sandbox 통합 문법 이전. `Feature-Spec` F2·F5, `GLOSSARY` 동기화.
- 제네릭 구성 분산 선언 추가 (ADR-0004 보완): `[GenericConstruction(typeof(구성), ClassId)]` 캐리어 속성 — 선언부 수정 없이 타 파일/프로젝트에서 구성 추가, 생성기가 등록 클래스 출력. ClassId 보관을 내부 필드에서 런타임 레지스트리(`GetGenericClassId<T>`)로 이전(타 어셈블리 캐리어 지원), 테스트 프로젝트에 `EmitCompilerGeneratedFiles` 활성화(생성 코드 감사). 테스트 70→74, Sandbox S12 추가. `Feature-Spec` F2·F5, `GLOSSARY` 동기화.
- 제네릭 와이어 재설계 ([ADR-0004](../05-Decisions/ADR-0004-Generic-Message-Wire-Format.md), ADR-0003 대체): `[GenericMessage(typeof(...), ClassId)]` 구성 선언 속성, 헤더 플래그 Generic(0) + MessageId 뒤 구성 클래스 ID 24비트 와이어(`GenericIdHeaderSize = 7`), (MessageId, ClassId) 디스패치·모듈 로드 자동 등록(송수신 무설정), 구성 공존·다중 타입 매개변수, `MSGPROT008`·`MSGPROT009` 진단. 테스트 63→70, Sandbox S11 추가. `Feature-Spec` F1·F2·F5·범위 밖, `GLOSSARY`, `Known-Issues` 동기화.
- 제네릭 직렬화 수신 측 제약 명문화: 지연 등록은 `Serialize(object)` 경로 한정 — 역직렬화만 하는 수신 프로세스는 닫힌 구성 명시적 등록 필요 (`ADR-0003`·`Feature-Spec` 갱신).
- 제네릭 메시지 직렬화 구현 ([ADR-0003](../05-Decisions/ADR-0003-Generic-Message-Serialization.md), Known-Issues KI-1 해결): 타입 매개변수 유지 생성(partial arity·헬퍼 이름 백틱 변환), `T` 멤버 런타임 메시지 디스패치, 제네릭 타입 자동 등록 미생성(닫힌 구성 지연/수동 등록). 회귀 테스트 5개 추가(테스트 58→63), Sandbox S10 시나리오(제네릭 round-trip·T 컬렉션·object dispatch) 추가. `ADR-0002` superseded 처리, `Feature-Spec` F5·범위 밖 동기화.
- `MSGPROT007` 메시지 속성 중복 경고 진단 추가 (`Source/MessageProtocol.CodeGenerator`) — 한 타입에 메시지 속성 2개 이상이면 경고·생성 건너뜀, 회귀 테스트 2개 (테스트 56→58). `Feature-Spec` F5 진단 목록 갱신.
- `05-Decisions/ADR-0002-Generic-Message-Serialization.md` 신규 — 제네릭 메시지 직렬화 추후 지원 연기 결정. `Feature-Spec` 범위 밖·`Known-Issues` KI-1 유예·KI-2 해결 동기화.
- `06-Troubleshooting/Known-Issues.md` 신규 — 코드 리뷰 발견 문제점: 확인 버그 2건(제네릭 메시지 무진단 깨진 생성, 속성 충돌 무진단·런타임 실패) + 잠재 결함 10건.

- `README.md` v2 동기화: 런타임 타깃에 net6.0 명시, 생성기 netstandard2.0 표기, 속성 네임스페이스 안내, 저장소 구조 표 추가.
- 리뷰 라운드 2 수정: 생성기 힌트 이름 중첩 구분자를 `+`로 변경(네임스페이스 점과 충돌 제거 + 회귀 테스트), `generated-out` 컴파일 제외 가드를 `DefaultItemExcludes`로 교체(`Compile Remove`는 기본 글롭 이전 평가라 무효).
- 리뷰 라운드 1 수정: 생성기 힌트 이름 충돌 수정(네임스페이스 포함 유일 힌트 + 회귀 테스트), 벤치마크 InProcess 도구 체인 전환(실행 가능), `MessageWireFormat` 상수 2개 복원(`NullSizedPayloadLength`·`DefaultStreamCapacity`), `generated-out` 정리·컴파일 제외 가드, vault 문서 동기화(`00-AI/CONTEXT·GLOSSARY·CONVENTIONS`, `01-Overview/Home`, `02-Architecture/Overview`, `03-Reference/Public-API·Packages`).
- 재작성 구현 완료: `Source/` (Core·CodeGenerator·메타), `Test/` (Tests 54·Benchmarks), `Sandbox/` 인수 조건(전체 통과), 패키지 3종 2.0.0 pack 검증.
- `02-Architecture/Feature-Spec.md` status → approved, "Legacy 대비 재작성 변경점" 추가.
- `05-Decisions/ADR-0001-Rewrite-Bootstrap.md` 신규 — 네임스페이스 유지·테스트 신규 작성·예시 우선·패키지 2.0.0 결정.
- `02-Architecture/Feature-Spec.md` 신규 — 재작성 프로젝트 지원 기능 스펙 (Legacy 기능 패리티 기준).
