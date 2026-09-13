using MessageProtocol.CodeGenerator.Generate;
using MessageProtocol.CodeGenerator.Metadata;
using MessageProtocol.CodeGenerator.Reference;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace MessageProtocol.CodeGenerator
{
    /// <summary>
    /// 메시지 속성이 붙은 partial 타입을 찾아 Serialize/Deserialize/MessageId 와
    /// ModuleInitializer 자동 등록 코드를 생성하는 incremental source generator.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public class MessageCodeGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // [Message] 이 종류 선언의 유일한 속성이다. [GenericMessage] 는 구성 선언용 캐리어 속성.
            var generic = CreateAttributeProvider(context, MetadataNames.GenericMessageAttribute);
            var message = CreateAttributeProvider(context, MetadataNames.MessageAttribute);

            var candidates = generic.Collect()
                .Combine(message.Collect())
                .Select(static (sources, _) =>
                {
                    var (genericTypes, messageTypes) = sources;
                    return genericTypes
                        .Concat(messageTypes)
                        .Distinct(NamedTypeSymbolComparer.Instance)
                        .ToImmutableArray();
                });

            var compilationAndCandidates = context.CompilationProvider.Combine(candidates);

            context.RegisterSourceOutput(compilationAndCandidates, static (spc, source) =>
            {
                var (compilation, types) = source;
                var attributeReferences = new AttributeReferences(compilation, CollectMessageDescendantBases(compilation, types));
                // 컴파일 전체 구성 선언을 먼저 훑어 중복(모듈 로드 크래시 원인)을 컴파일 진단으로 승격한다.
                var conflicts = GenericConstruction.CollectConstructionConflicts(types, attributeReferences);
                // 파생 메시지 타입을 가진 구체 메시지 베이스 — 이런 타입을 멤버 정적 타입으로 쓰면
                // 선언 타입 기준으로 직렬화되어 파생 멤버가 조용히 유실된다(MSGPROT012, Known-Issues KI-29).
                var polymorphicBases = CollectPolymorphicMessageBases(types, attributeReferences);
                // 캐리어 등록 클래스 이름 유일성 상태 — 동일 접미사 충돌 시 구분자 부여.
                var usedCarrierSuffixes = new HashSet<string>();
                foreach (var typeSymbol in types)
                {
                    Generate(typeSymbol, compilation, spc, conflicts, usedCarrierSuffixes, attributeReferences, polymorphicBases);
                }
            });
        }

        static IncrementalValuesProvider<INamedTypeSymbol> CreateAttributeProvider(
            IncrementalGeneratorInitializationContext context,
            string metadataName)
        {
            // 속성별 SyntaxProvider + Collect: 타입 선언만 걸러 증분 파이프라인을 유지한다.
            return context.SyntaxProvider.ForAttributeWithMetadataName(
                metadataName,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol);
        }

        internal static void Generate(
            INamedTypeSymbol typeSymbol,
            Compilation compilation,
            SourceProductionContext context,
            ConstructionConflicts conflicts,
            HashSet<string> usedCarrierSuffixes,
            AttributeReferences? cachedReferences = null,
            ImmutableHashSet<INamedTypeSymbol>? polymorphicBases = null)
        {
            var location = typeSymbol.Locations.FirstOrDefault() ?? Location.None;
            var attributeReferences = cachedReferences ?? new AttributeReferences(compilation);

            // 구성 선언 처리: [GenericMessage(typeof(구성), ClassId)] 가 붙은 타입은 선언부·캐리어 구분 없이 등록 클래스를 출력한다.
            var constructionEntries = GenericConstruction.ParseConstructionEntries(typeSymbol, attributeReferences);
            if (constructionEntries.Count > 0)
            {
                if (GenericConstruction.ValidateConstructionEntries(typeSymbol, constructionEntries, attributeReferences, conflicts, context, location))
                {
                    GenericConstruction.EmitConstructionRegistration(typeSymbol, constructionEntries, context, usedCarrierSuffixes);
                }
            }

            // 메시지 속성이 없는 순수 캐리어는 여기서 끝.
            if (!HasMessageAttribute(typeSymbol, attributeReferences))
            {
                return;
            }

            if (!IsPartial(typeSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MustBePartial, location, typeSymbol.Name));
                return;
            }

            if (typeSymbol.ContainingType != null && !IsNestedContainingTypesPartial(typeSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NestedContainingTypesMustBePartial, location, typeSymbol.Name));
                return;
            }

            if (!TypeMetadataValidator.TryValidateMessageIdRange(typeSymbol, attributeReferences, out string invalidAttributeName, out string invalidAttributeValue))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MessageAttributeValueOutOfRange,
                    location,
                    typeSymbol.Name,
                    invalidAttributeName,
                    invalidAttributeValue));
                return;
            }

            if (!TypeMetadataValidator.TryValidateCategoryRange(typeSymbol, attributeReferences, out string invalidCategoryValue))
            {
                // 방치하면 이미터가 0x0F 로 마스킹해 **다른 와이어 MessageId** 를 만든다 — 같은 ID 의 다른 메시지와
                // 충돌하면 모듈 이니셜라이저 등록 충돌로 어셈블리 로드가 실패하고, 오류 메시지는 원인을 가리키지 않는다(KI-8).
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MessageCategoryOutOfRange,
                    location,
                    typeSymbol.Name,
                    invalidCategoryValue));
                return;
            }

            if (!TypeMetadataValidator.TryValidateMessageAttributeConsistency(typeSymbol, attributeReferences, out string argumentMismatch))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MessageArgumentMismatch,
                    location,
                    typeSymbol.Name,
                    argumentMismatch));
                return;
            }

            var typeMeta = new TypeMetadata(typeSymbol, attributeReferences);

            if (!ValidateRootHierarchy(typeSymbol, typeMeta, attributeReferences, context, location))
            {
                return;
            }

            // abstract 그룹 루트는 상속 전용이라 코드를 생성하지 않는다.
            if (typeMeta.IsGroupRootMessage && typeSymbol.IsAbstract)
            {
                return;
            }

            // [Message] 해시가 Child 위치에서 0 을 조립하면 (확률 1/2^24) — 수동 id 와
            // 동일하게 0 은 금지다. 자동 재해시는 하지 않는다: 나중 메시지 추가로 기존 ID 가 바뀌는 와이어 파손을
            // 만들지 않게, 이름을 바꾸거나 명시적 ID 속성으로 전환하게 안내한다.
            if (typeMeta.IsHashZeroGroupElement)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.GroupElementHashZero,
                    location,
                    typeSymbol.Name));
                return;
            }

            // 메시지 타입은 매개변수 없는 생성자로 인스턴스를 만들 수 있어야 한다 (추상 클래스·포지셔널 레코드 거부).
            if (!IsConstructibleMessageType(typeSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.UnconstructibleMessageType, location, typeSymbol.Name));
                return;
            }

            // 동일 와이어 MessageId 를 조립하는 두 메시지는 모듈 로드 시 `_registeredMessageIds` 등록 충돌로
            // **어셈블리 로드가 실패**한다(TypeInitializationException) — 런타임까지 기다리지 않고 여기서 거부하며
            // 상대 타입 이름을 함께 알려 원인을 찾게 한다 (Known-Issues KI-31).
            uint wireMessageId = typeMeta.GetMessageId();
            if (conflicts.TryGetMessageIdPeers(wireMessageId, typeSymbol, out string messageIdPeers))
            {
                // [Message] 해시 ID 충돌은 이름 변경·명시적 속성 전환이라는 전용 해결 경로가 있어 별도 진단으로 안내한다.
                var descriptor = typeMeta.IsHashIdMessage
                    ? DiagnosticDescriptors.HashMessageIdCollision
                    : DiagnosticDescriptors.DuplicateWireMessageId;
                context.ReportDiagnostic(Diagnostic.Create(
                    descriptor,
                    location,
                    typeSymbol.Name,
                    wireMessageId.ToString("X8"),
                    messageIdPeers));
                return;
            }

            bool hasCollectionsMarshal = compilation.GetTypeByMetadataName("System.Runtime.InteropServices.CollectionsMarshal") != null;
            if (!MessageSerializeCodeEmitter.TryEmit(typeMeta, attributeReferences, hasCollectionsMarshal, compilation.Assembly, out string? serializeCode, out var unsupportedMembers))
            {
                foreach (var unsupported in unsupportedMembers)
                {
                    var descriptor = unsupported.Kind == UnsupportedMemberKind.NotAssignable
                        ? DiagnosticDescriptors.NotAssignableMember
                        : DiagnosticDescriptors.UnsupportedMemberType;
                    context.ReportDiagnostic(Diagnostic.Create(
                        descriptor,
                        unsupported.Location,
                        unsupported.TypeName,
                        unsupported.MemberOrTypeName));
                }
                return;
            }

            // 생성을 막지 않는 경고 — 베이스 필드만 보내는 것은 유효한 설계일 수 있으므로 판단은 소비자에게 남긴다.
            ReportPolymorphicMembers(typeMeta, polymorphicBases, context);

            context.AddSource($"{GetGeneratedFileName(typeMeta.Symbol)}.g.cs", SourceText.From(serializeCode!, Encoding.UTF8));
        }

        /// <summary>
        /// 이 컴파일에 **선언된** [Message] 타입이 상속하는 베이스 집합. [Message] 베이스의 GroupRoot 자동 승격 근거로 쓰인다.
        /// 참조 어셈블리의 베이스는 넣지 않는다 — 그 베이스의 와이어 플래그는 선언부 어셈블리에서 이미 확정됐으므로
        /// 소비 컴파일에서 Root 로 재해석하면 어셈블리 간 플래그 불일치가 생긴다(크로스 어셈블리 파생은 느슨한
        /// 루트 검증 — 임의 [Message] 조상을 루트로 인정 — 로 지원한다).
        /// </summary>
        static ImmutableHashSet<INamedTypeSymbol> CollectMessageDescendantBases(Compilation compilation, ImmutableArray<INamedTypeSymbol> types)
        {
            var messageAttributeType = compilation.GetTypeByMetadataName(MetadataNames.MessageAttribute);
            if (messageAttributeType == null)
            {
                return ImmutableHashSet.Create<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            }

            var builder = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var typeSymbol in types)
            {
                if (typeSymbol.FindAttribute(messageAttributeType) == null)
                {
                    continue;
                }

                for (var baseType = typeSymbol.BaseType;
                     baseType != null && baseType.SpecialType != SpecialType.System_Object;
                     baseType = baseType.BaseType)
                {
                    // 이 컴파일에 소스가 있는 베이스만 — 참조 어셈블리 베이스는 DeclaringSyntaxReferences 가 비어 있다.
                    if (baseType.DeclaringSyntaxReferences.Length > 0)
                    {
                        builder.Add(baseType);
                    }
                }
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// 이 컴파일에서 **파생 메시지 타입을 가진 구체 메시지 타입** 집합.
        /// 이런 타입을 멤버의 정적 타입으로 쓰면 직렬화가 선언 타입 기준으로 일어나 파생 멤버가 조용히 유실된다.
        /// 추상 베이스는 제외 — 추상 메시지 타입 멤버는 런타임 디스패치로 구체 요소가 헤더째 기록되므로 손실이 없다(KI-24).
        /// </summary>
        static ImmutableHashSet<INamedTypeSymbol> CollectPolymorphicMessageBases(
            ImmutableArray<INamedTypeSymbol> types,
            AttributeReferences attributeReferences)
        {
            var builder = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            foreach (var typeSymbol in types)
            {
                // 파생 쪽이 메시지여야 와이어 정체성(MessageId)을 갖고 디스패치될 수 있다.
                if (!HasMessageAttribute(typeSymbol, attributeReferences))
                {
                    continue;
                }

                for (var baseType = typeSymbol.BaseType;
                     baseType != null && baseType.SpecialType != SpecialType.System_Object;
                     baseType = baseType.BaseType)
                {
                    if (!baseType.IsAbstract && HasMessageAttribute(baseType, attributeReferences))
                    {
                        builder.Add(baseType);
                    }
                }
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// 와이어 멤버(상속 포함) 중 "파생 메시지 타입이 있는 구체 메시지 타입"을 정적 타입으로 쓴 멤버에
        /// <see cref="DiagnosticDescriptors.PolymorphicMemberSerializesByDeclaredType"/> 경고를 보고한다.
        /// 컬렉션 멤버는 요소 타입을 본다. 다른 어셈블리에만 파생이 있는 베이스는 이 컴파일에서 알 수 없어 보고되지 않는다.
        /// </summary>
        static void ReportPolymorphicMembers(
            TypeMetadata typeMeta,
            ImmutableHashSet<INamedTypeSymbol>? polymorphicBases,
            SourceProductionContext context)
        {
            if (polymorphicBases is null || polymorphicBases.IsEmpty)
            {
                return;
            }

            foreach (var member in TypeMetadata.GetWireMembers(typeMeta))
            {
                ITypeSymbol memberType = Graph.SerializationGraph.TryGetCollectionElementType(member.Type, out var elementType)
                    ? elementType
                    : member.Type;

                if (memberType is INamedTypeSymbol named && polymorphicBases.Contains(named))
                {
                    // `?`(nullable 주석)는 진단 문장에서 노이즈다 — "'EventBase?' 를 abstract 로 선언하라"는 읽히지 않는다.
                    string declaredType = named.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString();

                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.PolymorphicMemberSerializesByDeclaredType,
                        member.Symbol.Locations.FirstOrDefault() ?? Location.None,
                        declaredType,
                        member.Name));
                }
            }
        }

        /// <summary>매개변수 없는 생성자로 만들 수 있는 구체 타입인지 확인한다. 생성 partial 은 타입 내부라 비공개 생성자도 호출 가능하다.</summary>
        internal static bool IsConstructibleMessageType(INamedTypeSymbol typeSymbol)
        {
            if (typeSymbol.IsAbstract)
            {
                return false;
            }

            if (typeSymbol.TypeKind == TypeKind.Struct)
            {
                return true;
            }

            return typeSymbol.InstanceConstructors.Any(constructor => constructor.Parameters.Length == 0);
        }

        internal static bool HasMessageAttribute(INamedTypeSymbol typeSymbol, AttributeReferences attributeReferences)
        {
            // [Message] 이 종류 선언의 유일한 속성이다 — AllowMultiple = false 라 중복 부착 자체가 컴파일 오류이다.
            return typeSymbol.ContainAttribute(attributeReferences.MessageAttributeType);
        }

        /// <summary>타입 자신의 선언부 중 하나라도 partial 한지. MSGPROT001 진단과 충돌 판정 게이트(KI-31·KI-43)가 쓴다.</summary>
        internal static bool IsPartial(INamedTypeSymbol typeSymbol)
        {
            return typeSymbol.DeclaringSyntaxReferences
                .Select(static reference => reference.GetSyntax())
                .Any(static syntax => syntax is TypeDeclarationSyntax declarationSyntax
                    && declarationSyntax.Modifiers.Any(static modifier => modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)));
        }

        /// <summary>중첩 선언의 모든 컨테이닝 타입이 partial 한지. MSGPROT002 진단과 충돌 판정 게이트가 같은 판정을 공유한다(KI-43).</summary>
        internal static bool IsNestedContainingTypesPartial(INamedTypeSymbol typeSymbol)
        {
            var containingType = typeSymbol.ContainingType;
            while (containingType != null)
            {
                if (!IsPartial(containingType))
                {
                    return false;
                }

                containingType = containingType.ContainingType;
            }

            return true;
        }

        static string GetGeneratedFileName(INamedTypeSymbol typeSymbol)
        {
            // 힌트 이름은 네임스페이스 + 중첩 + 제네릭 차수를 포함해 유일해야 한다.
            // 단순 이름만 쓰면 다른 네임스페이스의 동명 타입이 충돌해 AddSource 가 예외를 던지고,
            // 해당 컴파일의 전체 생성 소스가 유실된다.
            var typeNames = new Stack<string>();
            for (var current = typeSymbol; current != null; current = current.ContainingType)
            {
                typeNames.Push(current.MetadataName);
            }

            string typeName = string.Join("+", typeNames); // 중첩 구분자: 네임스페이스 점과 혼동 방지(메타데이터 관례 +)
            string prefix = typeSymbol.ContainingNamespace == null || typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : typeSymbol.ContainingNamespace.ToDisplayString() + ".";

            return SanitizeHintName(prefix + typeName);
        }

        internal static string SanitizeHintName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                // `+` 를 허용 목록에 넣는다(KI-40): 식별자에는 `+` 가 절대 들어가지 못하므로 중첩 구분자로서 단사성을
                // 보장한다. 이전에는 `+` 가 `_` 로 치환돼 `Ns.A+B`(중첩)가 실재하는 `Ns.A_B` 타입과 같은 힌트
                // 이름을 만들었고, 둘 다 메시지면 AddSource 가 ArgumentException(중복 힌트)을 던져 AD0001 —
                // 해당 컴파일의 생성 소스 전체가 유실됐다.
                bool allowed = char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-' || c == '(' || c == ')' || c == '`' || c == '+';
                sb.Append(allowed ? c : '_');
            }
            return sb.ToString();
        }

        /// <summary>요소 메시지는 상속 계층에 루트가, 루트 메시지의 조상은 루트가 아니어야 한다(MSGPROT003·004).
        /// GenericConstruction 의 충돌 판정 게이트(KI-43)가 같은 판정을 재사용한다.</summary>
        internal static bool ValidateRootHierarchy(INamedTypeSymbol typeSymbol, TypeMetadata typeMeta, AttributeReferences attributeReferences)
        {
            // 요소 메시지는 상속 계층에 루트가 있어야 한다.
            if (typeMeta.IsGroupElementMessage)
            {
                bool hasRoot = false;
                var current = typeMeta;
                while (current != null)
                {
                    if (current.IsGroupRootMessage)
                    {
                        hasRoot = true;
                        break;
                    }
                    current = current.BaseTypeMetadata;
                }

                // [Message] 조상은 그 자체로 그룹의 루트 역할을 한다 — 조상이 참조 어셈블리에 있으면
                // 이 컴파일에서는 Standalone 으로 해석되지만(와이어 플래그는 선언부 어셈블리가 확정),
                // 크로스 어셈블리 파생 요소의 루트 요건은 만족시킨다.
                if (!hasRoot)
                {
                    for (var baseType = typeSymbol.BaseType;
                         baseType != null && baseType.SpecialType != SpecialType.System_Object;
                         baseType = baseType.BaseType)
                    {
                        if (baseType.FindAttribute(attributeReferences.MessageAttributeType) != null)
                        {
                            hasRoot = true;
                            break;
                        }
                    }
                }

                if (!hasRoot)
                {
                    return false;
                }
            }

            // 루트 메시지의 조상이 루트일 수 없다 — 메타데이터 체인으로 확인하므로 추론 루트도 잡힌다.
            if (typeMeta.IsGroupRootMessage)
            {
                for (var baseMeta = typeMeta.BaseTypeMetadata; baseMeta != null; baseMeta = baseMeta.BaseTypeMetadata)
                {
                    if (baseMeta.IsGroupRootMessage)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        static bool ValidateRootHierarchy(
            INamedTypeSymbol typeSymbol,
            TypeMetadata typeMeta,
            AttributeReferences attributeReferences,
            SourceProductionContext context,
            Location location)
        {
            if (ValidateRootHierarchy(typeSymbol, typeMeta, attributeReferences))
            {
                return true;
            }

            if (typeMeta.IsGroupElementMessage)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.ElementMessageMustHaveRoot,
                    location,
                    typeSymbol.Name));
                return false;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.RootMessageCannotHaveRootParent,
                location,
                typeSymbol.Name));
            return false;
        }

        sealed class NamedTypeSymbolComparer : IEqualityComparer<INamedTypeSymbol>
        {
            public static readonly NamedTypeSymbolComparer Instance = new();

            public bool Equals(INamedTypeSymbol? x, INamedTypeSymbol? y)
            {
                return SymbolEqualityComparer.Default.Equals(x, y);
            }

            public int GetHashCode(INamedTypeSymbol obj)
            {
                return SymbolEqualityComparer.Default.GetHashCode(obj);
            }
        }
    }
}
