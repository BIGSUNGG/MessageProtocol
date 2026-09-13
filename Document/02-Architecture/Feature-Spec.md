---
project: DS_MessageProtocol
type: architecture
status: approved
tags: [feature-spec, rewrite, parity]
updated: 2026-09-13
---

# Feature Spec — 재작성 프로젝트 지원 기능

`Legacy/` 참조 구현이 지원하는 기능을 새 구현에서도 그대로 지원한다.
이 문서는 지원 범위(스펙)만 정의하며, 구현 방식은 재작성하며 바뀔 수 있다.

- **참조 구현**: `Legacy/` (Source · Test 50 · Examples · Document)
- **Legacy 문서**: `Legacy/Document/` (00-AI ~ 06-Troubleshooting)
- **판정 기준**: 아래 기능 스펙 + Legacy `Test/` 50개 테스트와 동등한 동작

## F1. 와이어 헤더

| 항목 | 스펙 |
| --------- | ----------------------------------------------------------------- |
| Byte 0 | flags(상위 니블) + category(하위 니블) |
| ID 메시지 | 헤더 4바이트. `MessageId = (headerByte << 24) \| (value & 0x00FFFFFF)` |
| NonId 메시지 | 헤더 1바이트 |
| 제네릭 메시지 | 헤더 7바이트: 플래그 Generic(0) + MessageId 24비트 + 구성 클래스 ID 24비트 ([ADR-0004](../05-Decisions/ADR-0004-Generic-Message-Wire-Format.md)) |
| ID 값 범위 | `0 .. 2^24-1` |

헤더 규칙은 직렬화 런타임과 코드 생성기가 공유하는 단일 소스(Legacy: `Source/Shared` Link Compile)에서 온다. 생성 `Deserialize(ref reader)` 는 헤더를 건너뛰지 않고 타입의 MessageId 와 비교해 불일치 프레임(다른 타입의 바이트·위조 NonId 헤더·변조 id)을 진입에서 `InvalidDataException` 으로 거부한다(Known-Issues KI-5) — 합법 프레임의 와이어 바이트는 불변. 수동 구현도 같은 비교를 권장한다.

## F2. 메시지 종류·카테고리

| 속성 | 역할 |
| -------------------------------- | ---------------------------- |
| `Message(MessageKind kind = Automatic, uint id = 0, MessageCategory category = Category0)` | 메시지 선언의 유일한 속성. `kind=Automatic`(기본)은 계층에서 자동 추론 — 조상에 메시지가 있으면 Child, 없고 동일 컴파일에 `[Message]` 파생이 있으면 Parent, 나머지 Standalone. `id` 생략(0)이면 타입 FullName 의 FNV-1a 해시 24비트 (`MessageIdHash` 단일 소스, 알고리즘 동결), 명시하면 수동 할당(Automatic+수동 조합 가능, 수동 0 은 표현 불가). 해시 충돌은 `MSGPROT016`, Child 위치 해시 0 은 `MSGPROT017` 로 거부(자동 재해시 없음 — 와이어 안정성). 제네릭 선언부에 쓰면 선언 MessageId 만 id 값으로 대체(구성 선언은 기존 `[GenericMessage]`). `[Message]` 조상은 자식의 루트 요건을 만족(참조 어셈블리 베이스 상속 지원) |
| `MessageKind.Standalone` | 독립 ID 메시지 |
| `MessageKind.Parent` | 부모 메시지 — 상속 계층의 꼭대기 (구 GroupRoot) |
| `MessageKind.Child` | 자식 메시지 — 부모 필수, 수동 id ≠ 0 (구 GroupElement). 해시가 0 이면 `MSGPROT017` |
| `MessageKind.NonId` | ID 없는 메시지. id·category 인자 금지 — 위반·정의 밖 kind 값은 `MSGPROT018` |
| `category` 인자 | 카테고리 니블 — 값 범위 **0 .. 15**(벗어나면 `MSGPROT013`, 조용한 `& 0x0F` 마스킹 없음). 기본값 `Category0`. `[Flags]` 열거형이라 `Category1`·`Category4` 를 결합한 값(5)은 **다른 단일 카테고리로 조용히 해석**되고 `CategoryMask`(0x0F)는 `Category15` 와 구분되지 않으므로, 항상 단일 카테고리 멤버만 쓴다 |
| `GenericMessage(typeof(닫힌 구성), ClassId)` | 제네릭 구성 선언 — 선언부·캐리어 등 임의 타입 선언에 구성마다 반복 부착 (`AllowMultiple`). `ClassId` 범위 **1 .. 2^24-1**(MessageId 와 같은 3바이트 와이어 슬롯 — 벗어나면 컴파일 진단). 제네릭 선언에는 `[Message]` 필수, 구성 미선언 직렬화는 예외 ([ADR-0005](../05-Decisions/ADR-0005-Generic-Attribute-Unification.md)) |

- 메시지 타입은 `partial` 선언이 필수.
- 그룹 계층 규칙 위반은 컴파일 진단으로 거부 (F5).
- **와이어 MessageId 는 프로토콜 내에서 유일**해야 한다 — 조립된 ID(헤더 flags + category + 24비트 값)가 같은 두 메시지 타입은 `MSGPROT014` 로 컴파일 거부된다(방치하면 모듈 로드 시 등록 충돌로 어셈블리 로드가 실패한다). 판정 대상은 실제로 등록될 형태뿐이다. 제네릭 선언은 런타임 키가 (MessageId, ClassId) 이다 — 같은 선언의 중복 선언은 구성 충돌 검사(`MSGPROT008`)가, 서로 다른 두 선언이 같은 (MessageId, ClassId) 조립 키를 쓰는 경우는 `MSGPROT015` 가 담당한다.

## F3. 멤버 타입 지원

| 분류 | 지원 |
| ------ | ------ |
| 프리미티브 | `bool, byte, sbyte, short, ushort, int, uint, long, ulong, float, double, decimal, char` |
| 문자열 | `string` (nullable 허용) |
| 열거형 | underlying 프리미티브로 직렬화 |
| 컬렉션 | 1차원 배열 `T[]`, `List<T>`, `IList<T>` (요소 타입은 재귀적으로 위 규칙 적용) |
| 중첩 메시지 | 직렬화 대상 객체 멤버 (그래프 수집) |
| 다형 멤버 | 추상 메시지 타입 멤버 — 런타임 디스패치로 구체 요소를 헤더째 기록 |
| 순환 참조 | 객체 아이디 백레퍼런스로 처리 (무한 루프 없이 round-trip). null 참조는 `ReferenceKind.Null` 로 쓰고 추적 컨텍스트에 등록하지 않는다(등록 시 id 중복 발급 — Known-Issues KI-30) |
| byte 데이터 | `byte[]` (길이 + 내용) |

미지원 타입은 컴파일 타임 에러 진단으로 거부한다 (조용한 스킵 없음).

직렬화 멤버 선택은 `MessageIgnore` > `MessageInclude` > public 접근성 순이며, 정적 멤버와 **인덱서**는 제외한다 — 인덱서는 인수를 받아야 하므로 와이어 멤버가 될 수 없고, Roslyn 심볼 이름이 `this[]` 라 포함 시 `message.this[]` 같은 문법 오류 코드가 생성된다(Known-Issues KI-23).

와이어 페이로드 **멤버 순서**는 베이스 체인을 루트 쪽부터 내려온 선언 순서이며, 파생이 같은 이름으로 그림자 제거한 멤버는 **베이스의 위치를 유지한 채 파생 심볼(타입)로 기록**된다 — `TypeMetadata.GetWireMembers` 단일 구현을 이미터·그래프가 공유한다. 순서는 송수신 와이어 호환의 일부이므로 `Dictionary` 열거 같은 BCL 구현 세부에 의존하지 않는다(Known-Issues KI-4).

컬렉션 길이·개수 접두사는 할당 전에 남은 버퍼와 검증한다 — 고정 크기 요소는 `길이×요소크기 ≤ Remaining` 정확 검증, 가변 크기 요소는 `개수 ≤ Remaining`(요소 최소 와이어 1바이트) 상한 검증, 초과 시 `EndOfStreamException` (손상·악성 패킷의 거대 할당 차단).

decimal 와이어 16바이트는 재해석 전에 flags 를 검증한다 — 스케일 >28 또는 예약 비트 존재 시 `InvalidDataException` 거부 (무효 스케일의 런타임 내부 크래시 경로 차단).

문자열은 엄격 UTF-8 로 인코딩·디코딩한다 — 고립 서로게이트가 있는 문자열은 쓰기에서 인코딩 예외로 거부하고, 무효 UTF-8 바이트는 읽기에서 `InvalidDataException` 으로 거부한다 (대체 문자로의 조용한 변환 없음 — 왕복 문자열 변형·와이어 손상 은폐 차단).

문자열 길이 접두사는 int32 이며 `-1` 만 null, `0` 은 빈 문자열이다 — 그 외 음수(`-2`…`int.MinValue`)는 규약 위반이므로 읽기에서 `InvalidDataException` 으로 거부한다 (손상 프레임이 null 문자열로 조용히 복호되는 것 차단).

중첩 객체 깊이는 **쓰기·읽기 양쪽**에서 버퍼 단위 상한으로 제한한다 — 기본 상한은 `MessageBufferReader.DefaultMaxNestingDepth = MessageBufferWriter.DefaultMaxNestingDepth = 64`(writer 쪽 상수는 reader 쪽을 참조해 **구조적으로 동일**하게 고정 — 써서 보낼 수 있는 그래프는 상대가 기본 설정으로 읽을 수 있어야 한다). 생성 코드(그래프 내부 중첩 객체·그래프 밖 메시지 위임)와 런타임 경유 지점(`SerializeToWriter`·`DeserializeFromReader` — 타입 매개변수·추상 메시지 멤버와 수동 구현의 재귀)이 재귀 지점에서 `EnterNestedObject`·`LeaveNestedObject` 쌍을 호출하고, 상한 초과 시 읽기는 `InvalidDataException`(와이어 내용 불법 — 경계 `EndOfStreamException` 과 구분), 쓰기는 `InvalidOperationException`(호출자 그래프가 너무 깊음)으로 거부한다. 이 가드가 막는 것은 두 방향 모두에서 **catch 불가한 스택 오버플로(프로세스 즉시 사망)** 다 — 수신: 자기참조 메시지에 `ReferenceKind.NewObject` 1바이트씩만 늘어놓은 20KB 남짓한 적대 프레임(KI-14), 송신: 수만 노드 연결 리스트·깊은 트리, 또는 백레퍼런스가 추적되지 않는 런타임 디스패치 멤버로 돌아가는 **순환 그래프**(KI-25). 합법적으로 깊은 객체 그래프는 `new MessageBufferReader(buffer, maxNestingDepth)` · `MessageBufferWriter.Create(initialCapacity, maxNestingDepth)` 로 **양쪽 상한을 함께** 올려 처리한다.

메시지 타입 멤버는 세 경로로 나뉜다 — (1) 그래프 내부 타입(같은 컴파일의 구체 타입)은 백레퍼런스 추적 페이로드로, (2) 그래프 밖 **구체** 메시지(다른 어셈블리 등)는 그 타입의 생성 정적 `Serialize`/`Deserialize` 위임으로, (3) **추상 메시지 타입**(예: `abstract [Message(MessageKind.Parent)]` — 상속 전용이라 생성 정적 멤버가 존재하지 않는다)은 런타임 메시지 디스패치(`SerializeToWriter`·`DeserializeFromReader`)로 기록한다. (3) 은 와이어에 구체 요소의 헤더(MessageId)를 포함하므로 수신 측이 **등록된 구체 요소 타입과 파생 멤버를 그대로 복원**한다(다형 멤버 — 베이스 타입 페이로드로 써서 파생 필드를 잃는 일이 없다). (2)·(3) 경로(타입 매개변수 `T` 멤버 포함)도 **호출측 참조 추적 컨텍스트**를 공유한다 — 같은 인스턴스가 두 번 등장하면 두 번째부터 백레퍼런스로 기록되어 참조 동일성이 복원되고, 순환도 해당 프레임 안에서 백레퍼런스로 유한하게 종결된다(Known-Issues KI-9 해소). 잔존 제약 두 가지: 디스패치된 프레임 **내부**는 자체 컨텍스트를 써서 프레임 경계를 넘는 공유는 경계마다 1회 중복 기록되고(KI-25 깊이 가드가 무한 재귀는 차단), 디스패치 멤버 위치의 백레퍼런스는 2.2.0 부터 발생하므로 구버전 수신측(2.1.x)과 공유 참조 그래프를 주고받으려면 양측 모두 재생성이 필요하다. 세 판독 경로 모두 참조 태그 바이트를 검증해 규격 밖 값(3–255)은 즉시 `InvalidDataException` 으로 거부한다 — 손상·변조 프레임이 NewObject 로 조용히 해석돼 프레임 역동기화되는 일을 막는다(Known-Issues KI-36). 합법 태그(0/1/2)의 와이어 바이트는 불변이므로 기존 데이터는 그대로 복호된다.

**구체** 베이스(파생 메시지 타입이 존재하는 non-abstract 메시지 타입)를 멤버 정적 타입으로 쓰면 (1) 경로라 선언 타입 기준으로 기록되어 파생 인스턴스의 추가 멤버가 **조용히 유실**되고 복원 타입도 베이스가 된다 — 생성기가 `MSGPROT012` **경고**로 알린다(생성은 막지 않음: 베이스 필드만 보내는 것은 유효한 설계일 수 있고 `#pragma warning disable MSGPROT012` 로 억제 가능). 다형이 필요하면 베이스를 `abstract` 로 선언해 (3) 경로(런타임 디스패치)를 쓴다 (Known-Issues KI-29).

## F4. 멤버 제어

| 속성 | 역할 |
|------|------|
| `MessageIgnore` | 직렬화 제외 |
| `MessageInclude` | 직렬화 포함 힌트 |

## F5. 코드 생성기 (컴파일 타임)

- 속성 붙은 `partial` 메시지 타입에서 `Serialize` / `Deserialize` / (ID면) `MessageId` 생성.
- `[ModuleInitializer]` 등록 코드 생성 → 모듈 로드 시 런타임에 자동 등록 (수동 등록 불필요).
- Incremental generator. 생성 텍스트는 **결정적**이다 — 로컬 이름 번호가 이미트 단위 상태(`EmitState`)라 같은 입력은 항상 같은 출력을 내고, 컴파일러 프로세스의 이전 컴파일 이력에 의존하지 않는다(Roslyn 의 생성 출력 비교가 무관한 편집에 무효화되지 않음 — Known-Issues KI-3). 측정(KI-10 측정 기록): 출력 스텝 자체는 `Compilation` 의존 때문에 매 편집 재실행되지만, 생성 텍스트가 동일하므로 Roslyn 의 출력 비교가 **생성 트리 교체·재컴파일을 막는다** — 남은 비용은 편집당 생성기 CPU 뿐이다.
- 제네릭 메시지 타입 지원: `[GenericMessage(typeof(닫힌 구성), ClassId = n)]` 단일 속성으로 구성 선언(선언부·캐리어 무관) — 헤더 플래그 Generic(0) + MessageId 뒤에 구성 클래스 ID 24비트 와이어, 선언 구성은 모듈 로드 시 자동 등록(송수신 무설정), 다중 타입 매개변수 지원. 제네릭 + 스탠드얼론 선언은 항상 제네릭 와이어이며 **구성 선언 필수**(미선언 직렬화는 예외) ([ADR-0005](../05-Decisions/ADR-0005-Generic-Attribute-Unification.md)).
- `[Message]` 단일 선언 속성: `MessageKind`·`uint id`·`MessageCategory` 생성자 인자로 종류·ID·카테고리를 모두 지정 — kind 기본 `Automatic` 은 종류·ID 없이 선언만으로 메시지 등록(종류는 상속 계층에서 추론, ID 는 FullName FNV-1a 해시 24비트 마스크). 파생 클래스는 다른 프로젝트(참조 어셈블리)의 메시지 베이스를 상속해도 속성만 붙이면 인식·등록된다. 생성 partial 선언부는 원본 접근성(`public`/`internal` 등)을 그대로 따른다.
- 수동 구현 지원: 생성기 없이 동일한 계약 형태(`IMessageSerializable<T>` 등)를 직접 구현·등록 가능. 수동 구현 시 헤더는 사용자가 직접 쓴다.
- 진단 (Legacy 기준, 동등한 검출 필요):
  - `MSGPROT001` 메시지 타입은 partial 필수
  - `MSGPROT002` 중첩 메시지의 컨테이닝 타입 partial 필수
  - `MSGPROT003` 요소 메시지는 계층에 루트 필수
  - `MSGPROT004` 루트 메시지의 부모가 루트일 수 없음
  - `MSGPROT005` ID 값 범위 초과
  - `MSGPROT006` 미지원 멤버 타입
  - `MSGPROT007` 메시지 속성 중복 (경고 — 상호 배타, 생성 건너뜀. Legacy에 없는 신규 진단. 3.0.0 종류 속성 통합 이후 단일 `[Message]`(`AllowMultiple = false`) 이라 발생 불가 — 이중 히스토리 보존 목록)
  - `MSGPROT008` 잘못된 GenericMessage 선언 (비메시지 구성 대상·미바운드 제네릭·ClassId 누락/중복/범위 초과(0 또는 2^24 이상 - 방치하면 모듈 이니셜라이저에서 `TypeInitializationException`)/컴파일 내 중복 선언/선언부가 생성 거부될 구성 - MSGPROT001·002·005·010·013·018 사유 전부. 방치하면 캐리러가 구현 없는 타입을 참조하는 CS0311 생성 코드 방출. 캐리러 방출 조건은 제네릭 게이트 판정(`TryGetRegisteredGenericWireMessageId`)으로 일원화하되 **거부는 이 컴파일 소스 선언 한정** — 메타데이터 전용 외부 어셈블리 선언은 원 컴파일의 게이트가 검증했고 크로스 어셈블리 중복은 ADR-0005 런타임 감지 계약을 따른다, KI-43)
  - `MSGPROT009` (삭제됨 — `MSGPROT008` 로 흡수)
  - `MSGPROT010` 메시지 타입 생성 불가 (추상 클래스·매개변수 없는 생성자 없음 — 포지셔널 레코드 등. Legacy에 없는 신규 진단)
  - `MSGPROT011` 멤버 대입 불가 (읽기 전용·초기화 전용 프로퍼티·읽기전용 필드 — 역직렬화 불가. Legacy에 없는 신규 진단)
  - `MSGPROT012` 멤버가 선언 타입으로 직렬화됨 (**경고** — 파생 메시지 타입이 있는 *구체* 메시지 베이스를 멤버 정적 타입으로 쓰면 파생 인스턴스의 추가 멤버가 예외 없이 유실. Legacy에 없는 신규 진단. **탐지 범위 제한**: 이 컴파일에 보이는 파생만 대상이라 파생이 다른 어셈블리에만 있는 베이스는 보고되지 않고, 메시지 루트의 와이어 멤버만 검사해 중첩 페이로드 클래스 내부의 베이스 멤버는 보지 않는다 — 분석기 한계로 Known-Issues KI-29 에 명문화)
  - `MSGPROT013` `MessageCategory` 값 범위 초과(0..15) — 방치하면 `& 0x0F` 마스킹으로 와이어 MessageId 가 달라져 다른 메시지와 ID 충돌(모듈 로드 실패) 또는 피어 오라우팅. Legacy에 없는 신규 진단
  - `MSGPROT014` 와이어 MessageId 중복 — 조립된 ID(flags+category+24비트 값)가 같은 두 메시지 타입. 방치하면 모듈 이니셜라이저 등록 충돌로 `TypeInitializationException`(어셈블리 로드 실패). Legacy에 없는 신규 진단
  - `MSGPROT015` 제네릭 구성 런타임 키 중복 — 서로 다른 두 제네릭 선언이 같은 (MessageId, ClassId) 조합을 쓰면 `RegisterGenericReaderInvoker` 가 모듈 이니셜라이저에서 충돌해 `TypeInitializationException`(어셈블리 로드 실패). Legacy에 없는 신규 진단
  - `MSGPROT016` FullName 해시 MessageId 충돌 — 24비트 해시가 같은 두 해시 ID 타입. 이름 변경·수동 id 할당(`[Message(id: …)]`) 전환으로 해결하게 안내(자동 재해시는 와이어 파손 위험으로 하지 않음)
  - `MSGPROT017` 자식 메시지 해시 0 — Child 위치의 FullName 해시가 0(예약)이면 거부. 이름 변경·수동 id 전환 안내
  - `MSGPROT018` `[Message]` 인자·종류 불일치 — `NonId` 에 id·category 인자 전달 또는 정의 밖 `MessageKind` 값. Legacy에 없는 신규 진단

## F6. 런타임 `MessageSerializer`

| API | 설명 |
| ----- | ------ |
| `RegisterHasIdMessage<T>()` / `RegisterNonIdMessage<T>()` | ID / NonId 등록 (델리게이트 오버로드 포함) |
| `RegisterType(Type)` | 리플렉션 기반 등록 |
| `Serialize<T>(T)` | 선언 타입 기준 제네릭 캐시 경로 (`byte[]`) — 런타임 타입 미참조 |
| `Serialize(object)` / `SerializeToWriter` | 런타임 타입 dispatch — 다형성(베이스 변수 + 파생 인스턴스) |
| `SerializePooled*` | ArrayPool 기반 결과 (`PooledBuffer`, Dispose 멱등) |
| `Deserialize<T>(...)` | 제네릭 역직렬화 |
| `Deserialize(byte[] \| Span \| Memory)` | 헤더 MessageId 기반 object 역직렬화 (Standalone/Group만, NonId 불가) |

계약 인터페이스: `IMessageSerializable<T>`, `IHasIdMessageSerializable<T>` (+`static uint MessageId`).

등록 캐시(`SerializerCache<T>`)는 타입별 1회 채워지며 **영구 실패 상태를 남기지 않는다** — 리플렉션으로 계약 멤버를 못 찾아도 캐시 초기화는 던지지 않고 null 로 남긴 뒤 사용·등록 지점에서 `InvalidOperationException` 으로 알린다(CLR 이 타입별로 영구 캐싱하는 `TypeInitializationException` 이 아님). 등록 전에 캐시가 먼저 초기화됐더라도 이후 델리게이트 등록이 캐시를 채워 복구한다. Prefill 발행은 `volatile` 플래그 + `Volatile.Write` 로 찢어진 상태(null·혼합 델리게이트)가 캐시에 고정되지 않는다(Known-Issues KI-11).

## F7. 성능 계약

핫 경로에서 다음 특성을 유지한다 (회귀 시 벤치마크로 확인):

- 제네릭 경로는 Dictionary lookup·박싱 없음 (정적 캐시).
- `ArrayPool` + 풀링 버퍼 (`SerializePooled` / `PooledBuffer`).
- Span 기반 읽기·쓰기, 문자열은 중간 `ToArray` 없이 디코딩.
- `decimal`은 무할당 경로.
- 생성 코드는 고정 크기 프리미티브 구간을 일괄 `EnsureCapacity`.
- 중첩 깊이 가드(쓰기·읽기 동일 상한)는 중첩 객체당 인라인 증감 2회뿐 — 할당·딕셔너리 조회 없음, 중첩 없는 flat 메시지 비용 0, 값 타입(구조체) 중첩은 계상하지 않음.
- 컬렉션 멤버 쓰기는 멤버 표현식을 **1회만** 평가해 로컬로 스냅샷한다(null 판정 포함) — 길이·루프 조건·인덱서가 각자 게터를 다시 부르지 않으며, `CollectionsMarshal` 유무·선언 타입(`T[]`·`List<T>`·`IList<T>`)과 무관하게 동일 규약이다(Known-Issues KI-26).
- writer 증설은 `long` 산술 + 배열 상한 clamp — 1GB 너머에서도 배증 여지를 유지해 성장 비용이 제곱(매 증설마다 정확 요구량 대여 + 전체 복사)으로 퇴보하지 않으며, 상한 초과 요구는 할당 시도 없이 `InvalidOperationException`(Known-Issues KI-7).
- flat 메시지(멤버 1~2개)는 Dictionary 할당 없음.

## F8. 호환성

- 타깃: `netstandard2.1` (+ `net6.0`) — Unity 호환.
- `ModuleInitializer` polyfill 포함.
- 테스트는 최신 런타임 멀티타깃 (Legacy 기준 `net8.0;net9.0`).

## F9. 패키지 구성

| 패키지 | 내용 |
| -------- | ------ |
| **MessageProtocol** | 앱용 단일 진입점: Core 런타임 + CodeGenerator 를 NuGet 의존성으로 함께 설치 |
| **MessageProtocol.Core** | 런타임 API 단독 |
| **MessageProtocol.CodeGenerator** | 생성기 단독 (고급·세분화 참조용) |

## F10. 검증 산출물

- 유닛 테스트: round-trip(타입 종류 × 멤버 조합) · 생성기 진단 · 등록/디스패치 — Legacy 50 테스트 동등.
- `CollectionsMarshal` 폴백 경로 실행 검증: `Test/MessageProtocol.NetStandardFixtures`(netstandard2.1 = Unity 호환 프로필)가 고속 경로 없는 생성 코드를 담은 어셈블리를 제공하고, Tests(net8.0/net9.0)가 그것을 참조해 **실행**으로 왕복·할당 가드(KI-17)·중첩 깊이 가드(KI-14/25)를 검증한다 — 이 저장소의 테스트 TFM 에는 `CollectionsMarshal` 이 항상 있어 폴백 생성 코드가 텍스트 단언에만 의존했던 공백을 채운다.
- 벤치마크: 직렬화 핫 경로 (BenchmarkDotNet).
- `Sandbox/MessageProtocol.Sandbox`: 실행 가능 인수 조건(왕복·디스패치·제네릭·깊이 가드·신뢰 경계 거부 등 42 시나리오, 통과 시 종료 코드 0). S14(2026-09-08)는 이형 바이트·위조 NonId 헤더·규격 밖 참조 태그의 진입 거부(KI-5·KI-36)를 인수 수준에서 고정한다. 별도의 콘솔 예제 프로젝트는 없다 — 최소 사용 예는 `README.md` 와 `Legacy/` 참조 구현이 담당한다.

## 범위 밖 (Legacy와 동일)

- 네트워크 전송 → DS_Communication
- RPC 디스패치·원격 호출 → DS_RPC
- 제네릭 메시지 `T` 의 비메시지(원시) 타입 인스턴스화, 디스패치 프레임 **경계를 넘는** 공유·순환 참조의 완전한 단일 인스턴스 복원(프레임 내부 공유·순환은 2.2.0 부터 복원 — [ADR-0004](../05-Decisions/ADR-0004-Generic-Message-Wire-Format.md)·Known-Issues KI-9 참고).

### 추후 재검토 후 구현 예정 (2026-09-09 등록)

MessagePack·MemoryPack 기능 비교(2026-09-09)에서 미지원으로 확인된 항목 중 아래 네 가지는 **추후 더 고려해본 후 구현할 예정**이다. 이 외의 미지원 기능(스키마 진화는 [ADR-0006](../05-Decisions/ADR-0006-No-Schema-Evolution.md) 으로 미지원 확정, 압축·Typeless·TypeScript 생성 등)은 여전히 범위 밖이다.

| 기능 | 현재 상태 | 구현 시 고려 사항 |
| ------ | ------ | ------ |
| `Dictionary`·nullable 값 타입 등 멤버 타입 추가 | 미지원 — 스펙 동결 | 신규 멤버 타입 규격 추가. 별도 결정(05-ADR)으로 확장 |
| 컴팩트 정수 인코딩(varint) | 미지원 — 고정폭 | **와이어 형식 변경을 수반** — 기존 프레임 호환(2.3.9)·버전 정책과 함께 결정 필요. 작은 정수 위주 메시지에서 와이어 절반 수준 절약 vs 인코딩 분기 비용 트레이드오프 |
| 스트리밍 I/O (`Stream`·`PipeWriter`·비동기) | 미지원 — `byte[]`/Span 완결 버퍼 동기 API 만 | API 수준 추가로 와이어 무영향. 대형 스트림 처리·파일 I/O 대응 |
| 기존 인스턴스 역직렬화(Overwrite) | 미지원 — 항상 새 인스턴스 | API 수준 추가로 와이어 무영향. 오브젝트 풀링 서버의 역직렬화 무할당 경로 |

## Legacy 대비 재작성 변경점 (2026-08-31)

| 항목 | Legacy | 재작성 |
| ------ | -------- | -------- |
| 멤버 속성 동작 | `MessageIgnore`/`MessageInclude`가 전역 네임스페이스라 생성기가 못 찾아 **무효** | `MessageProtocol` 네임스페이스로 옮겨 실제 동작 |
| `IList<T>` + CollectionsMarshal | AsSpan 방출로 컴파일 에러 가능 | 선언 타입이 `List<T>` 일 때만 고속 경로 |
| 생성기 TFM | netstandard2.1 | netstandard2.0 (분석기 표준) |
| 수동 메시지 헤더 | 문서 미비 | 와이어 순서(헤더 바이트 → ID 3바이트) 기록 규칙 명문화 |
| 버전 | 1.0.1 | 2.0.0 (패키지 아이디 3종 유지) |

## 관련

- [[ADR-0001-Rewrite-Bootstrap]] — 구현 전 확정 결정 (네임스페이스·테스트·순서·버전)
- [CONTEXT](../00-AI/CONTEXT.md)
- Legacy 문서: `Legacy/Document/01-Overview/Scope.md`, `Legacy/Document/03-Reference/Public-API.md`
