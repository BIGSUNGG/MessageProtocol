using MessageProtocol;
using MessageProtocol.CodeGenerator.Reference;
using Microsoft.CodeAnalysis;

namespace MessageProtocol.CodeGenerator.Metadata
{
    /// <summary>Attribute, member, and hierarchy metadata for a single message type.</summary>
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

        /// <summary>Whether the type derives its MessageId from a FullName hash via [Message] or a parameterless explicit attribute (grounds for kind inference and collision diagnostics).</summary>
        public bool IsHashIdMessage { get; }

        public uint StandaloneMessageId { get; }
        public uint GroupRootMessageId { get; }
        public uint GroupElementMessageId { get; }

        /// <summary>Name used in generated declarations and signatures (includes type parameters, e.g. <c>Msg&lt;T&gt;</c>).</summary>
        public string DeclarationName => Symbol.Name + (Symbol.TypeParameters.Length == 0
            ? string.Empty
            : "<" + string.Join(", ", Symbol.TypeParameters.Select(static tp => tp.Name)) + ">");

        /// <summary>Declared accessibility keyword — the generated partial must match the original declaration's accessibility (supports non-public messages).</summary>
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
        /// Whether auto-registration ([ModuleInitializer]) is possible. Impossible inside generic types or
        /// generic containing types.
        /// </summary>
        public bool CanUseModuleInitializer => !Symbol.IsGenericType
            && ContainingTypes.All(static c => string.IsNullOrEmpty(c.TypeParameters));

        /// <summary>Header's lower nibble (0–15). Zero when the attribute constructor omits the category.</summary>
        public byte Category { get; }

        public TypeMetadata? BaseTypeMetadata { get; }
        public ContainingTypeMetadata[] ContainingTypes { get; }

        readonly AttributeReferences _references;
        MemberMetadata[]? _members;

        /// <summary>
        /// Whether this is a generic wire message: generic + Standalone declarations always use header flag
        /// Generic(0) plus a construction class-ID slot, regardless of construction declarations.
        /// </summary>
        public bool IsGenericWireMessage => Symbol.IsGenericType && IsStandaloneMessage;

        /// <summary>
        /// A Child whose FullName hash assembles to 0 — 0 is reserved, so generation is rejected with MSGPROT017
        /// (no automatic rehash). Generate's rejection point and the conflict-detection gate share this judgment (KI-43).
        /// </summary>
        internal bool IsHashZeroGroupElement => IsHashIdMessage && IsGroupElementMessage && GroupElementMessageId == 0;

        public TypeMetadata(INamedTypeSymbol typeSymbol, AttributeReferences references)
        {
            Symbol = typeSymbol;
            _references = references;
            DeclarationKind = TypeDeclarationKindHelper.GetDeclarationKind(typeSymbol);
            ContainingTypes = GetContainingTypes(typeSymbol);

            var messageAttribute = typeSymbol.FindAttribute(references.MessageAttributeType);

            // Decode kind, manual id, and category from the single [Message] attribute. An undefined Kind value was
            // already reported by the validator (MSGPROT018), so safely fall back to Automatic.
            if (!TryDecodeMessageAttribute(messageAttribute, out MessageKind kind, out uint manualId, out byte messageCategory))
            {
                kind = MessageKind.Automatic;
            }

            // Automatic kind inference: a message ancestor makes it Child (GroupElement); no ancestor but a
            // [Message] descendant in this compilation makes it Parent (GroupRoot); otherwise Standalone.
            // NonId ancestors are not counted — they have no wire identity (root role).
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

            // Omitting the manual id (0) makes every id kind (Automatic inference included) use the FullName hash.
            // The hash depends only on the declared name, so the same type hashes to the same value in any
            // compilation (wire stability).
            IsHashIdMessage = messageAttribute != null && !IsNonIdMessage && manualId == 0;
            uint fullNameHash = IsHashIdMessage ? MessageIdHash.FromFullName(BuildFullName(typeSymbol)) : 0;
            uint idValue = manualId != 0 ? manualId : fullNameHash;
            StandaloneMessageId = IsStandaloneMessage ? idValue : 0;
            GroupRootMessageId = IsGroupRootMessage ? idValue : 0;
            GroupElementMessageId = IsGroupElementMessage ? idValue : 0;

            // NonId forbids id and category arguments (MSGPROT018) — combinations that fail validation fall back to 0.
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
        /// Serializable members (ignore attribute &gt; include attribute &gt; public). **Computed on first access** so
        /// that whole-compilation passes that only need attributes — like the MessageId conflict check — do not
        /// walk every candidate type's members and construct `MemberMetadata` (Known-Issues KI-31).
        /// </summary>
        public MemberMetadata[] Members => _members ??= ComputeMembers(Symbol, _references);

        /// <summary>Whether any ancestor (excluding self) declares [Message] — the attribute is Inherited = false, so only declarations count.</summary>
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
        /// Hash target FullName — BCL <c>Type.FullName</c> convention: namespace dots + nesting <c>+</c> + generic arity <c>`n</c>.
        /// Type parameter names are excluded (the ID must not change on a type-parameter rename).
        /// </summary>
        static string BuildFullName(INamedTypeSymbol typeSymbol)
        {
            string ns = typeSymbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
                ? containingNamespace.ToDisplayString() + "."
                : string.Empty;

            var containingTypes = new Stack<string>();
            for (var current = typeSymbol.ContainingType; current != null; current = current.ContainingType)
            {
                containingTypes.Push(current.MetadataName); // MetadataName includes the generic arity (`n)
            }

            string nested = containingTypes.Count > 0 ? string.Join("+", containingTypes) + "+" : string.Empty;
            return ns + nested + typeSymbol.MetadataName;
        }

        static MemberMetadata[] ComputeMembers(INamedTypeSymbol typeSymbol, AttributeReferences references)
        {
            // Serializable target selection order: ignore attribute > include attribute > public.
            return typeSymbol.GetMembers()
                .Where(m => m is IFieldSymbol || m is IPropertySymbol)
                .Where(m => !m.IsStatic)
                // Indexers are IPropertySymbols, but their Roslyn name "this[]" would emit syntactically invalid code
                // like `message.this[]` without a diagnostic. They take arguments, so they can never be serializable
                // members (Known-Issues KI-23).
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
        /// Wire payload member order — merges the base chain from the root down in **declaration order**, and when a
        /// derived member shadows a base member of the same name, replaces only the symbol while keeping the
        /// **base's position**.
        /// <para>
        /// The emitter and the graph share this one implementation (the same logic used to be duplicated in two
        /// places). The key point is why order is not delegated to `Dictionary.Values` enumeration — that order is
        /// merely insertion order, a BCL implementation detail rather than a contract. Payload byte order is a wire
        /// format that sender and receiver must agree on, so it is pinned explicitly (Known-Issues KI-4).
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
                    // Shadow removal: keep the base declaration's position, take the derived type and symbol.
                    ordered[index] = member;
                }
                else
                {
                    indexByName[member.Name] = ordered.Count;
                    ordered.Add(member);
                }
            }
        }

        /// <summary>Protocol MessageId assembled from flags + category + id value.</summary>
        public uint GetMessageId()
        {
            MessageFlag flags;
            if (IsGenericWireMessage)
            {
                // Generic messages use the dedicated header flag (0) — the construction class ID follows the header.
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
        /// Decodes [Message] constructor arguments: a MessageKind argument gives the kind, a MessageCategory argument
        /// the category nibble, and an integer argument the manual id (0 = omitted → FullName hash). Returns false for
        /// an undefined kind value — the validator reports it as MSGPROT018.
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
