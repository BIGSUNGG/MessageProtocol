---
project: DS_MessageProtocol
type: troubleshoot
status: draft
tags: [known-issues, generator, runtime]
updated: 2026-09-13
---

# Known Issues

v2 코드 리뷰(2026-08-31)에서 확인된 문제점. KI-13·KI-15는 2026-09-01 프로덕션 적합성·공격 표면 검토 중 추가·같은 날 해결, KI-14는 미해결로 남았다가 2026-09-05 해결. 2026-09-04 감사에서 KI-16·KI-17·KI-18·KI-19·KI-20 추가·같은 날 해결, KI-21~KI-22 추가. 2026-09-05 감사 루프에서 KI-6·KI-12·KI-14·KI-21·KI-22 해결, 같은 날 백로그 소진 후 감사 패스에서 KI-23 추가·해결, 이어서 개선 루프에서 KI-24·KI-26·KI-27·KI-28·KI-29·KI-30·KI-31 추가·해결(KI-29 는 부분 해결), KI-25·KI-11·KI-4·KI-3·KI-7·KI-8 해결(그 외 신규 항목은 감사 루프 원장 `.pi-glla/audit-loop/findings.md` 에서 추적). 도입 시점 통과 기준선(빌드·테스트 56개·Sandbox 28 시나리오)은 이후 성장해 2026-09-07 기준 테스트 205개(net8.0·net9.0)·Sandbox 38 시나리오 전체 통과 상태다. 2026-09-13 3.0.0 상용화 재검증 패스에서 KI-43 추가·같은 날 해결했다.

## 확인된 버그 (실험 검증)

### KI-1. 제네릭 메시지 타입 → 진단 없이 깨진 코드 생성 (해결)

**상태: 해결 (2026-08-31).** [ADR-0002](../05-Decisions/ADR-0002-Generic-Message-Serialization.md)로 연기 결정 후 같은 날 [ADR-0003](../05-Decisions/ADR-0003-Generic-Message-Serialization.md)으로 지원 구현, 이어 [ADR-0004](../05-Decisions/ADR-0004-Generic-Message-Wire-Format.md)로 전용 속성 + 구성 클래스 ID 와이어 재설계(구성 공존·수신 측 무설정). 회귀 테스트·Sandbox S10/S11 포함.

원본 발견 내용:

`[StandaloneMessage(1)] partial class Msg<T> { public int Value; }` 처럼 멤버 타입이 전부 지원 대상인 제네릭 메시지 타입은 진단 없이 생성 코드를 방출하는데 그 코드가 컴파일 불가다.

- `MakeHelperSuffix`가 `MetadataName`(`Msg`1`)을 써서 헬퍼 메서드 이름에 백틱이 들어간다 (`__WritePayload_N_Msg`1`).
- `Define.Emit`은 `Symbol.Name`만 써서 partial 선언이 타입 매개변수를 잃는다 (`partial class Msg` vs `Msg<T>`).
- 사용자 프로젝트에 수십 개의 무관한 CS 구문 에러가 뜨고 MSGPROT 진단은 없다.

조치 방향: ~~제네릭 메시지 타입 거부 진단 추가~~ → ADR-0002로 연기 → **ADR-0003으로 지원 구현 완료**.

### KI-2. 메시지 속성 충돌 → 무진단, 런타임 object 역직렬화 실패 (해결)

**상태: 해결 (2026-08-31).** `MSGPROT007` 경고 진단 추가 — 한 타입에 메시지 속성 2개 이상이면 경고하고 생성을 건너뛴다 (`MessageCodeGenerator.TryReportDuplicateMessageAttributes`, 회귀 테스트 2개).

원본 발견 내용:

`[NonIdMessage]`와 `[StandaloneMessage(1)]`을 동시에 붙이면 진단 없이 `flags = NonId|Standalone` 헤더(0x30)를 만들고:

- 생성 코드는 4바이트 헤더를 쓴다 (`IsStandalone || IsGroup` 기준).
- 런타임 `RegisterCore`는 `HasEmbeddedMessageId(0x30) == false`(NonId 비트)로 판정해 ID·reader 등록을 **조용히 건너뛴다**.
- 결과: `Deserialize(object)` 호출 시 `KeyNotFoundException`. 제네릭 경로만 동작.

조치 방향: ~~네 메시지 속성 상호 배타 진단 추가~~ → 완료. 남은 꼬리: `RegisterCore`가 NonId 비트 가진 HasId 등록을 조용히 건너뛰는 동작은 수동 등록 경로에 남아 있음 (필요 시 별도 가드).

### KI-13. 컬렉션 길이 접두사 선할당 → 불신 피어 OOM DoS (해결)

**상태: 해결 (2026-09-01).** 생성기가 배열·리스트 역직렬화 전 변형에 할당 전 남은 버퍼 가드를 출력한다 — 고정 크기 요소 `길이×요소크기 ≤ Remaining` 정확 검증, 가변 크기 요소 `개수 ≤ Remaining`(요소 최소 와이어 1바이트) 상한 검증, 초과 시 `EndOfStreamException`. `MessageSerializeCodeEmitter.Member`의 `EmitArrayRead`·`EmitListRead` 5 변형, 회귀 테스트 4개(`CollectionGuardTests`).

원본 발견 내용:

생성 `Deserialize`가 길이·개수 접두사를 남은 바이트 검증 **전에** `new T[len]`·`new List<T>(len)` 할당에 써서, 불신 피어가 악성 길이(`int.MaxValue`)를 보내면 데이터 검증이 돌기도 전에 거대 할당→`OutOfMemoryException`으로 프로세스가 죽는다. bulk 경로의 `len * elemSize` 곱셈은 int 오버플로 가능. 문자열(`ReadString`)은 `ReadBytes` 경계 검증이 먼저라 영향 없음.

조치 방향: 할당 전 상한 가드 생성 코드 출력 → 완료. 정책 옵션 없이 통일 상한(설정 없음)으로 결정.

### KI-15. `ReadDecimal` 무검증 비트 재해석 → 원격 프로세스 크래시 (해결)

**상태: 해결 (2026-09-01).** `MessageBufferReader.ReadDecimal`이 재해석 전에 flags 검증 — 스케일 >28 또는 부호·스케일 외 예약 비트 존재 시 `InvalidDataException`으로 거부. 유효 `decimal`이 가질 수 없는 비트만 거부하므로 호환성 손실 없음. 회귀 테스트 3개(`BufferIOTests`: 스케일 78 거부·예약 비트 거부·경계 스케일 28 허용).

원본 발견 내용 (실험 검증):

와이어 16바이트를 검증 없이 `decimal` 비트로 재해석해 공격자가 flags 전체를 제어할 수 있었다. 스케일 78 이상으로 만든 값은 **파싱은 통과**한 뒤 게임 로직에서 덧셈·뺄셈에 쓰이는 순간 런타임 `DecCalc` 내부 고정 스택 버퍼를 오버플로시켜 **SIGSEGV — try/catch 불가, 프로세스 즉시 사망**. 스케일 ≤77 안전(값 왜곡만), ≥78 크래시, 곱·비교는 생존(실험: 덧셈 기준 임계 스케일 78). 파싱 직후가 아니라 로직 한가운데서 터지는 지연 지뢰라 추적이 어렵고 패킷 하나로 100% 재현 가능.

조치 방향: 판독 시 flags 검증(거부) → 완료. 무결성 위반은 `EndOfStreamException`(경계)과 구분해 `InvalidDataException`(와이어 내용 불법)으로 보고.

### KI-16. 동일 제네릭 페이로드 두 구성의 헬퍼 이름 충돌 → 소비자 CS0111 (해결)

**상태: 해결 (2026-09-04).** `SerializationGraph.MakeHelperSuffix`가 타입 인자·중첩 타입 체인을 포함한 유일 접미사를 만들고, 그래프 단위 사용 접미사 집합으로 잔여 충돌에 구분자를 붙인다. 회귀 픽스처 `DuplicateGenericPayloadsMessage`(`GenericPair<int>`+`GenericPair<string>` 공존)·왕복 테스트 추가로 수정 전 CS0111 재현 검증. 테스트 83→84.

원본 발견 내용:

한 메시지 그래프에 동일 제네릭 페이로드의 두 구성(`Pair<int>`·`Pair<string>`)이 도달 가능하면 접미사가 `네임스페이스+MetadataName`(`Ns_Pair_1`)으로 동일해 `__WritePayload_…`/`__CreateInstance_…` 헬퍼가 같은 partial 클래스에 중복 방출 → 소비자 프로젝트가 CS0111로 컴파일 실패. 같은 형태가 동명 중첩 타입(`Outer1.Point`·`Outer2.Point`)에서도 발생. 기존 테스트는 한 그래프에 두 구성을 넣지 않아 미발견.

조치 방향: 접미사 유일성 보장 → 완료. `MessageCodeGenerator.MakeCarrierSuffix`(구성 등록 캐리어)는 동일 형태 결함으로 KI-19 에서 같은 전략으로 해결.

### KI-17. `CollectionsMarshal` 미지원 `List<T>` 벌크 가드 누락 → 불신 피어 최대 8배 선할당 (해결)

**상태: 해결 (2026-09-04).** `EmitListRead` 의 `CollectionsMarshal` 미지원(요소별 판독) 분기 할당 전 가드를 `개수 ≤ Remaining` 에서 `개수×요소크기 ≤ Remaining`(long 산술)으로 격상 — 동일 타입의 `CollectionsMarshal` 고속 경로와 동일 검증. 회귀 테스트는 `InternalsVisibleTo` 로 이미터 진입점(`TryEmit`, `hasCollectionsMarshal: false`)을 직접 구동해 생성 가드 텍스트를 검증(약한 가드로 역전 시 실패 확인). 테스트 84→85.

원본 발견 내용:

KI-13 가드가 5 변형 전부 적용됐다고 기록됐으나, `CollectionsMarshal` 미지원 타깃(예: netstandard2.0 소비자)의 `List<long>`·`List<double>` 벌크 판독 분기는 개수만 검증했다. 불신 피어가 `개수 = Remaining` 을 보내면 가드를 통과하고 `new List<T>(개수)` 가 남은 버퍼의 최대 8배(8바이트 요소 기준)를 선할당한 뒤에야 요소 판독이 예외를 던진다.

조치 방향: 형제 경로와 동일한 `개수×요소크기` 가드 → 완료.

### KI-18. 생성 불가 페이로드 → 진단 없이 컴파일 불가 코드 방출 (해결)

**상태: 해결 (2026-09-04).** 세 갈래 검출: (1) 루트 메시지 타입이 추상이거나 매개변수 없는 생성자가 없으면 `MSGPROT010` 으로 생성 거부, (2) 중첩 페이로드가 추상 클래스·포지셔널 레코드 등 기본 생성 불가 타입이면 그래프 수집에서 제외 → 멤버 단위 `MSGPROT006`(미지원 타입), (3) 읽기 전용·초기화 전용 프로퍼티·읽기전용 필드처럼 대입 불가 멤버는 `MSGPROT011` 으로 생성 거부(루트는 자기 partial 이라 모든 접근 수준 허용, 중첩 페이로드는 internal 이상 설정자 요구). 회귀 테스트 5개. 테스트 85→90.

원본 발견 내용:

`EmitReferenceTypeMethods` 가 페이로드 구축 가능성을 검사하지 않고 `new {TypeName}()` 를 방출해 추상 클래스 멤버는 CS0712, 포지셔널 레코드는 매개변수 없는 생성자가 없어 CS7036, init-only 멤버는 대입 불가(CS0200/CS8852)가 생성 코드에서 났다. 멤버 선택(`TypeMetadata`)도 설정 가능성을 검사하지 않아 get-only 프로퍼티가 어떤 메시지에서든 대입 에러를 만들었다. 사용자는 속성 없이 불투명한 생성 코드 에러만 봤다.

조치 방향: 진단 승격 → 완료. 새 진단 `MSGPROT010`(생성 불가 메시지 타입)·`MSGPROT011`(대입 불가 멤버), `EmitState` 미지원 사유 열거 확장.

### KI-19. 구성 등록 캐리어 접미사 충돌 → 동명 중첩 호스트 CS0102 (해결)

**상태: 해결 (2026-09-04).** `MakeCarrierSuffix` 제거, KI-16과 동일 전략의 공용 `SymbolNaming.MakeUniqueSuffix`(네임스페이스·중첩 체인·제네릭 인자 + 컴파일 단위 사용 접미사 집합으로 구분자 부여)로 교체 — 그래프 헬퍼와 캐리어가 하나의 이름 체계 공유. 회귀 테스트: 같은 네임스페이스 동명 중첩 캐리어 2개(수정 전 충돌 재현 검증). 테스트 90→91.

원본 발견 내용:

`__GenericConstructionRegistration_{접미사}` 캐리어 클래스의 접미사가 `네임스페이스+MetadataName` 이라 같은 네임스페이스의 동명 중첩 호스트(`OuterA.Carrier`·`OuterB.Carrier`)가 동일 접미사 → 두 최상위 클래스 동명 충돌(CS0102) + `AddSource` 힌트 이름 중복으로 생성기 실행 자체가 깨진다. KI-16과 동일 형태의 결함.

조치 방향: 유일 접미사 체계 공유 → 완료.

### KI-20. UTF8 관대한 폴백 → 왕복 시 조용한 문자열 변형 (해결)

**상태: 해결 (2026-09-04).** 정책 결정: 와이어 무결성 엄격 정책(KI-15와 동일 기조). `WriteString`·`ReadString` 이 엄격 UTF-8(`EncoderFallback.ExceptionFallback`·`DecoderFallback.ExceptionFallback`) 사용 — 고립 서로게이트는 쓰기에서 인코딩 예외로 실패(대체 바이트로 조용한 변환 없음), 무효 UTF-8 바이트는 읽기에서 `InvalidDataException` 거부(경계 `EndOfStreamException` 과 구분). 회귀 테스트 2개. 테스트 91→93.

원본 발견 내용:

기본(교체 폴백) `Encoding.UTF8` 이 송신 측에서는 고립 서로게이트를 대체 바이트로 조용히 재인코딩하고, 수신 측에서는 무효 바이트를 U+FFFD 로 변환했다 — 왕복이 송신과 다른 문자열을 반환할 수 있고, 손상 패킷이 오류 없이 복호되어 와이어 손상이 은폐됐다.

조치 방향: 교체 대신 거부 → 완료. 송신 측 예외는 자연스러운 `EncoderFallbackException`(프로그래밍 오류), 수신 측은 `InvalidDataException`(와이어 내용 불법)으로 보고.

### KI-6. `ReadString` 음수 길이 접두사 → 손상 데이터가 null 로 조용히 통과 (해결)

**상태: 해결 (2026-09-05).** `ReadString` 이 `-1` 만 null 로 복호하고, 나머지 음수(`-2`…`int.MinValue`)는 `InvalidDataException` 으로 거부 — KI-15·KI-20 와이어 무결성 엄격 기조와 동일(경계 위반 `EndOfStreamException` 과 와이어 내용 불법을 구분). 회귀 테스트 4개(`-2`·`-3`·`int.MinValue` 거부 + `-1` null 복호 유지). 테스트 93→97.

원본 발견 내용:

null 규약은 길이 접두 `-1` 인데 판독 코드가 `length < 0` 전체를 null 로 처리했다. 손상되거나 악의적인 프레임의 `-2`·`int.MinValue` 접두사가 오류 없이 null 문자열로 복호되어, 필드 누락(전송 실패)과 와이어 손상을 수신 측이 구분할 수 없었다.

조치 방향: 규약 외 음수 거부 → 완료.

### KI-21. `Skip`·`Advance` 음수 허용 → 위치 되돌림 (해결)

**상태: 해결 (2026-09-05).** `MessageBufferReader.Skip`·`MessageBufferWriter.Advance` 가 음수 `count` 를 공개 경계에서 `ArgumentOutOfRangeException` 으로 거부 — forward-only 규약 강제. 되돌림이 실제로 일으키던 두 행동(소비한 바이트 재읽기, 기록한 페이로드 덮어쓰기)에 대한 행동 회귀 테스트 + `0`·양수 정상 경로 보존 테스트. 회귀 테스트 7개(케이스). 테스트 97→104. `Public-API` 버퍼 I/O 계약 명문화.

원본 발견 내용:

두 메서드 모두 `(uint)(_position + count) > (uint)_buffer.Length` 상한 검증만 했고, 음수는 이 검증을 통과해 `_position` 을 줄였다. `Skip(-1)` 은 예외 없이 이미 소비한 바이트를 다시 읽게 하고(경계 검사도 잘못된 `EndOfStreamException` 을 던져 프로그래밍 오류와 혼동됨), `Advance(-1)` 은 `Length` 를 줄여 다음 쓰기가 기록된 페이로드를 덮어쓰게 했다. 생성 코드는 음수를 넘기지 않아 수동·외부 호출자 대상 위험.

조치 방향: 공개 경계에서 음수 거부 → 완료. 되돌림은 `EndOfStreamException`(와이어 경계) 이 아니라 `ArgumentOutOfRangeException`(호출자 프로그래밍 오류) 으로 보고.

### KI-22. `WriteString` 용량 산술 int 오버플로 → 증설 누락 (해결)

**상태: 해결 (2026-09-05).** 필요 용량을 `long` 으로 계산하는 내부 헬퍼 `GetStringBufferRequirement(charCount)` 도입(UTF-8 상한 공식 `4 + 문자당 3바이트 + 프리앰블 3` 을 직접 long 산술) — `Encoding.GetMaxByteCount(int)` 의 int 오버플로 의존 제거. 버퍼(배열) 상한 `0X7FEFFFFF` 를 넘으면 `GetBytes` 의 내부 `ArgumentException` 대신 명확한 메시지의 `ArgumentException` 으로 거부. 가드 후 `required ≤ MaxBufferLength - _position < int.MaxValue` 라 좁힘·이후 `EnsureCapacity` int 합산도 오버플로하지 않는다. 회귀 테스트 9개(케이스) — 자체 공식이 `GetMaxByteCount + 4` 와 일치함을 715,827,881자까지 검증(상한을 좁히거나 헤프게 바꾸지 않음), int 상한 너머에서 양수·단조증가 유지, 30만 바이트 한글 문자열 증설·왕복. 테스트 104→113. `Core` 에 `InternalsVisibleTo(MessageProtocol.Tests)` 추가.

원본 발견 내용:

`WriteString` 이 `EnsureCapacity(4 + StrictUtf8.GetMaxByteCount(value.Length))` 로 용량을 확보했는데, `GetMaxByteCount` 는 `charCount * 3 + 3` 을 int 로 계산하므로 약 7.15억 자(문자 수 > 715,827,881)에서 음수로 오버플로한다. 음수가 `EnsureCapacity` 에 들어가면 `_position + additional > _buffer.Length` 비교가 거짓이 되어 증설이 건너뛰어지고, 이은 `GetBytes` 가 공간 부족으로 실패했다. `GetBytes` 가 출력 배열 경계를 검사하므로 메모리 손상은 없고 예외만 내부 원인(버퍼 부족)을 가리는 형태였다.

조치 방향: long 산술 + 명확한 상한 거부 → 완료. 임계값 미만에서는 기존과 동일한 용량을 요청하므로 동작 변화 없음(회귀 테스트로 고정).

### KI-12. RS2008 — 분석기 릴리스 추적 미사용 (해결)

**상태: 해결 (2026-09-05).** `Source/MessageProtocol.CodeGenerator/` 에 `AnalyzerReleases.Shipped.md`(릴리스 2.0 = `v2.0.0` 태그 기준 MSGPROT001~008, 007 만 Warning) · `AnalyzerReleases.Unshipped.md`(태그 이후 추가된 MSGPROT010·011) 추가. SDK 가 두 파일을 자동으로 `AdditionalFiles` 에 포함하므로 csproj 에 별도 선언하지 않는다(중복 선언 시 컴파일러에 두 번 전달됨). 검증: `dotnet build MessageProtocol.sln -t:Rebuild` 클린 리빌드 결과 **경고 0개·오류 0개**(RS2008·RS2007 모두 소멸), 테스트 113개·Sandbox 전체 통과. `Packages` 에 새 진단 규칙 추가 시 추적 파일 갱신 규약 명문화.

원본 발견 내용:

분석기 프로젝트에 `EnforceExtendedAnalyzerRules` 가 켜져 있는데 릴리스 추적 파일이 없어, `DiagnosticDescriptor` 마다 RS2008 경고가 발생했다(클린 빌드 기준 16건 보고). 증분 빌드에서는 컴파일이 건너뛰어져 경고가 보이지 않으므로 오랫동안 방치되기 쉬운 형태였고, 진단 규칙이 어떤 릴리스에 도입·변경됐는지에 대한 기록도 없었다.

함정 두 가지(수정 중 확인): ① 구분 행을 `-------- | ---------- | ---------- | -------` 처럼 공백 포함으로 쓰면 Roslyn 릴리스 추적 파서가 인식하지 못해 RS2007(잘못된 릴리스 헤더)이 발생 — 반드시 `--------|----------|----------|-------` 형태여야 한다(마크다운 자동 서식 도구가 이 행을 바꾸지 않도록 주의). ② csproj 에 `AdditionalFiles` 를 수동 선언하면 SDK 자동 포함과 겹쳐 같은 파일이 컴파일러에 두 번 전달된다.

조치 방향: 추적 파일 추가 + 형식 엄수 → 완료.

### KI-23. 공개 인덱서 → 문법 오류 생성 코드 (해결)

**상태: 해결 (2026-09-05).** `TypeMetadata` 멤버 선택에서 `IPropertySymbol.IsIndexer` 제외 — 인덱서는 인수를 받아야 하므로 직렬화 멤버가 될 수 없다. 회귀 테스트(`GeneratorDiagnosticTests.공개_인덱서는_직렬화_멤버에서_제외된다`)는 수정 전 `this[` 부분열 발견으로 실패함을 확인 후 추가(진단 없음·생성 코드 컴파일 오류 없음·일반 멤버는 그대로 방출). 테스트 113→114. `Feature-Spec` F3 멤버 선택 규칙 명문화.

원본 발견 내용:

멤버 선택이 `m is IFieldSymbol || m is IPropertySymbol` + 비상속 + (ignore > include > public) 만 봤다. C# 인덱서는 `IsIndexer == true` 인 `IPropertySymbol` 이고 Roslyn `Name` 이 `this[]` 라, 공개 인덱서를 가진 메시지 타입은 `writer.WriteInt32(message.this[]);` 같은 **문법적으로 불가능한 코드**가 생성됐다. MSGPROT 진단도 없으므로 소비자 프로젝트가 CS0026·CS0443·CS1001·CS1002·CS1003 으로 붕괴하고, 원인이 생성기라는 단서도 주어지지 않았다. 그래프 수집측 `GetAllMembers` 도 동일한 `TypeMetadata.Members` 를 쓰므로 한 지점 수정으로 양쪽 경로 모두 차단됐다.

조치 방향: 멤버 선택에서 인덱서 제외 → 완료.

### KI-14. 중첩 역직렬화 재귀 깊이 무제한 → 작은 적대 프레임으로 원격 프로세스 사망 (해결)

**상태: 해결 (2026-09-05).** `MessageBufferReader` 가 중첩 깊이를 세는 단일 지점이 된다 — `EnterNestedObject()`(상한 도달 시 `InvalidDataException`)·`LeaveNestedObject()`(0 아래 클램프)·`MaxNestingDepth`·`NestingDepth`, 기본 상한 `DefaultMaxNestingDepth = 64`(`System.Text.Json`·`Newtonsoft.Json` 과 동일). 계상 지점은 재귀가 일어나는 세 갈래 전부: 생성기 방출 2곳(그래프 내부 중첩 객체 판독 `EmitInGraphMessageRead`·그래프 밖 메시지 위임 `EmitOutOfGraphMessageRead`)과 런타임 경유 지점 1곳(`MessageSerializer.DeserializeFromReader` — 타입 매개변수 멤버·외부 호출자 재귀, `try/finally`). 깊은 합법 그래프용 탈출구는 reader 단위 상한 `new MessageBufferReader(buffer, maxNestingDepth)`(0 이하 거부). 회귀 테스트 10개(케이스) — `NestingDepthTests`(상한+1 거부·상한 정확히 허용·object dispatch 경로·상한 상향 후 200단계 복호·넓은 그래프 무영향·기존 순환 참조 왕복·Enter/Leave 계약과 0 클램프·0 이하 상한 거부), 픽스처 `ChainMessage`(자기참조)·`WideChainMessage`(500개 형제 중첩). 테스트 114→124. `Feature-Spec` F3·F7, `Public-API` 중첩 깊이 계약 명문화.

원본 발견 내용 (실험 검증):

자기참조 메시지 멤버는 재귀로 판독되는데 깊이를 세는 곳이 없어, 깊이가 오직 프레임 크기 ÷ 최소 페이로드(자기참조 멤버 1개 기준 수준당 1바이트)로만 제한됐다. 저장소 밖 소비자 프로젝트로 재현한 결과 **5,005바이트 프레임(깊이 5,000)은 생존, 20,005바이트(깊이 20,000)는 `Stack overflow.` 로 프로세스 즉시 종료**(exit 127, try/catch 불가, 100,005바이트도 동일). 수준당 스택은 약 50~200바이트라 사망 임계는 런타임·스레드 스택 크기·빌드 구성에 따라 **내려간다**(스택이 작은 워커 스레드·Unity 는 더 얕은 프레임에서도 사망) — 20KB 안팎 프레임은 어떤 실전 프레임 상한으로도 걸러지지 않는다. 수정 후 같은 20,005·100,005바이트 프레임이 모두 `InvalidDataException` 으로 catch 되고 프로세스는 생존한다(동일 재현 프로그램, exit 0).

조치 방향: reader 단위 깊이 카운터 + 생성 코드·런타임 경유 지점 계상 → 완료. 생성 코드 쪽은 hot path 비용을 아끼려고 `try/finally` 를 쓰지 않는다 — 판독 중 예외로 `Leave` 가 생략되면 깊이는 **부풀기만** 하므로(0 아래 클램프) 가드는 실패 방향으로 안전하고, 예외가 난 reader 는 위치가 객체 중간이라 재사용 대상이 아니다(재사용하는 공개 경유 지점 `DeserializeFromReader` 만 `finally` 로 짝을 맞춘다). 값 타입(구조체) 중첩은 자기 포함이 컴파일러에 의해 불가능해 깊이가 정적 그래프로 제한되므로 계상하지 않는다.

### KI-24. 추상 메시지 타입 멤버 → 무진단 CS0117 생성 코드 (해결)

**상태: 해결 (2026-09-05).** 진단 승격이 아니라 **지원 승격**으로 처리 — 생성기가 멤버 타입의 `IsAbstract` 를 봐서 추상 메시지 타입 멤버에는 정적 위임 대신 **런타임 메시지 디스패치** 코드를 방출한다(쓰기 `MessageSerializer.SerializeToWriter(value, ref writer)`, 읽기 `(선언타입)MessageSerializer.DeserializeFromReader(ref reader)` — ADR-0003 의 타입 매개변수(`T`) 멤버와 동일 기제, 이미터 메서드도 `EmitRuntimeDispatchWrite`·`EmitRuntimeDispatchRead` 로 개명해 공용). 와이어에 구체 요소의 헤더(MessageId)가 실리므로 수신 측은 등록된 **구체 요소 타입과 파생 멤버를 그대로 복원**한다(다형 멤버 — 베이스 페이로드로 써서 파생 필드를 조용히 잃는 형태도 함께 차단). KI-14 의 깊이 가드가 이 경로(`DeserializeFromReader`)에도 걸려 있어 적대 중첩은 동일하게 거부된다. 회귀 테스트 5개 — 생성기 2개(추상 루트 멤버: 위임 미방출·디스패치 방출·MSGPROT 진단 0·컴파일 오류 0 / **역방향 가드**: 비공개 매개변수 없는 생성자로 그래프에서 빠진 *구체* 메시지 멤버는 정적 위임 유지) + 런타임 3개(`DispatchTests`: 구체 타입·베이스+파생 멤버 복원, `List<추상루트>` 다형 복원, null 왕복). 픽스처 `AbstractCommand`(126)·`StartCommand`(127)·`StopCommand`(128)·`CommandEnvelope`(129), Sandbox S13(3체크). 테스트 124→129, Sandbox 35→38 체크. `Feature-Spec` F3 메시지 타입 멤버 3경로·백레퍼런스 미추적 제약 명문화.

원본 발견 내용 (실험 검증):

`abstract [GroupRootMessage] AbstractEvent` 를 멤버로 가진 메시지는 **MSGPROT 진단이 하나도 없이** `global::TestNs.AbstractEvent.Serialize(message.Payload, ref writer);` · `result.Payload = global::TestNs.AbstractEvent.Deserialize(ref reader);` 를 방출해 소비자 빌드가 `CS0117` 2건으로 깨졌다(GeneratorDriver 실험으로 확인 — 생성기 진단 0건, 컴파일 오류 `CS0117: 'AbstractEvent'에 'Serialize' 정의가 없음` ×2). 추상 루트 + 구체 요소는 그룹 메시지의 **정상적인 선언 형태**(상속 전용 루트라 `MessageCodeGenerator` 가 의도적으로 생성을 건너뜀)라, 다형 페이로드를 담은 봉투 메시지라는 자연스러운 사용이 곧장 빌드 붕괴로 이어졌고 원인이 생성기라는 단서도 없었다. 구조적 원인: 그래프 수집(`SerializationGraph.IsSerializableObjectType`)이 추상 타입을 제외한 뒤 `IsMessageType` 분기로 빠지는데, 이 분기는 **위임 대상에 정적 멤버가 실제로 존재하는지 검증하지 않는** 사각지대였다(KI-18 은 비메시지 추상 페이로드만 MSGPROT006 으로 걸렀다). 회귀 테스트의 이빨도 확인 — 이미터 변경만 되돌리면 테스트 프로젝트가 생성 코드 CS0117 로 빌드 실패한다.

조치 방향: 추상 메시지 멤버를 런타임 디스패치로 연결 → 완료. 제약: 이 경로는 2.2.0(KI-9 해소)부터 호출측 참조 추적을 공유해 추상 멤버를 통한 공유·순환 참조가 복원된다. 남은 꼬리(감사 원장 HIGH → **KI-34 로 실험 확정·완화**): **구체** 베이스 타입 멤버(예: `EventBase` 타입 멤버에 `LoginEvent` 인스턴스)는 여전히 그래프 내부 페이로드 경로라 선언 타입 기준으로 직렬화되어 파생 필드가 유실되고, 공유 조합에 따라 판독이 블라인드 캐스트로 실패했다(이제 안내 예외). 남은 꼬리: **구체** 베이스 타입 멤버(예: `EventBase` 타입 멤버에 `LoginEvent` 인스턴스)는 여전히 그래프 내부 페이로드 경로라 선언 타입 기준으로 직렬화되어 파생 필드가 유실된다 — 감사 원장 HIGH 항목(백레퍼런스 캐스트·파생 필드 유실)으로 별도 추적 중이며 와이어 형식 정책 결정이 필요해 이번 변경에서 분리했다.

### KI-25. 쓰기 측 중첩 재귀 무제한 → 송신 측 스택 오버플로·읽기 가드와의 비대칭 (해결)

**상태: 해결 (2026-09-05).** KI-14 의 읽기 가드와 **대칭**으로 writer 가 깊이를 센다 — `MessageBufferWriter.DefaultMaxNestingDepth`(= `MessageBufferReader.DefaultMaxNestingDepth` 를 참조해 구조적으로 동일 고정)·`EnterNestedObject()`(상한 도달 시 `InvalidOperationException`)·`LeaveNestedObject()`(0 아래 클램프)·`MaxNestingDepth`·`NestingDepth`, 상한 상향 탈출구 `MessageBufferWriter.Create(initialCapacity, maxNestingDepth)`(0 이하 `ArgumentOutOfRangeException`). 계상 지점은 읽기 측과 동일한 세 갈래: 생성기 방출 2곳(그래프 내부 중첩 객체 기록·그래프 밖 메시지 위임)과 런타임 경유 지점 `MessageSerializer.SerializeToWriter`(타입 매개변수·추상 메시지 멤버, 수동 구현 — `try/finally`). 예외 타입은 reader(`InvalidDataException` = 와이어 내용 불법)와 달리 `InvalidOperationException` — writer 쪽 한계 위반은 호출자 데이터·상태 문제라 `ThrowAdvanceBeyondCapacity` 와 같은 기조. 회귀 테스트 9개(케이스) — `NestingDepthTests` 쓰기 섹션(기본 상한 대칭 고정·깊은 체인 거부·**디스패치 멤버 순환 그래프 거부**·상한 상향 후 200단계 쓰기+맞춤 reader 로 되읽기·넓은 그래프 무영향·Enter/Leave 계약과 0 클램프·0 이하 상한 거부 ×3), 순환 픽스처 `WrapCommand`(130, 추상 루트 요소가 `CommandEnvelope` 을 되참조). 테스트 129→138(net8.0·net9.0), Sandbox 전체 통과, Release 빌드 경고 0·오류 0. `Feature-Spec` F3·F7, `Public-API` writer 중첩 깊이 계약 명문화.

원본 발견 내용 (실험 검증):

KI-14 는 읽기만 막았다. 쓰기 측은 깊이를 세는 곳이 아예 없어 두 가지가 **catch 불가한 스택 오버플로(프로세스 즉시 사망)** 로 끝났다 — 저장소 밖 소비자 프로젝트에서 확인: ① 송신자가 스스로 만든 **합법적 데이터**인 20,000노드 자기참조 연결 리스트를 `Serialize` 하면 사망(10,000노드는 생존 — 임계는 스택 크기·빌드 구성에 따라 내려감), ② 런타임 디스패치 멤버(KI-24 로 지원된 추상 메시지 멤버, 기존 `T` 멤버)로 돌아가는 **순환 그래프**(`batch.Head = new WrapCommand { Inner = batch }`)는 디스패치마다 `SerializeContext` 가 새로 만들어져 백레퍼런스가 동작하지 않아 재귀가 무한히 깊어지고 사망. 수정 후 세 경우(20,000·100,000노드·순환) 모두 `InvalidOperationException` 으로 catch 되고 프로세스는 생존한다(exit 0).

부수적으로 **비대칭**이 사라졌다: KI-14 이후 수신 측은 깊이 64 초과 프레임을 거부하는데 송신 측은 훨씬 깊은 프레임도 문제없이 만들어냈다 — 즉 성공적으로 직렬화된 메시지가 상대에게서 읽히지 않을 수 있었다. 양쪽 기본 상한을 하나의 상수로 묶어( writer 가 reader 상한을 참조) "기본 설정으로 쓴 것은 기본 설정으로 읽힌다" 가 구조적으로 보장되고, 송신 측이 **먼저** 실패하므로 원인 추적이 수신 측에서 끝나지 않는다.

조치 방향: reader 와 대칭인 writer 깊이 카운터 + 생성 코드·런타임 경유 지점 계상 → 완료. 생성 코드 쪽은 읽기 측과 동일하게 `try/finally` 없이 짝만 맞춘다(기록 중 예외 시 깊이는 부풀기만 하므로 실패 방향 안전, 부분 기록된 writer 는 재사용 대상 아님). 남은 꼬리: 디스패치 멤버의 백레퍼런스 미추적 자체는 여전하므로 **공유 참조**(순환이 아닌 동일 인스턴스 두 번 등장)는 디스패치 멤버를 통과하면 중복 기록되고 참조 동일성이 복원되지 않는다 — KI-9 제약으로 문서화됨, 가드는 그 경로가 프로세스를 죽이지 못하게 막는 것까지가 범위 → **KI-9 해소(2026-09-07)로 꼬리 종결**: 디스패치·위임 쓰기가 호출측 컨텍스트를 공유해 이미 등록된 인스턴스 재방문은 백레퍼런스로 종결된다. 깊이 가드는 여전히 유효 — 매 수준이 새 인스턴스인 깊은 디스패치 체인(공유 없음)은 상한 초과 시 `InvalidOperationException` 으로 거부된다(회귀 테스트 `깊은_디스패치_체인은_스택오버플로_대신_InvalidOperationException으로_거부된다`).

### KI-11. 등록 캐시 경쟁·조기 접근 → 영구 `TypeInitializationException` (해결)

**상태: 해결 (2026-09-05).** `SerializerCache<T>` 가 **영구 상태를 남기지 않도록** 세 가지를 바꿨다. ① cctor 는 더 이상 던지지 않는다 — 리플렉션으로 계약 멤버를 못 찾으면 null 로 남기고(`ResolveSerialize*` → `TryResolveSerialize*`, 공용 `TryCreateDelegate<TDelegate>`), 사용 지점(`Serialize<T>` 3개 오버로드)과 등록 지점(리플렉션 등록 3경로)이 `ThrowMissingSerialize<T>()` 로 명확히 보고한다(기존 `ThrowMissingDeserialize<T>` 와 대칭). ② 캐시 필드에서 `readonly` 를 벗기고 `PrefillSerializerCache` 가 `RunClassConstructor` 뒤 **캐시가 비어 있으면 직접 채워 복구**한다(CLR 은 cctor 를 다시 돌리지 않으므로 이 단계가 없으면 등록 전 조기 접근이 그 타입을 영구히 unusable 하게 만든다). ③ Prefill 홀더의 `IsSet` 을 `volatile`(release store)로 바꿔 델리게이트 6개 쓰기의 publication 을 플래그에 묶고, 복구 경로도 핫 경로가 먼저 읽는 `Serialize` 를 `Volatile.Write` 로 마지막에 발행한다 — 동시 cctor 가 `IsSet=true` 만 보고 델리게이트는 null 인 찢어진 상태를 고정하는 것 차단(x86 에선 관찰이 어렵지만 **Unity ARM 은 store-store 재배열이 가능**). 부수적으로 object dispatch 델리게이트가 캐시 필드를 `!` 로 직접 호출하던 것을 공용 `Serialize<T>`·`Deserialize<T>` 경유로 바꿔 null 델리게이트 NRE 대신 같은 안내 예외를 타게 했다. 회귀 테스트 4개(`SerializerCacheTests`) — 수정 전 **3개 실패 확인**(`System.TypeInitializationException` 16건 관측): 조기 접근이 영구 초기화 실패가 아니라 안내 예외를 던지고 두 번째 접근도 동일, 조기 접근 후 델리게이트 등록으로 제네릭·object dispatch 양쪽 복구, 계약 멤버 없는 타입의 리플렉션 등록은 등록 시점에 보고, + 역방향 가드(수동 구현 타입 리플렉션 등록·왕복 불변). 테스트 138→142(net8.0·net9.0), Sandbox 전체 통과, Release 빌드 경고 0·오류 0. `Feature-Spec` F6 등록 캐시 규약, `Public-API` 예외 형식 명문화.

원본 발견 내용:

`PrefillSerializerCache` 가 정적 필드 6개를 fence 없이 쓰고 `IsSet = true` 를 일반 쓰기로 발행해, 다른 스레드의 캐시 cctor 가 찢어진 상태(null·혼합 델리게이트)를 `readonly` 필드에 영구 고정할 수 있었다. 더 쉽게 밟히는 제2형태는 순서 문제였다 — 등록 전에 누군가 `SerializerCache<T>` 를 건드리면(예: 미등록 타입을 `Serialize<T>` 하려다 실패) cctor 가 리플렉션 경로로 돌고, 계약 멤버가 없는 타입에서는 `ResolveSerializeRefMethod` 가 **cctor 안에서** 던진다. CLR 은 정적 생성자 실패를 타입별로 영구 캐싱하므로 이후 델리게이트 등록이 성공해도 그 타입은 영원히 `TypeInitializationException` 이었다(실험: 수정 전 회귀 테스트에서 16건 관측). 즉 **일시적 순서 실수가 영구 고장으로 고정**되는 형태였고, 오류 메시지도 진짜 원인(등록 누락)이 아니라 CLR 내부 예외로 가려졌다.

조치 방향: 캐시를 오염 불가능하게 만들기 → 완료. ~~남은 꼬리(별도 추적): 첫 등록 시도가 `RegisterCore` 검증에서 거부되면 롤백이 Prefill·cctor 를 되돌리지 않아 `MessageId`·`HasId` 가 잔류한다(감사 원장 MEDIUM).~~ **잔존 해결 (2026-09-08)**: 델리게이트 fast path(HasId·NonId 양쪽)가 prefill **전에** 새 검증 관문 `ValidateRegistration`(부수효과 없음 — 타입 중복·generic 플래그·와이어 id 중복·NonId 플래그)을 통과한다. 거부되면 캐시가 아예 건드려지지 않으므로 `RegisterGenericConstruction<T>` 가 캐시의 `MessageId` 로 런타임 키를 조립하던 2차 오염(잘못된 키로의 등록)도 함께 차단된다. `RegisterCore` 의 원자적 클레임(TryAdd/GetOrAdd)은 검증 통과 후 발행 직전의 동시 등록 경쟁용 백스톱으로 그대로 남는다. 회귀 테스트: 거부된 등록(점유 id) 후 캐시 `MessageId` 가 자기 값으로만 채워지고 올바른 id 재등록·왕복 성공(돌연변이로 prefill 을 검증 앞으로 되돌리면 실패). 델리게이트 조용히 갈아끼우지 않는 성질도 유지 — 중복 등록은 여전히 거부되며 이번에는 캐시도 오염되지 않는다.

### KI-4. 와이어 멤버 순서가 `Dictionary.Values` 열거에 의존 + 병합 로직 이중 정의 (해결)

**상태: 해결 (2026-09-05).** 페이로드 바이트 순서를 정하는 베이스 체인 멤버 병합이 `TypeMetadata.GetWireMembers`(신규 공용 정적) 한 곳으로 모였다 — 이전에는 **동일한 19줄이 이미터(`MessageSerializeCodeEmitter.GetAllMembers`)와 그래프(`SerializationGraph.GetAllMembers`)에 복제**되어 있었고 둘 다 `Dictionary<string, MemberMetadata>.Values` 를 반환했다. 새 구현은 `List<MemberMetadata>` + 이름→위치 인덱스로 순서를 **명시적으로** 만든다: 베이스 체인을 루트 쪽부터 내려오며 선언 순서로 추가, 같은 이름의 파생 멤버는 **베이스 위치를 유지한 채 심볼만 교체** — 와이어 형식이므로 기존 동작을 바이트 단위로 보존했다. 호출부 5곳(페이로드 기록·populate·고정 크기 합산·그래프 수집)이 모두 이 한 구현을 쓴다. 고정(characterization) 테스트 2개 — 루트의 베이스+파생+그림자 제거 순서(`BaseFirst, Shadowed, BaseLast, DerivedOwn`)와 그림자 멤버가 파생 타입(`WriteInt64`)으로 베이스 위치에 기록됨, 중첩 페이로드 헬퍼 순서(그래프 경로) — + **이빨 확인**: `GetWireMembers` 결과를 뒤집는 돌연변이를 넣자 두 테스트 모두 실패(2/2, net8.0·net9.0), 되돌려 144/144 통과. 테스트 142→144, Sandbox 전체 통과, Release 빌드 경고 0·오류 0. `Feature-Spec` F3 와이어 멤버 순서 규칙 명문화. **남은 꼬리(연기 — 정책 결정 사항)**: 한 타입 내부 멤버 순서는 여전히 Roslyn `ISymbol.GetMembers()` 선언 순서라, 여러 파일로 갈라진 `partial` 메시지에서는 파트(구문 트리) 순서가 와이어 배치를 정한다 — 두 피어가 같은 소스를 다른 파일 순서로 컴파일하면 이론상 배치가 갈려 조용한 손상이 가능하다. 고정은 와이어 형식 변경(파트 경로+위치 또는 이름 기준 정렬)이 필요해 이 루프(와이어 비호환 금지) 범위 밖이며, 같은 소스를 같은 파일 구성으로 컴파일하는 팀 규약으로 실 위험은 제한된다.

원본 발견 내용:

페이로드 멤버의 **바이트 순서**는 송수신이 반드시 일치해야 하는 와이어 형식의 일부인데, 그 순서가 두 곳에서 `Dictionary.Values` 열거에 얹혀 있었다. .NET `Dictionary` 는 제거가 없으면 삽입 순서로 열거하지만 그것은 **문서화된 규약이 아니라 구현 세부**다(공식 문서도 순서를 보장하지 않는다고 밝힌다). 즉 BCL 내부 변화 하나로 — 컴파일 오류도, 진단도, 테스트 실패도 없이 — 송신과 수신의 필드 배치가 어긋나 **조용한 데이터 손상**이 날 수 있는 형태였다. 게다가 동일 로직이 두 벌이라 한쪽만 고치면 이미터(실제 바이트 순서)와 그래프(도달 가능 타입 수집)가 다른 멤버 집합을 보는 구조 위험도 안고 있었다.

조치 방향: 명시적 순서 + 단일 구현 → 완료. 남은 꼬리(별도 추적): 한 타입 **내부**의 멤버 순서는 여전히 Roslyn `ISymbol.GetMembers()` 의 선언 순서다 — 여러 파일로 갈라진 `partial` 타입에서는 파트(구문 트리) 순서가 되므로, 두 피어가 같은 소스를 다른 파일 순서로 컴파일하면 이론상 배치가 갈릴 수 있다. 와이어 형식을 바꾸는 결정이 필요해 이번 변경에서 분리했다(감사 원장 등록).

### KI-26. 컬렉션 쓰기 루프의 멤버 재평가 → 게터 2N+2회·계산형 프로퍼티에서 프레임 자기모순 (해결)

**상태: 해결 (2026-09-05).** 컬렉션(배열·`List<T>`·`IList<T>`) 쓰기 템플릿 6변형 전부에서 멤버 표현식을 루프 밖으로 끌어올려 **한 번만** 평가한다 — `var __arr/__coll/__list = message.Member;` 뒤 null 판정도 그 로컬로 하고, 길이는 `__count`(또는 `CollectionsMarshal` 경로의 `__span.Length`)로 스냅샷한다. 이전 코드는 길이 접두(`Count`) + 루프 조건(`Count`, N+1회) + 인덱서(멤버 접근 N회)가 각자 `message.Member` 를 다시 평가했다. 회귀 테스트 4개 — 실행 검증 2개(`CollectionSnapshotTests`: 게터 호출 수가 `IList<int>` 3요소·`string[]` 2요소 각각 **정확히 1회**; 수정 전 8회·6회 + 빈 컬렉션·null 규약 보존) + 생성 텍스트 2개(`hasCollectionsMarshal` false/true Theory — `message.Values`·`Names`·`Tags` 가 생성 코드에 각각 1회만 등장, `if (message.X is null)` 미방출, 스냅샷 로컬 존재; false 는 Unity/netstandard2.1 경로라 이 저장소에서 실행되지 않아 텍스트로 고정). **이빨 확인**: 이미터만 되돌리자 신규 4개 중 3개 실패(행위 보존 테스트 1개는 양쪽 통과). 테스트 144→148(net8.0·net9.0), Sandbox 전체 통과, Release 빌드 경고 0·오류 0. `Feature-Spec` F7 컬렉션 스냅샷 계약 명문화.

원본 발견 내용:

두 가지 문제였다. ① **비용** — 요소마다 프로퍼티 게터와(`IList<T>` 는 인터페이스 `Count` 호출까지) 멤버 접근이 반복됐다. `CollectionsMarshal` 이 있는 타깃의 `List<T>` 는 이미 `AsSpan` 스냅샷으로 한 번만 평가했지만, 배열·`IList<T>`·그리고 `CollectionsMarshal` 이 없는 타깃(**Unity/netstandard2.1**)의 `List<T>` 는 루프가 멤버를 계속 다시 읽었다 — 이 저장소의 1차 타깃이 바로 그 경로다. ② **일관성(더 심각)** — 길이 접두와 요소를 *서로 다른 평가*에서 가져오므로, `public IList<int> Codes => Build();` 같은 계산형 프로퍼티에서는 길이와 요소가 다른 컬렉션 인스턴스에서 나와 프레임이 스스로 모순될 수 있고(수신 측에서 길이·내용 불일치), 게터가 두 번째 호출에서 null 을 돌려주면 `else` 분기 안에서 `NullReferenceException` 이 난다(TOCTOU). 스냅샷 방식은 `CollectionsMarshal` 경로가 이미 따르던 규약이라 6변형이 같은 의미가 됐다.

조치 방향: 멤버 표현식 1회 평가 + 로컬 스냅샷(null 판정 포함) → 완료. 부수 효과: 직렬화 중 컬렉션이 변하는 경우 프레임이 "한 순간 스냅샷"으로 일관되게 나온다(이전에는 길이와 요소가 다른 시점 값일 수 있었다). 직렬화 중 컬렉션 변경은 여전히 계약 위반이다.

### KI-27. `GenericMessage.ClassId` 상한 미검증 → 모듈 이니셜라이저에서 `TypeInitializationException` (해결)

**상태: 해결 (2026-09-05).** `ValidateConstructionEntries` 가 `classId == 0`(누락)만 거부하고 상한을 보지 않던 자리에 `classId > TypeMetadata.MaxMessageAttributeValue`(= `MessageWireFormat.MessageIdValueMask`, 2^24-1) 검증을 추가 — `MSGPROT008`(잘못된 GenericMessage 선언)로 컴파일 진단 승격하고 해당 구성의 등록 캐리어를 방출하지 않는다. ClassId 는 MessageId 와 같은 3바이트 와이어 슬롯(`GenericIdHeaderSize` 의 뒤 3바이트)에 담기므로 상한이 동일해야 맞다. 누락(0) 안내 메시지의 하드코딩 `16777215` 도 같은 상수로 교체. 런타임 검증(`RegisterGenericConstruction` 의 `ArgumentOutOfRangeException`, `GenericMessageAttribute.ClassId` 설정자의 `MessageAttributeRange.Validate`)은 방어층으로 유지. 회귀 테스트 4개(케이스) — `MSGPROT008_ClassId_상한_초과는_컴파일_진단으로_거부된다`(Theory: 2^24, `uint.MaxValue` → MSGPROT008 보고·`RegisterGenericConstruction<` 미방출·컴파일 오류 0) + 역방향 가드 `ClassId_경계값은_진단_없이_등록_코드를_생성한다`(Theory: 1, 2^24-1 → 진단 0·등록 코드 방출). **이빨 확인**: 수정 전 두 거부 케이스 실패(진단 없음), 경계 케이스는 통과(과잉 차단 아님). 테스트 148→152(net8.0·net9.0), Sandbox 전체 통과, Release 빌드 경고 0·오류 0. `Feature-Spec` F2 ClassId 범위·F5 MSGPROT008 사유 목록 갱신(진단 규칙 자체는 기존 것이라 분석기 릴리스 추적 파일 변경 없음).

원본 발견 내용:

`[GenericMessage(typeof(Box<int>), ClassId = 16777216)]` 처럼 24비트를 넘는 ClassId 는 생성기를 **진단 없이** 통과하고, 생성된 등록 캐리어가 `MessageSerializer.RegisterGenericConstruction<Box<int>>(16777216)` 을 `[ModuleInitializer]` 에서 호출한다. 런타임은 이미 상한을 검증하므로 예외 자체는 나지만 — 그것이 **모듈 이니셜라이저 안**이라 CLR 이 `TypeInitializationException` 으로 감싸 **모듈 로드 자체가 실패**한다. 즉 속성 값 오타 한 자리(`1677721` → `16777216`)의 증상이 "어셈블리 로드 실패"로 나타나고, 원인이 속성 값 범위라는 단서는 어디에도 없었다. 상한 검증이 없으면 값이 와이어에 3바이트로 잘려 들어갈 수 있다는 점도 문제다(그 경우 등록은 성공하지만 송수신 ClassId 가 어긋난다).

조치 방향: 컴파일 진단 승격 → 완료. `MSGPROT005`(ID 값 범위 초과)가 메시지 ID 에 대해 하는 일을 ClassId 에 대해 한 것이며, 새 진단 ID 를 늘이지 않고 기존 `MSGPROT008` 사유로 흡수했다.

### KI-28. `new` 수식어 판정이 베이스 속성만 봄 → 추상 루트 파생마다 CS0109 (해결)

**상태: 해결 (2026-09-05).** `GetStaticHidingModifier` 가 **베이스가 정적 계약을 실제로 방출하는지**를 보도록 바뀌었다 — 소스 베이스는 `MessageCodeGenerator.IsPartial`(MSGPROT001 로 거부되는 형태) && `IsConstructibleMessageType`(abstract·기본 생성 불가 = MSGPROT010, 그리고 상속 전용이라 생성을 건너뛰는 abstract 그룹 루트)일 때만 `new` 를 붙인다. 다른 어셈블리(메타데이터) 베이스는 구문 참조가 없어 partial 여부를 판정할 수 없으므로 그쪽 컴파일에서 생성됐다고 보고 **기존대로 `new` 유지**(내리면 CS0108/CS0114 로 역전). 부수적으로 `Define.cs`·`Method.cs` 에 **동일하게 복제**돼 있던 이 함수 2벌을 이미터 공용 헬퍼 하나로 통합(KI-4 와 같은 패턴)하고, 판정에 쓰는 `IsPartial`·`IsConstructibleMessageType` 을 `internal` 로 승격해 **생성 거부 조건과 `new` 조건이 한 사실 출처를 공유**하게 했다. 회귀 테스트 2개 — 추상 루트 파생은 `new static` 미방출(+진단 0·컴파일 오류 0), 역방향 가드로 구체 루트 파생은 `new static` 유지. **이빨 확인**: 판정을 옛 규칙(베이스 속성만)으로 되돌리는 돌연변이에서 추상 루트 테스트 실패(1/2 — 역방향 가드는 양쪽 통과), 복원 후 160/160. 효과: 클린 리빌드(`-t:Rebuild`) 기준 이 저장소 **CS0109 64건 → 0건**, 오류 0. 테스트 158→160(net8.0·net9.0), Sandbox 전체 통과.

원본 발견 내용:

`GetStaticHidingModifier` 는 베이스 타입의 **메시지 속성 유무**만으로 `new` 를 결정했다. 그런데 abstract `[GroupRootMessage]` 는 상속 전용이라 생성기가 정적 멤버를 아예 방출하지 않으므로(`MessageCodeGenerator` 의 의도된 건너뛰기), 그 파생 요소의 `new public static …` 는 가릴 대상이 없어 **CS0109**("멤버가 상속된 멤버를 숨기지 않습니다")가 된다. KI-24 로 추상 그룹 루트를 멤버 타입으로 쓰는 다형 패턴이 정상 지원되면서 이 형태가 일반화됐고, 클린 리빌드 기준 이 저장소에서만 64건이 쌓였다(파생 요소당 6개: `Serialize` ×2·`Deserialize` ×2·`MessageId`·`Initialize`). **증분 빌드에서는 컴파일이 건너뛰어져 경고가 전혀 보이지 않는다** — KI-12(RS2008)와 같은 함정이라, 이 저장소의 과거 "빌드 경고 0" 기록도 증분 빌드 기준이었다(실제 클린 리빌드에서는 64건). `TreatWarningsAsErrors` 를 켠 소비자에서는 이것이 곧 **빌드 실패**다.

조치 방향: 실제 방출 여부로 판정 + 복제 통합 → 완료. 남은 꼬리: 베이스가 MSGPROT002(컨테이닝 타입 non-partial)·MSGPROT003·MSGPROT005·MSGPROT007 로 거부되는 경우까지 `new` 판정에 반영하지는 않았다 — 그 경우들은 이미 컴파일 오류 진단이 떠서 소비자 빌드가 실패하므로 CS0109 하나가 더해져도 실질 영향이 없다. 검증 습관 교정: 빌드 경고 주장은 반드시 `-t:Rebuild` 로 한다.

### KI-3. 생성 로컬 이름 번호가 프로세스 전역 카운터 → 비결정적 생성 코드 (해결)

**상태: 해결 (2026-09-05).** `_uniqueIdCounter`(프로세스 전역 정적 + `Interlocked.Increment`)를 제거하고 번호를 **이미트 단위 상태**인 `EmitState.NextUniqueId()` 로 옮겼다 — `TryEmit` 이 타입별로 새 `EmitState` 를 만들므로 같은 입력은 항상 같은 번호(같은 텍스트)를 얻는다. 번호를 쓰는데 `EmitState` 를 받지 않던 헬퍼 6개(`EmitInGraphMessageWrite`·`Read`, `EmitOutOfGraphMessageWrite`·`Read`, `EmitRuntimeDispatchWrite`·`Read`)에 `state` 를 전달하도록 시그니처를 정리했고, 전역 상태라서 필요했던 `Interlocked`(와 `using System.Threading;`)도 함께 사라졌다 — 이미트는 타입별 단일 스레드라 잠금이 필요 없다. 회귀 테스트 1개: 한 컴파일에서 A→B→A→B 순서로 두 번씩 이미트해 **A₁==A₂·B₁==B₂** 를 비교(번호를 실제로 쓰는 로컬 `__item`·`__arr`·`__span`·`__refKind`·`__backId` 존재를 먼저 확인해 비교가 vacuous 해지지 않게 함). **이빨 확인**: 이미터·`EmitState` 만 되돌리자 이 테스트 실패(1/1), 복원 후 통과. 부수 증거: 클린 리빌드 2회의 생성 텍스트 전체 md5 동일(`e969267305af`). 테스트 160→161(net8.0·net9.0), Sandbox 전체 통과, 클린 리빌드 경고 0·오류 0. `Feature-Spec` F5 결정적 생성 텍스트 명문화.

원본 발견 내용:

생성 코드의 로컬 이름(`__item3`, `__coll1`, `__refKind7` …) 번호가 **컴파일러 프로세스 전역 정적 카운터**에서 나왔다. 그래서 같은 소스라도 그 프로세스가 이전에 몇 개의 메시지를 이미트했는지에 따라 번호가 밀려 **동일 입력 → 다른 출력**이 됐다. 결과는 세 가지: ① Roslyn 은 생성 출력을 비교해 "변화 없음"을 판정하는데 텍스트가 매번 달라져 **무관한 편집에도 생성 트리가 교체·재컴파일**됐다(증분 빌드 이점 상실 — KI-10 과 별개로 동작하는 손실), ② 같은 커밋에서도 생성 파일이 달라질 수 있어 빌드 재현성·CI diff 가 흔들렸고, ③ 생성 코드 감사(`EmitCompilerGeneratedFiles`) diff 가 의미 없이 흔들렸다. 전역 상태라 `Interlocked` 로 보호해야 했던 것 자체가 이 설계의 부산물이었다.

조치 방향: `EmitState` 로 이전(원장 권고 그대로) → 완료. 남은 꼬리: 헬퍼 메서드 **방출 순서**는 여전히 그래프 `_lookup.Values` 반복에 얹혀 있다(감사 원장 LOW) — 수집 순서 자체는 KI-4 에서 명시화한 멤버 순서를 따르므로 같은 입력이면 안정적이지만, `Dictionary` 열거라는 BCL 구현 세부에 의존하는 형태는 KI-4 와 같은 종류의 잔존 위험이다.

### KI-29. 구체 베이스 멤버의 파생 필드 조용한 유실 → `MSGPROT012` 경고로 가시화 (부분 해결)

**상태: 부분 해결 (2026-09-05) — "조용히" 부분이 해결됐고, 손실 자체는 와이어 형식 문제라 남았다.** 파생 메시지 타입을 가진 **구체** 메시지 베이스를 멤버 정적 타입으로 쓴 곳에 새 경고 진단 `MSGPROT012`(Warning)를 보고한다 — 컴파일 전체를 한 번 훑어 "파생 메시지 타입이 있는 non-abstract 메시지 타입" 집합(`CollectPolymorphicMessageBases`)을 만들고, 생성에 성공한 메시지의 와이어 멤버(상속 포함, 컬렉션은 요소 타입)가 그 집합에 있으면 멤버 위치에 경고를 낸다(`ReportPolymorphicMembers`). **생성은 막지 않는다** — 베이스 필드만 보내는 것은 유효한 설계일 수 있으므로 판단은 소비자에게 남기고 `#pragma warning disable MSGPROT012` 로 억제할 수 있다(픽스처 `EventHost` 가 이 억제 수단을 실제로 검증). 안내 문구는 해결책 세 가지를 명시한다 — 베이스를 `abstract` 로 선언해 런타임 디스패치(KI-24) · 멤버를 구체 요소 타입으로 선언 · 메시지 전체를 `MessageSerializer.Serialize(object)` 로 전송. 추상 베이스는 제외(디스패치로 구체 요소가 기록되므로 손실 없음), 다른 어셈블리에만 파생이 있는 베이스는 이 컴파일에서 알 수 없어 보고되지 않는다. 신규 규칙이라 `AnalyzerReleases.Unshipped.md` 에 MSGPROT012 행 추가(KI-12 규약). 회귀 테스트 5개 — 드라이버 4개(구체 베이스 멤버 경고 딱 1건·심각도 Warning·생성 유지·에러 진단 없음 / 컬렉션 요소 타입 경고 / **역방향 가드** 추상 루트 멤버는 경고 없음 + 디스패치 코드 확인 / 파생 없는 구체 타입 멤버는 경고 없음) + 실행 1개(`DispatchTests`: 현재 동작 고정 — `LoginEvent` 를 넣어도 복원 타입이 `EventBase` 이고 `User` 는 와이어에 없음). 테스트 161→166(net8.0·net9.0), 클린 리빌드 경고 0·오류 0, Sandbox 전체 통과. `Feature-Spec` F3(구체 베이스 멤버 규칙)·F5(MSGPROT012) 명문화.

원본 발견 내용 (실험 검증):

`EventBase`(구체 `[GroupRootMessage]`) 타입 멤버에 `LoginEvent` 인스턴스를 넣어 직렬화하면 **13바이트 프레임이 성공적으로 만들어지고** `User = "kim"` 은 어디에도 기록되지 않으며, 복원 결과는 `LoginEvent` 가 아니라 `EventBase` 인스턴스다(소비자 프로젝트 실험: `restored runtime type: EventBase`, `derived member User: <FIELD GONE>`). 예외도 진단도 없어 송신 측 코드는 정상으로 보이고 수신 측에서만 필드가 비어 보인다 — "로그인 이벤트의 사용자명이 비어 있다" 류의 추적 어려운 손실.

조치 방향: (a) 경고로 가시화 → 완료(이번 변경). (b) 손실 자체 제거 = 중첩 메시지 멤버를 런타임 디스패치로 바꾸는 **와이어 형식 변경**(기존 피어와 호환 파괴)이라 정책 결정이 필요 → 감사 원장 HIGH 로 열린 채 유지. 백레퍼런스 판독이 레지스트리 객체를 멤버 정적 타입으로 캐스트하는 형태(같은 인스턴스를 베이스·파생 멤버로 동시에 도달할 때 `InvalidCastException`)도 (b) 와 함께 풀어야 한다.

함정 기록: `AnalyzerReleases.Unshipped.md` 에 행을 추가할 때 마크다운 자동 서식 도구가 구분 행을 `-------- | ---------- | …` 형태로 바꿔 **RS2007**(잘못된 릴리스 헤더)을 유발했다 — KI-12 가 경고한 그대로다. 자동 서식을 타지 않는 경로(sed)로 되돌리고 클린 리빌드로 경고 0 을 확인했다(원장 LOW 등록).

### KI-30. 참조 추적 컨텍스트의 `_firstObject is null` 센티널 → null 등록 시 id 1 중복 발급 (해결)

**상태: 해결 (2026-09-05).** `SerializeContext.RegisterObject`·`TryGetObjectId`·`DeserializeContext.RegisterNewObject` 가 null 을 공개 경계에서 `ArgumentNullException` 으로 거부한다(공용 헬퍼 `ThrowNullReferenceValue`, `[DoesNotReturn]` + `NoInlining` — 주석 없이는 가드 뒤에도 nullable 흐름 분석이 value 를 maybe-null 로 봐서 CS8601·CS8604 20건이 났다). sentinel 구조 자체는 손대지 않았다 — null 이 들어올 수 없으면 `_firstObject is null` 은 모호 없이 "빈 슬롯"이므로, 첫 슬롯 최적화(두 번째 등록까지 Dictionary 미할당)가 그대로 유지된다. 회귀 테스트 5개(`ReferenceContextTests`) — 세 진입점 null 거부, 거부 후 상태 오염 없음(id 1 부터 정상 발급), **정상 경로 고정**(id 1·2·3 발급, 두 번째 등록에서 Dictionary 승격, `TryGetObjectId` 명중·비명중, `GetObject` 동일 인스턴스 복원). **이빨 확인**: `Contexts.cs` 만 되돌리자 5개 중 **4개 실패**(정상 경로 테스트만 양쪽 통과 = 과잉 변경 아님). 소비자 프로젝트 실험으로 수정 전·후 확인: 수정 전 `null → id 1, 다음 객체 → id 1`(쓰기·읽기 양쪽 중복), 수정 후 설명 메시지가 딸린 `ArgumentNullException`. 테스트 166→171(net8.0·net9.0), 클린 리빌드 경고 0·오류 0, Sandbox 전체 통과. `Public-API` 참조 추적 계약(수동 구현 필수 항목) 명문화 — 기존에 수동 구현 계약이 `SerializeContext`·`DeserializeContext`·`ReferenceKind` 를 전혀 언급하지 않던 원장 MEDIUM 도 함께 닫힌다. `Feature-Spec` F3 순환 참조 행 갱신.

원본 발견 내용 (실험 검증):

두 컨텍스트 모두 "첫 객체는 슬롯만 쓰고 Dictionary 는 두 번째 등록부터 할당"하는 최적화를 쓰는데, 빈 슬롯 판정을 `_firstObject is null` 로 했다. 그래서 `RegisterObject(null)` 은 **id 1 을 발급하고도 슬롯을 채우지 않고**, 이은 `RegisterObject(객체)` 가 다시 id 1 을 받는다(소비자 프로젝트 실험: 쓰기·읽기 양쪽 `null → 1, real → 1`). 읽기 쪽에서는 `_objects[1] = value` 가 덮어써서 `GetObject(1)` 이 백레퍼런스를 **다른 인스턴스**로 돌려준다 — 예외 없이 객체 그래프가 조용히 손상된다. 생성 코드는 참조 타입 멤버마다 `is null` 을 먼저 봐서 `ReferenceKind.Null` 을 쓰므로 이 경로를 타지 않지만, 세 메서드는 모두 **공개 API** 라 수동 구현자가 같은 계약을 모르면 밟을 수 있었고 계약은 어디에도 적혀 있지 않았다.

조치 방향: 공개 경계에서 null 거부 + 계약 문서화 → 완료. sentinel 을 별도 플래그로 바꾸는 대안은 정상 경로(객체 1~2개 메시지)에서 필드 하나를 더 읽게 만들어 채택하지 않았다 — null 거부만으로 모호성이 사라지므로 최적화는 그대로 두는 쪽이 낫다.

## 잠재 결함 (코드 리뷰)

| 번호 | 위치 | 내용 |
| ---- | ---- | ---- |
| KI-5 | 생성 `Deserialize(ref reader)` | **해결 (2026-09-08)** — 헤더 4바이트(NonId 는 1바이트)를 타입의 MessageId 와 비교해 불일치 시 `InvalidDataException` 으로 거부(아래 항목). 와이어 불변 — 합법 프레임 그대로 복호 |
| KI-9 | 그래프 밖 메시지 위임·런타임 디스패치 멤버 (`EmitOutOfGraphMessage*`·`EmitRuntimeDispatch*`) | **해결 (2026-09-07)** — 호출측 `SerializeContext` 의 오브젝트 id 추적이 위임·디스패치 쓰기로 전파되어 두 번째 등장부터 백레퍼런스로 기록·참조 동일성이 복원된다(와이어는 기존 참조 인코딩 재사용). 잔존 제약: **프레임 경계를 넘는 공유**는 별개 인스턴스로 남는다(각 프레임이 자체 컨텍스트를 쓴다 — 아래 KI-9 상세). 혼합 버전 피어(구버전 수신측)는 디스패치 멤버 위치의 백레퍼런스를 해석하지 못하므로 함께 업그레이드 필요 |
| KI-37 | `PooledBuffer`(struct) | **해결 (2026-09-08)** — 소유 상태를 참조형 홀더로 공유해 사본 이중 반납 원천 차단(아래 항목). 빈 대여 `Array.Empty` 는 풀 반납 대상에서 제외, `GetSpan(음수)` 는 계약 예외로 명문화 |
| KI-39 | `SerializerCache<T>` 복구 블록 | **해결 (2026-09-08)** — 캐시 필드 전체 volatile 화로 위치별 release/acquire 쌍 성립(아래 항목) |
| KI-10 | 증분 파이프라인 | **측정 완료(2026-09-05, 아래 기록)** — 출력 스텝은 매 편집마다 재실행되지만(`Compilation` 스텝 항상 Modified + `ForAttributeWithMetadataName` transform 출력이 컴파일별 심볼 인스턴스) 생성 텍스트는 동일해서 다운스트림 재컴파일은 이미 차단됨. 남은 비용은 편집당 생성기 CPU(메시지 타입 수에 비례)뿐이며, 근본 해결은 value-equatable 모델 재작성(대규모)이라 측정 근거로 연기 |

### KI-9 해소 상세 (2026-09-07)

**조치**: 런타임 디스패치 멤버(`T`·추상 메시지 타입)와 그래프 밖 구체 메시지 위임 멤버의 생성 쓰기가 이제 **호출측 `SerializeContext`** 로 `TryGetObjectId`·`RegisterObject` 를 호출한다 — 이미 등록된 인스턴스의 재등장은 `ReferenceKind.BackReference` + 오브젝트 id 로 기록되고, 읽기 측도 대칭으로 `DeserializeContext.GetObject` 역참조 후 복원 인스턴스를 등록한다. 쓰기는 프레임 앞에서·읽기는 프레임 뒤에서 등록하지만 그 사이 외부 컨텍스트 등록은 없으므로 id 순서는 양측이 일치한다. 와이어는 기존 참조 인코딩(`ReferenceKind` 0/1/2)을 재사용 — 새 태그 없이, 단일 인스턴스(공유 없음)의 바이트열은 종전과 동일하다.

**실험(소비자 프로젝트)**: 수정 전 같은 `StartCommand` 인스턴스를 `CommandEnvelope.Command` + `History[0]`(추상 멤버 2개)에 넣고 왕복하면 `ReferenceEquals == false`(44바이트 프레임에 풀 페이로드 2벌) — 수정 후 `true`(31바이트, 두 번째는 백레퍼런스). `GenericPair<TA,TB>` 의 `T` 멤버 쌍도 동일(64→51바이트). 회귀 테스트 3개(추상·타입 매개변수·그래프 밖 위임 — `DispatchTests`), 순환 회귀 1개 갱신(`NestingDepthTests`: 디스패치 순환이 이제 백레퍼런스로 **유한하게 기록·복원**된다), 깊은 디스패치 체인 가드 회귀 1개 신규. **이빨 확인**: 디스패치 쓰기 추적 제거 돌연변이 3/205 실패, 그래프 밖 추적 제거 돌연변이 1/205 실패.

**잔존 제약(제약 문서화)**:

1. **프레임 경계를 넘는 공유** — 디스패치된 메시지 프레임 내부는 여전히 자체 `SerializeContext` 를 쓴다. 같은 인스턴스가 바깥 그래프와 프레임 내부 페이로드 양쪽에 등장하면 프레임마다 한 번씩 중복 기록된다(경계당 1회, 무한 재귀는 KI-25 가드가 차단). 프레임 내부 인스턴스를 바깥과 공유하려면 멤버를 그래프 내부 타입으로 승격해야 한다.
2. **혼합 버전 피어** — 디스패치·위임 멤버 위치의 백레퍼런스는 이번 버전부터 발생할 수 있다. 구버전(2.1.x) 수신측 생성 코드는 `ReferenceKind.BackReference`(2) 를 뉴 오브젝트로 오해석해 프레임을 깨뜨리므로, 공유 참조 그래프를 주고받는 피어는 **양측 모두 2.2.0+ 로 재생성**되어야 한다. 구버전 송신측 → 신버전 수신측은 안전(백레퍼런스가 없는 프레임만 오니까).

### KI-10 측정 기록 (2026-09-05)

`GeneratorDriverOptions(trackIncrementalSteps: true)` 로 **무관한 편집**(메시지 타입이 아닌 클래스 본문 한 줄 변경) 전·후 드라이버를 두 번 돌려 스텝별 `IncrementalStepRunReason` 을 관측했다.

| 관측 | 결과 |
| ---- | ---- |
| 생성 텍스트 | **동일** (7,571자 / 7,571자, 생성 파일 2개 → 2개, 파일별 텍스트 동일) |
| `Compilation` 스텝 | `Modified` (컴파일 인스턴스가 매 편집마다 새로움 — 출력 스텝이 `CompilationProvider` 와 결합돼 있음) |
| `result_ForAttributeWithMetadataName` | `Modified, Modified` (transform 출력 = `INamedTypeSymbol`, 컴파일별 새 인스턴스 → 참조 동등성) |
| `SourceOutput` | **`Modified`** → 생성기 본문(문자열 방출)은 매 편집 재실행 |
| `CompilationOptions`·global aliases 계열 | `Cached`/`Unchanged` (무관한 입력은 정상 캐싱됨 — 파이프라인 자체가 깨진 것은 아님) |

해석: 비싼 쪽(생성 파일 재컴파일·재분석)은 **이미 차단**돼 있다 — Roslyn 은 출력 스텝이 내놓은 `SourceText` 를 비교하므로 텍스트가 같으면 생성 트리를 교체하지 않는다. 그 성질을 보장하는 것이 KI-3(전역 카운터 제거)이었고, 회귀 시 이 방어막이 사라진다(드라이버 수준 테스트 `무관한_편집에도_생성_파일별_텍스트는_변하지_않는다` 가 고정 — KI-3 를 되돌리면 이 테스트가 실패함을 확인). 남은 비용은 편집당 생성기 CPU(후보 타입 전부에 대한 `TypeMetadata`·`SerializationGraph`·문자열 방출)이며, 이를 없애려면 transform 출력을 값 동등 스냅샷 모델로 바꾸고 출력 스텝에서 `Compilation` 의존을 제거해야 한다 — 이미터 전부가 `ISymbol` 을 소비하므로 대규모 재작성이다. 측정상 비용이 "생성기 CPU"로 한정됐으므로(재컴파일 아님) 지금 시점에서는 연기가 맞다고 판단한다.

### KI-7. writer 증설 산술 int 오버플로 + `PatchInt32` 무경계 (해결)

**상태: 해결 (2026-09-05).** 세 곳을 함께 손봤다. ① `EnsureCapacity` 의 `_position + additional` 을 **long 비교**로 — int 합산이 GB 급 요구에서 음수로 오버플로해 증설 가드가 거짓으로 통과하고 이은 `AsSpan`·`CopyTo` 가 원인을 가리는 예외를 던지던 것 차단(감사 원장 LOW 동일 항목). ② `Grow` 의 용량 산정을 long 산술 + 배열 상한 clamp 로 바꾸고 internal 헬퍼 `ComputeGrowCapacity(currentCapacity, required)` 로 분리 — 이전 공식 `Math.Max(_buffer.Length * 2, required)` 는 버퍼가 1GB 를 넘는 순간 배증값이 음수가 되어 `Math.Max` 가 항상 **정확 요구량**을 골랐고, 그래서 매 증설이 여유 없는 대여 + 전체 복사가 되어 성장 비용이 제곱이 됐다(그 크기면 `ArrayPool` 버킷도 아니라 매번 새 배열). 페이로드 상한 `0X7FEFFFFF`(약 2.1GB)는 `WriteString` 이 명시적으로 지원하는 범위라(KI-22) 이 구간은 실제 회귀다. 요구량이 상한을 넘으면 `InvalidOperationException`(정확한 바이트 수 안내)으로 **할당을 시도하지 않고** 거부한다. ③ `PatchInt32` 가 오프셋을 **기록된 구간**(`0 .. Length-4`)으로 검증(`ArgumentOutOfRangeException`) — 이전에는 대여 배열의 미기록 바이트에 쓸 수 있었고 그 배열은 나중에 풀로 돌아간다. 회귀 테스트 15개(케이스, `WriterGrowthTests`) — 증설 용량 산정 Theory(빈 버퍼·배증 우선·요구량 우선·배증=요구량), 1GB 너머에서 음수 아님 + 여유 유지, 상한 불초과·요구량 미달 없음(8×4 조합), 상한 초과 요구의 명확한 예외, 정상 증설과 여유 용량, `PatchInt32` 정상 동작(경계 오프셋 포함)·기록 구간 밖 거부 4케이스·빈 writer 거부. **이빨 확인**: `ComputeGrowCapacity` 를 수정 전 int 산술로 되돌리는 돌연변이에서 1GB 여유 테스트 실패(1/15), 복원 후 187/187. 소비자 프로세스 실험(수정 전 → 후): `EnsureCapacity(int.MaxValue)` `OutOfMemoryException`("Array dimensions exceeded supported range") → `InvalidOperationException`(정확한 바이트 수 안내), `PatchInt32(60)` with `Length=4` **수용** → `ArgumentOutOfRangeException`, `1_500_000_000 * 2 = -1_294_967_296` 확인. 테스트 172→187(net8.0·net9.0), 프로젝트별 클린 리빌드 6/6 경고 0·오류 0, Sandbox 전체 통과. `Public-API` writer 버퍼 계약, `Feature-Spec` F7 성장 계약 명문화.

조치 방향: long 산술 + 상한 clamp + 경계 검증 → 완료. `MessageBufferWriter` 는 `ref struct` 라 `Assert.Throws` 람다 포획이 안 되어 `ref` 인자 헬퍼(`CatchEnsureCapacity`·`CatchPatchInt32`)로 예외를 관찰했다(KI-14 의 reader 테스트와 같은 함정). 남은 꼬리: `GetSpan(size)` 는 증설 후 대여 배열이 바뀌므로 **이전에 받아 간 span 이 무효**가 된다(풀 배열 별칭 위험) — 죽은 공개 API 정리 항목과 함께 별도 추적.

### KI-8. 범위 밖 `MessageCategory` 값의 조용한 마스킹 → 와이어 MessageId 변형·모듈 로드 실패 (해결)

**상태: 해결 (2026-09-05).** `TypeMetadataValidator.TryValidateCategoryRange`(신규, 상한 `MaxCategoryValue = MessageWireFormat.NibbleMask`)가 `MessageCategory` 값을 검사하고, 벗어나면 새 진단 **`MSGPROT013`(Error)** 을 보고하고 생성을 건너뜀다 — `Generate` 에서 기존 MSGPROT005(ID 값 범위) 검사 바로 뒤에 붙였다. 속성이 `Inherited = false` 라 베이스 계층을 걸을 필요 없이 자기 선언만 본다. 신규 규칙이라 `AnalyzerReleases.Unshipped.md` 에 행 추가(KI-12 규약 — 마크다운 자동 서식이 구분 행을 망가뜨리지 않도록 sed 로 편집 후 분리자 무결성 확인). 회귀 테스트 6개(케이스) — 거부 Theory(16·99·255 → MSGPROT013 Error·메시지에 원본 값 포함·생성 건너뜀·컴파일 오류 0), 실험 재현 시나리오 1개, 역방향 가드 Theory(Category0 → 헤더 `0x20`, Category15 → `0x2F` — 진단 0·생성 유지·헤더 니블 확인). **이빨 확인**: 검증 상한을 무력화하는 돌연변이(`value > 999999u`)에서 6개 중 **4개 실패**, 역방향 가드 2개는 양쪽 통과. 테스트 187→193(net8.0·net9.0), 프로젝트별 클린 리빌드 6/6 경고 0·오류 0(RS1032 = 다문장 메시지 마침표 규칙 위반을 이번에 또 밟아 수정), Sandbox 전체 통과. `Feature-Spec` F2 카테고리 범위·`[Flags]` 함정, F5 MSGPROT013 명문화.

원본 발견 내용 (실험 검증):

`ReadMessageCategoryOrDefault` 가 `(byte)(value & 0x0F)` 로 **조용히 마스킹**하고 `MessageCategoryAttribute` 는 아무 검증이 없었다(`GenericMessageAttribute.ClassId` 에는 있는 검증이 여기엔 없었다). 저장소 밖 소비자 프로젝트에서 확인한 실제 형태: `[StandaloneMessage(7)] [MessageCategory((MessageCategory)99)] MaskedCategory` 와 `[StandaloneMessage(7)] [MessageCategory(Category3)] RealCategory3` 는 99 & 0x0F = 3 이라 **동일한 와이어 MessageId 0x23000007(587202567)** 을 만들고, 모듈 로드 시 `InvalidOperationException: Message type with ID 587202567 is already registered by '…'` 로 **어셈블리 로드 자체가 실패**했다(TypeInitializationException). 오류 메시지는 상대 타입 이름만 말할 뿐 원인(카테고리 99)을 가리키지 않는다. 충돌이 없는 경우엔 더 조용해서, 피어는 개발자가 의도하지 않은 카테고리로 라우팅한다. 부수 확인: `MessageCategory` 는 `[Flags]` 라 `Category1 | Category4`(= 5) 처럼 **조합이 다른 단일 카테고리로 조용히 해석**되고, `CategoryMask`(0x0F)가 열거 멤버로 존재해 `Category15` 와 구분되지 않는다 — 값 자체는 0..15 라 범위 검사로는 못 잡으므로 `Feature-Spec` 에 함정으로 명문화했다(열거 형태 변경은 공개 API 결정 사항).

조치 방향: 컴파일 진단 승격 → 완료. 남은 꼬리(별도 추적): **동일 와이어 MessageId 를 가진 두 메시지 타입**에 대한 컴파일 진단은 여전히 없다 — 이번 실험의 충돌은 마스킹이 원인이라 MSGPROT013 으로 막히지만, id 를 그냥 복사해 붙여도 같은 모듈 로드 실패가 난다(런타임 `_registeredMessageIds` 만 검사). 감사 원장에 신규 등록.

### KI-31. 동일 와이어 MessageId 충돌이 모듈 로드 실패로만 드러남 (해결)

**상태: 해결 (2026-09-05).** 컴파일 전체에서 **모듈 로드 시 실제로 등록될 형태**의 메시지 타입만 골라(`TryGetRegisteredWireMessageId`: 제네릭 선언·NonId·partial 아님·기본 생성 불가·abstract 그룹 루트·MSGPROT005/013 거부 대상 제외) 조립된 와이어 MessageId 별로 소유자를 모으고(`ConstructionConflicts.MessageIdOwners`), 2개 이상이면 새 진단 **`MSGPROT014`(Error)** 를 각 타입에 보고하고 생성을 건너뜀다. 메시지에는 16진 와이어 ID 와 **상대 타입의 정규 이름**이 함께 실려 "어떤 값이 충돌했는지"를 가리킨다(런타임 오류는 상대 타입 이름만 말하고 flags·category·24비트 값 분해를 알려주지 않았다). 부수적으로 `TypeMetadata.Members` 를 **지연 계산**으로 바꿨다 — 이 패스처럼 속성만 필요한 컴파일 전체 순회에서 후보 타입 전부의 멤버를 순회·`MemberMetadata` 생성하지 않도록(KI-10 측정에서 남은 비용으로 지목된 생성기 CPU 에도 유리). 회귀 테스트 4개 — 동일 id 두 메시지(양쪽에 진단 2건·둘 다 생성 안 함·메시지에 상대 이름과 0x ID 포함·컴파일 오류 0), 역방향 가드 3개(**카테고리가 다르면 같은 id 값도 무충돌** = 충돌 키가 속성 원값이 아니라 조립된 와이어 ID 임을 고정, 제네릭 선언은 ClassId 가 다르면 공존, abstract 그룹 루트는 판정 제외). **이빨 확인**: 충돌 게이트를 무력화하는 돌연변이에서 1/4 실패(역방향 가드 3개는 양쪽 통과). 테스트 193→197(net8.0·net9.0), 프로젝트별 클린 리빌드 6/6 경고 0·오류 0, Sandbox 전체 통과. `AnalyzerReleases.Unshipped.md` MSGPROT014 행 추가(sed 편집 후 구분 행 무결성 확인 — 마크다운 자동 서식이 이번에도 구분 행을 망가뜨려 RS2007 함정이 재발했고 복구했다). `Feature-Spec` F2 MessageId 유일성·F5 MSGPROT014 명문화.

원본 발견 내용 (실험 검증, KI-8 작업 중 발견):

KI-8(카테고리 마스킹) 실험이 드러낸 더 넓은 사각지대다. `[StandaloneMessage(7)]` 두 개처럼 **id 를 그냥 복사해 붙여도** 두 타입이 같은 와이어 MessageId 를 조립하고, 충돌은 모듈 이니셜라이저의 `_registeredMessageIds`(`RegisterCore`)에서만 발견되어 `InvalidOperationException: Message type with ID 587202567 is already registered by '…'` → CLR 이 `TypeInitializationException` 으로 감싸 **어셈블리 로드가 실패**했다. 제네릭 구성의 (MessageId, ClassId) 충돌은 `CollectConstructionConflicts` 가 이미 컴파일 진단(MSGPROT008)으로 승격하고 있었으므로, **비제네릭 메시지 id 충돌만** 진단 없이 런타임 크래시로 남는 비대칭이었다.

조치 방향: 컴파일 진단 승격 → 완료. 연쇄 오탐 방지: MSGPROT005·MSGPROT013 으로 이미 거부될 타입은 소유자에서 뺀다 - 그렇게 하지 않으면 지난 턴 KI-8 테스트가 실제로 깨졌다(문제없는 상대 타입까지 MSGPROT014 를 맞아 생성이 막힘). **내 기존 테스트가 새 기능의 설계 결함을 잡아낸 사례**라 그 테스트를 약화시키지 않고 게이트를 고쳤다. 남은 꼬리(별도 추적): **서로 다른 두 제네릭 선언**이 같은 MessageId 값 + 같은 ClassId 를 쓰면 런타임 키 (MessageId, ClassId) 가 같아지는데, `CollectConstructionConflicts` 의 키는 (선언, ClassId) 라 이 충돌을 잡지 못한다 → 모듈 로드 실패가 남는다(감사 원장 등록) → **KI-32 로 해결**.

### KI-32. 서로 다른 제네릭 선언의 (MessageId, ClassId) 런타임 키 충돌이 모듈 로드 실패로만 드러남 (해결)

**상태: 해결 (2026-09-07).** 감사 원장 MEDIUM(2026-09-06 패스 · KI-31 남은 꼬리). 구성 등록의 실제 런타임 키는 **조립된 (제네릭 와이어 MessageId, ClassId)** 인데, 기존 충돌 검사의 키는 `(선언 원본 정의, ClassId)` 였다 - 같은 선언의 중복은 잡지만 서로 다른 두 선언이 우연히 같은 MessageId 값 + 같은 ClassId 를 쓰면 다른 키로 보고 지나쳤다. 이제 `CollectConstructionConflicts` 가 등록될 형태의 구성만 골라(`TryGetRegisteredGenericWireMessageId`: 제네릭+`[StandaloneMessage]`·partial·생성 가능·범위·속성 중복 검사 통과 선언만 - KI-31 과 같은 연쇄 오탐 방지 규약) 런타임 키별 선언 소유자를 모으고(`ConstructionConflicts.GenericRuntimeKeyOwners`), 같은 키에 선언이 2개 이상이면 새 진단 **`MSGPROT015`(Error)** 를 각 캐리어에 보고하고 등록 캐리어 생성을 건너뛴다. 메시지에는 16진 MessageId·ClassId·상대 선언 정규 이름을 실어 MSGPROT014 와 같은 형식으로 원인을 가리킨다. 소비자 프로젝트 실험: 수정 전 `[StandaloneMessage(7)] [GenericMessage(typeof(GenA<int>), ClassId=1)] class GenA<T>` + `[StandaloneMessage(7)] [GenericMessage(typeof(GenB<int>), ClassId=1)] class GenB<T>` → 첫 사용 시 `TypeInitializationException`(내부 `InvalidOperationException: Generic construction with MessageId 7 and ClassId 1 is already registered by 'GenA<int>'`) - 수정 후 컴파일에서 MSGPROT015 2건으로 거부. 회귀 테스트 4개(정방향 1 + 역방향 가드 3: ClassId 가 다르면 무충돌·MessageId 값이 다르면 무충돌·단일 선언의 여러 구성은 무충돌). **이빨 확인**: 게이트 무력화 돌연변이에서 1/4 실패. `AnalyzerReleases.Unshipped.md` MSGPROT015 행 추가(마크다운 자동 서식이 구분 행을 다시 망가뜨렸고 - RS2007 함정 재발 - 서식 훅이 닿지 않는 경로로 복구·무결성 확인). `Feature-Spec` F2 유일성 규칙·F5 진단 목록 갱신.

원본 발견 내용 (소비자 프로젝트 실험 검증):

같은 저장소 밖 소비자 프로젝트에서 두 제네릭 선언이 같은 `[StandaloneMessage(7)]` 값과 같은 `ClassId=1` 구성을 선언하면 빌드는 성공하고, 첫 직렬화 시점(모듈 이니셜라이저)에 `RegisterGenericReaderInvoker` 의 `TryAdd` 가 실패해 `InvalidOperationException` → CLR 이 `<Module>` cctor 실패로 캐싱 → **어셈블리 로드 실패**. 오류 메시지는 상대 구성 타입만 지목하고 (MessageId, ClassId) 분해를 알려주지 않는다.

### KI-33. RegisterGenericConstruction 발행 순서 경쟁 → ClassId 0 "not registered" (해결)

**상태: 해결 (2026-09-08).** 감사 원장 MEDIUM(MessageSerializer.cs:124·131). 발행 순서를 `classId → writer invoker → reader invoker` 로 재배치했다 — 생성 코드의 쓰기 경로는 `GetGenericClassId<T>()` 를 읽는데, 수정 전 순서는 writer invoker(`_writerDispatch`)가 먼저 보이므로 object dispatch 로 진입한 `Serialize` 가 classId 기록 전에 `GetGenericClassId=0` 을 읽고 안내 없는 "This generic construction is not registered for serialization" 예외를 냈다(모듈 이니셜라이저끼리는 경쟁이 불가능하지만, 수동 시작 등록(공개 API 가 안내하는 패턴)과 직렬화 워커의 경쟁에서 현실적으로 열린다). 이제 writer 가 보이는 순간 classId 는 항상 보인다. 실패 시 롤백도 재배치에 맞춰 classId 를 되돌린다(회귀 테스트: reader 키 선점으로 강제 실패 → `GetGenericClassId` 가 0으로 복귀). 순서 자체는 나노초 창이라 스트레스 테스트로 결정적 재현이 안 되는 것을 확인하고(돌연변이 검증 3회 통과 — 창이 너무 좁다), 발행 순서 단언 + 병렬 Serialize 압박 테스트로 고정한다.

원본 발견 내용 (코드 리뷰):

`RegisterGenericConstruction<T>` 가 writer invoker 등록(:124) 후 `_genericClassIds` 기록(:131) 사이에 다른 스레드의 `Serialize` 가 끼면 ClassId 0 으로 "not registered" 예외. 순서만 바꾸면 되는 결정적 결함이지만 관측 창이 좁아 실험 재현은 어렵다.

### KI-34. 공유 백레퍼런스 판독이 멤버 정적 타입으로 블라인드 캐스트 (완화)

**상태: 완화 (2026-09-08).** 감사 원장 HIGH(2026-09-05 패스, 09155d9 부분 처리). **실험 확정(2026-09-08, 2.2.0 생성 코드)**: 같은 파생 인스턴스를 구체 베이스 멤버가 먼저 기록하면(베이스 필드만 와이어에) 파생 타입 멤버의 백레퍼런스 판독이 등록된 **베이스 인스턴스**를 파생 타입으로 캐스트한다 — `InvalidCastException: Unable to cast 'EventBase' to 'LoginEvent'`. 베이스 멤버 2곳이면 예외 없이 **조용한 타입 좁힘**(같은 베이스 인스턴스 공유·파생 필드 유실), 추상(디스패치) 멤버 + 구체 멤버 조합은 구체 타입이 헤더째 기록되므로 온전히 복원된다. **완화(와이어 무영향)**: 그래프 내부·그래프 밖 위임·런타임 디스패치의 백레퍼런스 판독 3곳 모두 블라인드 캐스트 대신 타입 검사 후, 불일치 시 원인(실제 복원 타입·요구 타입·멤버)과 해법(구체 타입으로 선언 또는 베이스를 abstract 로)을 안내하는 `InvalidDataException` 을 던진다 — 와이어 바이트는 불변. 회귀 테스트 3개(예외 안내·조용한 좁힘 고정·디스패치 대조군). **여전히 열림(정책 결정 사항)**: 좁혀진 복원(베이스 필드만 유실) 자체는 와이어 변경(중첩 메시지를 디스패치로 기록) 없이는 고칠 수 없다 — 감사 원장 HIGH 로 추적 지속.

### KI-36. 참조 태그 바이트 3–255 가 NewObject 로 조용히 해석됨 (해결)

**상태: 해결 (2026-09-08).** 참조 추적 3경로(그래프 내부 `EmitInGraphMessageRead`·그래프 밖 위임 `EmitOutOfGraphMessageRead`·런타임 디스패치 `EmitRuntimeDispatchRead`)의 생성 판독 코드가 `Null(0)`·`BackReference(2)` 만 검사하고 나머지를 `else` 로 떨어뜨려, 태그 바이트 3–255 를 **NewObject 로 조용히 해석**했다. 불신 피어 입장에서 이건 검증 우회다: 손상·변조 프레임이 즉시 거부되지 않고 다음 바이트부터 객체 페이로드로 파싱되어 프레임 역동기화 → 공격자가 만든 형태의 객체로 복원되거나 엉뚱한 위치에서 늦은 예외가 났다. 수정: 세 `else` 앞에 태그가 `NewObject(1)` 인지 검사하는 분기를 두고, 아니면 값과 규격(0/1/2)을 안내하는 `InvalidDataException` — 합법 프레임(0/1/2)의 와이어 바이트는 불변, 기존 데이터 전부 그대로 복호된다. 회귀 테스트 4개(3경로 각각 태그 3·0xFF 거부 + 정상 왕복 가드). 원본 발견: 2026-09-08 병렬 서브에이전트 감사(contexts 스캔, FINDING 1).

### KI-37. PooledBuffer mutable struct 사본 → 이중 ArrayPool.Return (해결)

**상태: 해결 (2026-09-08).** 2026-09-08 병렬 서브에이전트 감사(writer 스캔, FINDING 1). `PooledBuffer` 는 소유권 핸들인데 struct 라 대입·전달마다 독립 사본이 만들어졌고, `Dispose` 는 그 사본의 필드만 비웠다 — 두 사본을 각각 Dispose 하면 같은 대여 배열이 풀에 두 번 반납돼 다음 대여자가 **다른 메시지의 데이터**를 읽는 손상(프로세스 내 정보 유출·논리 오염). .NET 의 소유권 핸들(`IMemoryOwner<T>`)이 참조형인 이유와 같은 결함이다. 해법 선택: 공개 API 표면(`public struct PooledBuffer`)을 그대로 두고(하위호환 — 클래스화는 사용법 변경이라 major), 소유 상태(`byte[]`·길이·fromPool)를 참조형 `Owner` 홀더에 옮겨 **모든 사본이 홀더를 공유**하게 했다. 어떤 사본이 먼저 Dispose 해도 반납은 정확히 한 번, 이후 모든 사본의 뷰는 비어 있다. 비용: 풀링 직렬화 결과당 24바이트 홀더 1개(페이로드 복사 회수가 이 API 의 목적이므로 지배 비용 아님). 함께 처리(같은 감사 FINDING 2·3): `FromRented` 가 0길이 배열(`Array.Empty` 싱글턴)을 `fromPool:true` 로 기록하던 것을 길이>0 조건으로 수정(공용 풀의 0길이 조기 반환은 문서화되지 않은 내부 동작), `GetSpan(음수)` 가 Span 생성자의 우연한 예외로만 막히던 것을 `ArgumentOutOfRangeException(paramName:"size")` 계약으로 명문화(위치 불변). 회귀 테스트 6개(사본 이중 Dispose·역방향 사본·SerializePooled 사본 공유·빈 대여 Dispose·GetSpan 음수 거부·정상 전진 기록).

### KI-5. 생성 Deserialize 헤더 무검증 건너뛰기 → 다른 타입 바이트의 조용한 재해석 (해결)

**상태: 해결 (2026-09-08).** 원장 최초 등록 항목. 생성 `Deserialize(ref reader)` 는 헤더를 읽어 버리기만 했다(1 또는 4바이트 무비교 skip) — 다른 타입의 바이트를 먹이면 프레임 역동기화 없이 페이로드를 그 타입 문법으로 **조용히 재해석**해, 엉뚱한 필드 값·혼란스러운 깊은 예외 또는 조용한 상태 오염으로 이어졌다. 서버에서 메시지 타입 착오는 전형적인 프로덕션 결함 클래스라 진입에서 차단한다. 해법: `EmitDeserialize` 가 `EmitSerialize` 와 같은 MessageId 4바이트 상수를 바이크해 프레임 바이트와 비교 — 불일치 시 실제 헤더(16진)·기대 MessageId·타입명을 안내하는 `InvalidDataException`. **분기는 프레임 바이트(런타임) 기준**을 유지한다: 불신 바이트가 NonId 플래그를 주장하면 4바이트 읽기를 건너뛰되 1바이트 비교에서 거부된다(위조 우회 불가). 호출 경로 전수 확인(2026-09-08 스카우트 감사): 제네릭 진입·object 디스패치·제네릭 디스패치·그래프 밖 위임·런타임 디스패치 모두 reader 가 정확히 헤더에 위치하고 생성 Serialize 는 header==MessageId 를 구성상 보장 — 검증은 모든 합법 경로에 안전(NonId 1바이트 경로 포함). 수동 구현(`IHasIdMessageSerializable`)은 검증 외부(스스로 헤더를 기록/소비) — 계약 문서에 권장으로 명시. DS_RPC 로컬 팩(`2.3.1-ki5`) 회귀: 빌드 0 오류·58/58 통과 — 합법 프레임은 전부 검증을 통과한다. 회귀 테스트 4개(타입 착오 거부·NonId 위조 헤더 거부·id 변조 거부·정상 왕복), 테스트 224→228.

### KI-38. 등록 TOCTOU — 검증과 클레임 사이에 prefill 이 캐시를 덮어쓴다 (해결)

**상태: 해결 (2026-09-08).** 2026-09-08 스레드 안전성 감사(병렬 스카우트) FINDING 1·3. 델리게이트 등록 경로(`RegisterHasIdMessage<T>`·`RegisterNonIdMessage<T>` fast path)는 검증(부수효과 없음)→prefill→클레임(`RegisterCore` 의 `_registeredTypes.TryAdd`) 순서였다. 같은 타입을 **다른 델리게이트로** 동시 등록하면 두 스레드 모두 검증을 통과하고 각자 prefill 이 권위적인 `SerializerCache<T>` 를 덮어쓴 뒤, 클레임 패자만 "already registered" 로 실패한다 — 패자의 델리게이트(A/B 혼합 포함)가 캐시에 잔류하고 디스패치 invoker 는 캐시를 경유하므로 **거부된 등록의 직렬화기가 조용히 실행**된다(KI-11 무오염 클래스의 TOCTOU 재발. ModuleInitializer 경로는 단일 스레드·동일 델리게이트라 도달하지 않고, 공개 API 의 수동 등록 경로에서 실재). 해법: `RegisterGenericConstruction` 가 이미 쓰던 올바른 형태 — **클레임 선점 후 prefill**. 델리게이트 경로는 검증 전에 `TryAdd` 로 타입을 원자적으로 선점하고(패자는 prefill 전에 예외), 실패 시 클레임을 롤백한다. `ValidateRegistration`·`RegisterCore` 는 `typeClaimed` 매개변수로 이중 선점을 피한다. 함께(같은 감사 FINDING 3): `TryRemoveGenericReaderInvoker` 의 제거 순서를 등록 순서의 역순(dispatch→owner)으로 — 같은 순서면 롤백 찰나에 owner 만 사라져 같은 키의 재등록이 새 디스패치를 롤백에 뺏길 수 있었다. 회귀 테스트 2개(배리어 동기화 이중 델리게이트 등록 — 정확히 한쪽 실패·캐시는 승자만 / 검증 거부 후 클레임 롤백 재등록). 테스트 228→230, DS_RPC 로컬 팩(`2.3.3-ki38`) 빌드+테스트 통과.

### KI-39. 캐시 복구 블록의 발행이 Serialize 를 먼저 읽는 독자하고만 짝이 됨 (해결)

**상태: 해결 (2026-09-08).** 2026-09-08 스레드 안전성 감사 FINDING 2(LOW). 등록 전 조기 접근으로 cctor 가 먼저 돈 타입은 **복구 블록**(`PrefillSerializerCache` 후반)이 cctor 밖에서 캐시 필드를 다시 쓰는데, 발행은 평문 쓰기 후 `Volatile.Write(Serialize)` release 하나로 묶였다. release/acquire 짝은 **같은 위치**에서만 성립하므로 이 발행은 `Serialize` 를 먼저 읽는 독자에게만 유효했고, `Deserialize` 만 읽는 핫 경로(`Deserialize<T>`), `MessageId` 만 읽는 경로(`RegisterGenericConstruction`)는 짝이 없어 ARM(Unity 모바일)에서 등록 완료 후에도 오래된 null/0 을 읽을 수 있었다(false `ThrowMissingDeserialize`·ClassId 0 오독 — x86 TSO 에서는 관찰 불가). 해법: `SerializerCache<T>` 필드 6개 전체를 `volatile` 로 — 각 쓰기는 release·각 읽기는 acquire 가 되어 **위치별** 동기화 쌍이 성립하고, 복구 블록의 묶음 `Volatile.Write` 는 의미가 사라져 자연순 대입으로 단순화했다. 비용: ARM64 acquire load ≈ 1사이클(LDAR), x86 무료. 관찰 불가결(ARM 하드웨어 필요)이라 회귀 테스트 대신 메모리 모델 추론으로 인자화 — 게이트는 전체 스위트(230×2 TFM)·Sandbox 38·DS_RPC 로컬 팩(`2.3.3-ki39`) 빌드+테스트 통과.

### KI-40. 힌트 이름 `+`→`_` 치환이 중첩 타입과 밑줄 이름 타입의 충돌을 만듦 (해결)

**상태: 해결 (2026-09-08).** 2026-09-08 메타데이터·속성 파싱 레이어 감사(스카우트) FINDING 1 — 유일한 크래시 클래스 결함. `SanitizeHintName` 이 중첩 구분자 `+` 를 `_` 로 치환해, `Ns.A+B`(중첩 메시지)와 실재하는 `Ns.A_B`(탑레벨 메시지)가 같은 힌트 이름을 만들었다. 둘 다 메시지 속성을 가지면 `AddSource` 가 중복 힌트 `ArgumentException` 을 던져 **AD0001 — 해당 컴파일의 생성 소스 전체 유실**(IDE 빌드·수정 제출 모두). 해법: `+` 를 허용 목록에 추가 — 식별자에는 `+` 가 절대 들어가지 못하므로 중첩 구분자로서 단사성이 구조적으로 보장된다. 중첩 메시지 타입의 힌트 파일명만 `Ns.A_B.g.cs` → `Ns.A+B.g.cs` 로 바뀐다(생성 내용·컴파일 결과 불변, IDE 표시만). 회귀 테스트 1개(중첩+밑줄 조인 이름 공존 — AD0001 없음·세 타입 모두 생성). 테스트 243→244, Sandbox 42 통과. 같은 감사의 진단 품질 LOW 2건도 해결(2026-09-08): 오류형 `ClassId = <error>` 항목은 건너뛰어 1차 컴파일 오류(CS0103)가 원인을 지목하게 하고(회귀 테스트 1개), MSGPROT005 폴백 속성명을 범용 "MessageAttribute" 로 수정(NonIdMessageAttribute 는 생성자 인자가 없어 해당 경로의 원인일 수 없었다).

### KI-41. 역직렬화 신뢰 경계의 두 미검증 경로 — 디스패치 블라인드 캐스트·NonId 플래그 오유형 예외 (해결)

**상태: 해결 (2026-09-08).** **차등 퍼저가 발견**(신규 `DeserializerFuzzTests` — 유효 프레임 3종×결정적 변이 1,500회: 비트 뒤집기·절단·극값 치환; 불변식 ①거부는 알려진 깨끗한 예외 유형만 ②성공 판독은 멱등 왕복). **발견 1(iter 116)**: 런타임 디스패치 판독의 신규 객체 분기가 `({typeName})MessageSerializer.DeserializeFromReader(...)` 로 **블라인드 캐스트** — 불신 헤더가 다른 등록 타입(FlatMessage)으로 라우팅하면 PointMessage 멤버 캐스트에서 원인 없는 `InvalidCastException`. KI-34 가 백레퍼런스 분기만 고쳤을 때 이 분기는 미커버였다. → KI-34 계열의 안내 타입 검사로 교정(안내 `InvalidDataException`). **발견 2(iter 291)**: 최상위 object dispatch 가 NonId 플래그 프레임을 `InvalidCastException("Message is not a standalone or group message")` 으로 거부 — 캐스트가 일어난 적 없는 와이어 내용 불법에 오유형 예외. → `InvalidDataException` 으로 교정(Sandbox S2·DispatchTests 계약 재고정). 두 발견 모두 예외 유형 교정 — 이미 실패는 났다, 안내·분류가 틀어져 신뢰 경계 모니터링에서 누락됐던 것. 계약 재고정: 테스트 2·Sandbox 1. 퍼저는 상주 회귀로 유지(시드 고정·재현 가능, 관측 하한 포함 — 죽은 퍼저 방지). **심화(2026-09-08)**: 코퍼스 6종(NonId·그룹 요소·깊이 60 체인 추가)·변이 5종(다중 비트·가비지 접미 추가 — 길이 접두 동시 타격·소비되지 않는 접미 쓰레기)·진입 2경로(object dispatch + 제네릭 — 이형 헤더는 KI-5 검증 경로)로 확장, 6×2,000×2 변이에서 **신규 위반 0** — 심화 코퍼스에서도 경계 유지 확인. **폴백 프로파일 코퍼스 추가(2026-09-08)**: netstandard2.1(Unity) 로 생성된 `FallbackCollections`(인덱서 루프 판독기·NaN/-0.0/∞ 페이로드 포함)를 7번째 시드로 — CollectionsMarshal 경로만 변이되던 공백을 메우고 폴백 생성 코드도 변이 하에서 **위반 0** 확인. **캠페인 노브 + 심층 캠페인(2026-09-08)**: `MSGPROT_FUZZ_SCALE` 환경변수로 온디맨드 심층 탐사(CI 는 기본 1) — **15배 캠페인(7시드×3만×2진입 ≈ 42만 판독, 양 TFM) 위반 0**, 결함 꼬리의 깊은 구간까지 정화 확인.

### KI-43. 충돌 판정 게이트가 거부 예정 선언을 계산 — 정상 타입 거짓 양성 MSGPROT014/015·거부될 선언의 등록 캐리러 방출 (해결)

**상태: 해결 (2026-09-13, 3.0.0 상용화 재검증 패스).** KI-31 이 확립한 “실제로 등록될 형태만 센다” 게이트(`GenericConstruction.TryGetRegisteredWireMessageId`·`TryGetRegisteredGenericWireMessageId`)가 3.0.0 종류 통합 이후 새로 생긴 거부 경로 두 개를 반영하지 않았다. ① **정의 밖 kind 값**(`[Message((MessageKind)99, id: 7)]` — MSGPROT018): `TypeMetadata.TryDecodeMessageAttribute` 가 실패해 Automatic 폴백 추론이 되고, 게이트가 018 검사를 건너뛰어 거부될 선언이 Standalone 으로 세 count 된다(`NonId+id` 조합은 decode 가 성공해 `IsNonIdMessage` 경로로 이미 제외되므로 정의 밖 kind 이 실제 갭). ② **계층 위반**(MSGPROT003 루트 없는 요소·MSGPROT004 루트의 루트 조상): 게이트가 계층 판정을 전혀 하지 않는다. 두 형태 모두 이미 에러 진단으로 생성이 거부되어 등록되지 않는데 카운트되므로, **같은 조립 ID/런타임 키를 쓰는 문제없는 상대 타입이 거짓 양성 MSGPROT014/MSGPROT015 로 생성·등록 캐리어까지 잃는다**(연쇄 오탐 — KI-31 이 차단하려던 바로 그 형태). 제네릭 변형에서는 반대 방향 결함이 함께 드러났다: 018 로 거부될 제네릭 선언의 구성 캐리러가 여전히 방출되어 `RegisterGenericConstruction<T>` 가 `IHasIdMessageSerializable<T>` 를 구현하지 못하는 타입을 참조 → **CS0311 컴파일 불가 생성 코드**(KI-16·18 과 같은 “진단 없이 컴파일 불가 코드 방출” 결함류). 수정: 비제네릭 게이트에 018 정합성 검사 + `ValidateRootHierarchy`(심볼 전용 오버로드를 internal 로 승격해 Generate 와 같은 판정 공유)를 추가하고, 제네릭 게이트에 018 정합성 검사를 추가, `ValidateConstructionEntries` 는 선언부가 018 불일치인 구성을 MSGPROT008 로 거부(캐리러 미방출). 회귀 테스트 3개(이빨 확인: 수정 전 전부 실패 — 계층 위반 거짓 양성 014·정의 밖 kind 거짓 양성 014·015+캐리러 CS0311) + 기존 실충돌 회귀(양방향 가드)는 그대로 통과. 테스트 323→326(net8.0·net9.0).

**2차 수정 (2026-09-13, 리뷰 라운드).** 1차 수정 직후 신규 컨텍스트 리뷰 3인(정확성·테스트·상용 적합성)이 같은 게이트·같은 결함류의 잔존 트리거를 교차 발견했다 — 1차 감사의 “유일한 실결함” 판정은 부정확했다. ① **MSGPROT002 사각지대**(비제네릭·제네릭 게이트 공통): 게이트의 `IsPartial` 은 타입 자신만 보고 중첩 컨테이닝 타입의 partial 여부를 보지 않아, MSGPROT002 로 거부될 중첩 메시지가 카운트되어 같은 조립 ID/런타임 키의 최상위 정상 타입이 거짓 양성 014/015 로 생성·캐리러를 잃었다. ② **캐리러 가드의 018 한정**: `ValidateConstructionEntries` 의 가드가 018 만 보아, MSGPROT001(비-partial)·002·005(id 범위)·013(category)·010(abstract 등)으로 거부될 선언의 캐리러도 방출되어 CS0311 이 났다. ③ **MSGPROT017 미제외**: 해시 0 Child 는 등록되지 않는데 게이트가 카운트했다(관측 가능한 피해는 없다 — Generate 가 017 지점에서 충돌 검사 앞에 반환하고, value-0 Child 조립 ID 를 가진 유효 타입은 존재할 수 없어. 규약 완결을 위한 구조적 가드). 수정: `IsNestedContainingTypesPartial` 을 internal 승격해 양 게이트가 Generate 와 같은 002 판정을 공유, 해시 0 Child 판정을 `TypeMetadata.IsHashZeroGroupElement` 로 단일화해 Generate·게이트 양쪽이 쓰게 했고, 캐리러 방출 조건은 제네릭 게이트 `TryGetRegisteredGenericWireMessageId` 판정 하나로 일원화(개별 조건 재복제 제거 — 리뷰 지적: 게이트·Generate·캐리러 3곳 조각 복제가 이 사각지대의 구조적 원인). 회귀 테스트 5개 추가(이빨 확인: 2차 수정만 되돌리면 4개 실패 — 002 거짓 양성 014 비제네릭·제네릭, 005/001 트리거 캐리러 CS0311; 017 은 규약 고정용 역방향 가드로 수정 전에도 통과 — 치아 없음을 명시) + 기존 018 캐리러 테스트에 MSGPROT008 긍정 단언 보강. 테스트 326→331(net8.0·net9.0).

**3차 수정 (2026-09-13, 리뷰 라운드 2 — 2차 일원화가 만든 회귀).** 리뷰 라운드 2가 2-컴파일레이션 재생으로 확정한 것: 2차의 캐리러 방출 조건 일원화가 **유효한 크로스 어셈블리 구성을 거짓 MSGPROT008 으로 거부**한다. 근본 원인: 게이트의 `IsPartial`·`IsNestedContainingTypesPartial` 은 `DeclaringSyntaxReferences` 를 순회하는데 메타데이터 전용 심볼(참조 어셈블리 PE)은 그것이 비어 있어 항상 false — 게이트는 외부 선언을 무조건 “등록되지 않을 형태”로 본다. HEAD(가드 없음)·1차(가드가 018 전용 — 018 검사는 이 컴파일의 속성 참조만 파싱하므로 외부 선언에 안전)에서는 동작했고, 2차가 가드를 게이트 판정으로 일원화하면서 외부 선언 순수 캐리러(프로토콜 DLL 선언+생성 + 게임 DLL `[GenericMessage(typeof(ProtoBox<int>), ClassId)]` — KI-42 가 표준으로 문서화한 구성)가 “선언부가 생성 거부될 구성”으로 오판돼 생성·등록을 잃었다. 수정: `ValidateConstructionEntries` 의 캐리러 가드를 **이 컴파일 소스 선언 한정**(`declaration.DeclaringSyntaxReferences.Length > 0`)으로 적용 — 외부 선언은 그 자체 컴파일의 게이트가 이미 검증했고, 크로스 어셈블리 (MessageId, ClassId) 중복은 ADR-0005 의 런타임 감지 계약을 따른다(게이트·`IsPartial` 자체는 손대지 않는다 — 메타데이터 심볼을 통과시키면 `CollectConstructionConflicts` 가 외부 소유자를 015 맵에 넣어 계약이 컴파일 타임 015 로 바뀐다). 015 피어 체크는 가드가 저장한 게이트 결과를 재사용(중복 호출 제거)하며 외부 선언 스킵은 종래 동작(게이트 false → 스킵)과 동일. 회귀 테스트 1개: 기존 `RunGeneratorWithMetadataBase` 하네스로 베이스 PE(선언+구성+생성, 진단 0)를 방출하고 소비자 컴파일의 순수 캐리러가 MSGPROT 진단 없이 캐리러를 방출·컴파일 오류 0임을 단언(이빨 확인: 가드를 2차 형태로 되돌리면 실패). 테스트 331→332(net8.0·net9.0).

### KI-42. 교차 어셈블리 파생의 `new` 수식어 오남용 → CS0109 경고 누적·TreatWarningsAsErrors 빌드 실패 (해결)

**상태: 해결 (2026-09-09).** KI-28(`GetStaticHidingModifier` 가 베이스의 실제 방출 여부를 보도록 교정)의 교차 어셈블리 꼬리. 프로토콜 DLL(abstract 그룹 루트) + 서버/클라이언트 DLL(구체 요소) 분리는 상용 프로젝트의 표준 구성인데, 두 가지 형태로 CS0109(“멤버가 상속된 멤버를 숨기지 않음”)가 남아 있었다. ① 메타데이터 베이스가 abstract 면 소스와 무관하게 정적 계약을 절대 방출하지 않음(abstract 그룹 루트는 생성 skip·그 외 abstract 는 MSGPROT010)에도 무조건 `new` 를 붙여 **타입당 4건**(MessageId·Initialize·Deserialize×2). ② `Initialize()` 는 `internal` 이라 어셈블리 밖의 베이스가 가진 Initialize 는 이 컴파일에서 접근 불가 — 구체 메타데이터 베이스에서도 Initialize 의 `new` 는 **항상** CS0109(타입당 1건). 해법: `BaseEmitsStaticContract` 가 abstract 를 메타데이터만으로 먼저 걸러내고(내리는 쪽이 안전 — 가릴 대상이 확실히 없다), `Initialize` 의 `new` 는 베이스가 이 컴파일 소스에 있을 때만(`GetStaticHidingModifier(typeMeta, isModuleInitializer: true)`). **IVT 예외(리뷰 라운드 보강)**: 베이스 어셈블리가 소비자 어셈블리에 `[InternalsVisibleTo]` 로 internal 접근을 열면 베이스의 internal `Initialize` 는 가릴 대상이 되돌아온다 — 이 구성에서 `new` 를 빼면 사용자가 수정할 수 없는 CS0108 이 생성 코드에 뜬다(TreatWarningsAsErrors 에서 빌드 실패). `GetStaticHidingModifier` 가 `baseType.Symbol.ContainingAssembly.GivesAccessTo(consumerAssembly)` 로 이 경우를 감지해 방출 여부 판정으로 되돌아간다. 구체 메타데이터 베이스의 public 멤버(MessageId·Deserialize×2) `new` 는 기존대로 유지 — 그쪽 컴파일에서 생성됐는지 여기서 알 수 없어 내리면 CS0108/CS0114 로 역전한다. 회귀 테스트 4개(신규 `RunGeneratorWithMetadataBase` 헬퍼 — 베이스를 별도 어셈블리로 컴파일·PE 방출 후 소비 컴파일이 메타데이터 베이스로 상속받게 한다): abstract 메타데이터 베이스 파생은 `new static` 미방출 + CS0109 0건, 구체 메타데이터 베이스 파생은 MessageId/Deserialize 만 `new` 유지 + CS0109 0건, IVT 개방 베이스는 Initialize 도 `new` 유지 + CS0108 0건, 비-IVT 베이스는 Initialize `new` 생략 고정(퇴행 가드). 기존 KI-28 회귀 2개(소스 베이스)는 그대로 통과 — 소스 경로 동작 불변. Serialize 오버로드는 첫 인자 타입이 타입마다 달라 시그니처가 달라지므로 애초에 가릴 수 없다는 점도 이번에 확정(파생 Serialize 는 `new` 없이 안전). 테스트 296→309(net8.0·net9.0; 상용화 패스 +13 — DeserializeExact 5·KI-42 회귀 4·리뷰 라운드 계약 보강 4[폴백 프로파일 DeserializeExact 2·빈 span/NonId 프레임 2]).

원본 발견 내용 (실험 검증):

베이스 어셈블리를 생성기와 함께 컴파일한 뒤(실제 프로토콜 DLL 빌드와 동일) 소비 어셈블리에서 `[GroupElementMessage]` 파생을 생성하면, abstract 공유 루트에서 파생된 요소마다 CS0109 4건(경고), 구체 공유 루트에서도 Initialize 로 1건씩 발생했다. `TreatWarningsAsErrors` CI 에서는 서버/클라이언트 프로젝트가 빌드 실패한다. KI-28 이 소스 베이스만 고쳤던 이유는 메타데이터 심볼에 구문 참조가 없어 partial 여부를 판정할 수 없었기 때문인데, abstract 여부와(internal) 접근성은 메타데이터만으로 확정 가능하므로 그 두 사실만으로 안전하게 내릴 수 있었다.

조치 방향: 판정 가능한 사실(abstract·어셈블리 경계)만으로 `new` 판정 보강 → 완료. 남은 꼬리(KI-28 과 동일): MSGPROT002·003·005·007 로 거부되는 소스 베이스는 이미 컴파일 오류 진단이 떠 CS0109 하나가 더해져도 실질 영향이 없어 반영하지 않는다.

## 관련

- [Feature-Spec](../02-Architecture/Feature-Spec.md)
- [CONTEXT](../00-AI/CONTEXT.md)
