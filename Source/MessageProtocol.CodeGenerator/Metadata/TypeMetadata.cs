using MessageProtocol;
using MessageProtocol.CodeGenerator.Reference;
using Microsoft.CodeAnalysis;

namespace MessageProtocol.CodeGenerator.Metadata
{
    /// <summary>메시지 타입 하나의 속성·멤버·계층 메타데이터.</summary>
    internal sealed class TypeMetadata
    {
        public const uint MaxMessageAttributeValue = MessageWireFormat.MessageIdValueMask;

        public INamedTypeSymbol Symbol { get; }
        public TypeDeclarationKind DeclarationKind { get; }
        public string DeclarationKeyword => TypeDeclarationKindHelper.GetDeclarationKeyword(DeclarationKind);

        public bool IsNonIdMessage { get; }
        public bool IsStandaloneMessage { get; }
        public bool IsGroupMessage { get; }
        public bool IsGroupRootMessage { get; }
        public bool IsGroupElementMessage { get; }

        /// <summary>[Message] 또는 무인수 explicit 속성으로 MessageId 를 FullName 해시로 얻는 타입인지 (종류 추론·충돌 진단 근거).</summary>
        public bool IsHashIdMessage { get; }

        public uint StandaloneMessageId { get; }
        public uint GroupRootMessageId { get; }
        public uint GroupElementMessageId { get; }

        /// <summary>생성 코드 선언·시그니처에 쓰는 이름 (타입 매개변수 포함, 예: <c>Msg&lt;T&gt;</c>).</summary>
        public string DeclarationName => Symbol.Name + (Symbol.TypeParameters.Length == 0
            ? string.Empty
            : "<" + string.Join(", ", Symbol.TypeParameters.Select(static tp => tp.Name)) + ">");

        /// <summary>선언된 접근성 한정자 — partial 생성부는 원본 선언의 접근성과 일치해야 한다(public 이 아닌 메시지 지원).</summary>
        public string AccessibilityKeyword => Symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.Private => "private",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => "public",
        };

        /// <summary>
        /// 자동 등록([ModuleInitializer]) 가능 여부. 제네릭 타입·제네릭 컨테이닝 타입 안의 타입은 불가능하다.
        /// </summary>
        public bool CanUseModuleInitializer => !Symbol.IsGenericType
            && ContainingTypes.All(static c => string.IsNullOrEmpty(c.TypeParameters));

        /// <summary>헤더 하위 니블(0~15). 속성 생성자가 category 를 안 주면 0.</summary>
        public byte Category { get; }

        public TypeMetadata? BaseTypeMetadata { get; }
        public ContainingTypeMetadata[] ContainingTypes { get; }

        readonly AttributeReferences _references;
        MemberMetadata[]? _members;

        /// <summary>
        /// 제네릭 와이어 메시지 여부: 제네릭 + Standalone 선언은 구성 선언과 무관하게
        /// 항상 헤더 플래그 Generic(0) + 구성 클래스 ID 슬롯을 쓴다.
        /// </summary>
        public bool IsGenericWireMessage => Symbol.IsGenericType && IsStandaloneMessage;

        /// <summary>
        /// FullName 해시가 0 으로 조립되는 Child — 0 은 예약값이라 MSGPROT017 로 생성이 거부된다(자동 재해시 없음).
        /// Generate 의 거부 지점과 충돌 판정 게이트가 같은 판정을 공유한다(KI-43).
        /// </summary>
        internal bool IsHashZeroGroupElement => IsHashIdMessage && IsGroupElementMessage && GroupElementMessageId == 0;

        public TypeMetadata(INamedTypeSymbol typeSymbol, AttributeReferences references)
        {
            Symbol = typeSymbol;
            _references = references;
            DeclarationKind = TypeDeclarationKindHelper.GetDeclarationKind(typeSymbol);
            ContainingTypes = GetContainingTypes(typeSymbol);

            var messageAttribute = typeSymbol.FindAttribute(references.MessageAttributeType);

            // [Message] 단일 속성에서 종류·수동 Id·category 를 해독한다. 정의되지 않은 Kind 값이면
            // 검증기(MSGPROT018)가 이미 보고했으므로 Automatic 으로 안전하게 되돌린다.
            if (!TryDecodeMessageAttribute(messageAttribute, out MessageKind kind, out uint manualId, out byte messageCategory))
            {
                kind = MessageKind.Automatic;
            }

            // Automatic 종류 추론: 조상에 메시지가 있으면 Child(GroupElement),
            // 없고 이 컴파일에 [Message] 파생이 있으면 Parent(GroupRoot), 나머지는 Standalone.
            // NonId 조상은 세지 않는다 — 와이어 정체성(루트 역할)이 없다.
            bool inferredStandalone = false, inferredGroupRoot = false, inferredGroupElement = false;
            if (messageAttribute != null && kind == MessageKind.Automatic)
            {
                if (HasMessageAncestor(typeSymbol, references))
                {
                    inferredGroupElement = true;
                }
                else if (references.HasMessageDescendant(typeSymbol))
                {
                    inferredGroupRoot = true;
                }
                else
                {
                    inferredStandalone = true;
                }
            }

            IsNonIdMessage = messageAttribute != null && kind == MessageKind.NonId;
            IsStandaloneMessage = messageAttribute != null && (kind == MessageKind.Standalone || inferredStandalone);
            IsGroupRootMessage = messageAttribute != null && (kind == MessageKind.Parent || inferredGroupRoot);
            IsGroupElementMessage = messageAttribute != null && (kind == MessageKind.Child || inferredGroupElement);
            IsGroupMessage = IsGroupRootMessage || IsGroupElementMessage;

            // 수동 Id 를 생략(0)하면 모든 Id 종류(Automatic 추론 포함)가 FullName 해시를 쓴다.
            // 해시는 선언 이름만으로 결정되므로 동일 타입이 어느 컴파일에서 해시돼도 같은 값을 가진다(와이어 안정성).
            IsHashIdMessage = messageAttribute != null && !IsNonIdMessage && manualId == 0;
            uint fullNameHash = IsHashIdMessage ? MessageIdHash.FromFullName(BuildFullName(typeSymbol)) : 0;
            uint idValue = manualId != 0 ? manualId : fullNameHash;
            StandaloneMessageId = IsStandaloneMessage ? idValue : 0;
            GroupRootMessageId = IsGroupRootMessage ? idValue : 0;
            GroupElementMessageId = IsGroupElementMessage ? idValue : 0;

            // NonId 는 id·category 인자 금지(MSGPROT018) — 검증을 통과하지 못한 조합은 0 으로 되돌린다.
            Category = IsNonIdMessage ? (byte)0 : messageCategory;

            var baseTypeSymbol = typeSymbol.BaseType;
            if (baseTypeSymbol != null &&
                baseTypeSymbol.SpecialType != SpecialType.System_Object &&
                baseTypeSymbol.SpecialType != SpecialType.System_ValueType)
            {
                BaseTypeMetadata = new TypeMetadata(baseTypeSymbol, references);
            }
        }

        /// <summary>
        /// 직렬화 멤버(무시 속성 &gt; 포함 속성 &gt; public 순). **첫 접근에서 계산**한다 —
        /// MessageId 충돌 검사처럼 속성만 필요한 컴파일 전체 패스에서 `TypeMetadata` 를 만들 때
        /// 모든 후보 타입의 멤버를 순회·`MemberMetadata` 생성하지 않도록 (Known-Issues KI-31).
        /// </summary>
        public MemberMetadata[] Members => _members ??= ComputeMembers(Symbol, _references);

        /// <summary>조상(자기 자신 제외) 중 [Message] 선언이 있는지 — 속성은 Inherited = false 라 선언부만 확인한다.</summary>
        static bool HasMessageAncestor(INamedTypeSymbol typeSymbol, AttributeReferences references)
        {
            for (var baseType = typeSymbol.BaseType;
                 baseType != null && baseType.SpecialType != SpecialType.System_Object;
                 baseType = baseType.BaseType)
            {
                if (baseType.ContainAttribute(references.MessageAttributeType))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 해시 대상 FullName — BCL <c>Type.FullName</c> 관례: 네임스페이스 점 + 중첩 <c>+</c> + 제네릭 차수 <c>`n</c>.
        /// 타입 매개변수 이름은 포함하지 않는다(이름 리팩터링으로 ID 가 바뀌면 안 된다).
        /// </summary>
        static string BuildFullName(INamedTypeSymbol typeSymbol)
        {
            string ns = typeSymbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
                ? containingNamespace.ToDisplayString() + "."
                : string.Empty;

            var containingTypes = new Stack<string>();
            for (var current = typeSymbol.ContainingType; current != null; current = current.ContainingType)
            {
                containingTypes.Push(current.MetadataName); // MetadataName 은 제네릭 차수(`n) 를 포함한다
            }

            string nested = containingTypes.Count > 0 ? string.Join("+", containingTypes) + "+" : string.Empty;
            return ns + nested + typeSymbol.MetadataName;
        }

        static MemberMetadata[] ComputeMembers(INamedTypeSymbol typeSymbol, AttributeReferences references)
        {
            // 무시 속성 > 포함 속성 > public 순으로 직렬화 대상을 고른다.
            return typeSymbol.GetMembers()
                .Where(m => m is IFieldSymbol || m is IPropertySymbol)
                .Where(m => !m.IsStatic)
                // 인덱서는 IPropertySymbol 이지만 Roslyn 이름이 "this[]" 라 멤버로 뽑히면 `message.this[]` 같은
                // 문법 오류 코드가 진단 없이 생성된다. 인수를 받아야 하므로 직렬화 멤버가 될 수 없다 (Known-Issues KI-23).
                .Where(m => m is not IPropertySymbol { IsIndexer: true })
                .Where(m =>
                {
                    bool ignore = m.ContainAttribute(references.MessageIgnoreAttributeType);
                    if (ignore) return false;
                    bool include = m.ContainAttribute(references.MessageIncludeAttributeType);
                    if (include) return true;
                    return m.DeclaredAccessibility == Accessibility.Public;
                })
                .Select(m => new MemberMetadata(m, references))
                .ToArray();
        }

        /// <summary>
        /// 와이어 페이로드 멤버 순서 — 베이스 체인을 루트 쪽부터 내려오며 **선언 순서**로 병합하고,
        /// 같은 이름의 파생 멤버가 베이스 멤버를 그림자 제거할 때 **베이스의 위치**를 유지한 채 심볼만 바꾼다.
        /// <para>
        /// 이미터와 그래프가 이 한 구현을 공유한다(이전에는 동일한 로직이 두 곳에 복제되어 있었다).
        /// 순서를 `Dictionary.Values` 열거에 맡기지 않는 이유가 핵심이다 — 그 순서는 삽입 순서일 뿐 규약이 아니라
        /// BCL 구현 세부모다. 페이로드 바이트 순서는 송수신이 반드시 일치해야 하는 와이어 형식이므로
        /// 명시적으로 고정한다 (Known-Issues KI-4).
        /// </para>
        /// </summary>
        public static IReadOnlyList<MemberMetadata> GetWireMembers(TypeMetadata typeMeta)
        {
            var ordered = new List<MemberMetadata>();
            var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
            AppendWireMembers(typeMeta, ordered, indexByName);
            return ordered;
        }

        static void AppendWireMembers(
            TypeMetadata typeMeta,
            List<MemberMetadata> ordered,
            Dictionary<string, int> indexByName)
        {
            if (typeMeta.BaseTypeMetadata != null)
            {
                AppendWireMembers(typeMeta.BaseTypeMetadata, ordered, indexByName);
            }

            foreach (var member in typeMeta.Members)
            {
                if (indexByName.TryGetValue(member.Name, out int index))
                {
                    // 그림자 제거: 위치는 베이스 선언 자리, 타입·심볼은 파생 것으로.
                    ordered[index] = member;
                }
                else
                {
                    indexByName[member.Name] = ordered.Count;
                    ordered.Add(member);
                }
            }
        }

        /// <summary>flags + category + id 값을 조립한 프로토콜 MessageId.</summary>
        public uint GetMessageId()
        {
            MessageFlag flags;
            if (IsGenericWireMessage)
            {
                // 제네릭 메시지는 전용 헤더 플래그(0) — 구성 클래스 ID가 헤더 뒤에 따라온다.
                flags = MessageFlag.Generic;
            }
            else
            {
                flags = MessageFlag.None;
                if (IsNonIdMessage) flags |= MessageFlag.NonIdMessage;
                if (IsStandaloneMessage) flags |= MessageFlag.Standalone;
                if (IsGroupRootMessage) flags |= MessageFlag.Parent;
                if (IsGroupElementMessage) flags |= MessageFlag.Child;
            }

            return MessageWireFormat.ComposeMessageId(flags, Category, GetMessageIdValue());
        }

        uint GetMessageIdValue()
        {
            if (IsStandaloneMessage) return StandaloneMessageId;
            if (IsGroupElementMessage) return GroupElementMessageId;
            if (IsGroupRootMessage) return GroupRootMessageId;
            return 0;
        }

        /// <summary>
        /// [Message] 생성자 인자를 해독한다: MessageKind 인자는 kind, MessageCategory 인자는 category 니블,
        /// 정수 인자는 수동 Id(0 = 생략 → FullName 해시). kind 가 정의되지 않은 값이면 false —
        /// 검증기가 MSGPROT018 로 보고한다.
        /// </summary>
        internal static bool TryDecodeMessageAttribute(
            AttributeData? attributeData,
            out MessageKind kind,
            out uint manualId,
            out byte category)
        {
            kind = MessageKind.Automatic;
            manualId = 0;
            category = 0;
            if (attributeData == null)
            {
                return true;
            }

            uint rawKind = (uint)MessageKind.Automatic;
            foreach (var argument in attributeData.ConstructorArguments)
            {
                if (argument.Type?.TypeKind == TypeKind.Enum)
                {
                    if (!TryConvertToUInt32(argument.Value, out uint value))
                    {
                        continue;
                    }

                    if (argument.Type.Name == nameof(MessageKind))
                    {
                        rawKind = value;
                    }
                    else
                    {
                        category = (byte)(value & MessageWireFormat.NibbleMask);
                    }
                }
                else if (TryConvertToUInt32(argument.Value, out uint id))
                {
                    manualId = id;
                }
            }

            if (rawKind > (uint)MessageKind.NonId)
            {
                return false;
            }

            kind = (MessageKind)rawKind;
            return true;
        }

        internal static bool TryConvertToUInt32(object? value, out uint result)
        {
            switch (value)
            {
                case byte byteValue:
                    result = byteValue;
                    return true;
                case sbyte sbyteValue when sbyteValue >= 0:
                    result = (uint)sbyteValue;
                    return true;
                case ushort ushortValue:
                    result = ushortValue;
                    return true;
                case short shortValue when shortValue >= 0:
                    result = (uint)shortValue;
                    return true;
                case uint uintValue:
                    result = uintValue;
                    return true;
                case int intValue when intValue >= 0:
                    result = (uint)intValue;
                    return true;
                case ulong ulongValue when ulongValue <= uint.MaxValue:
                    result = (uint)ulongValue;
                    return true;
                case long longValue when longValue >= 0 && longValue <= uint.MaxValue:
                    result = (uint)longValue;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        static ContainingTypeMetadata[] GetContainingTypes(INamedTypeSymbol typeSymbol)
        {
            var containingTypes = new Stack<ContainingTypeMetadata>();
            var current = typeSymbol.ContainingType;
            while (current != null)
            {
                containingTypes.Push(new ContainingTypeMetadata(current));
                current = current.ContainingType;
            }

            return containingTypes.ToArray();
        }
    }
}
