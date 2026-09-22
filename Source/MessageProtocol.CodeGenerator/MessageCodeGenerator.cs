using MessageProtocol.CodeGenerator.Generate;
using MessageProtocol.CodeGenerator.Metadata;
using MessageProtocol.CodeGenerator.Reference;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

// Suppresses RS1024 (4.3-bundled-analyzer false positive): NamedTypeSymbolComparer purely delegates to
// SymbolEqualityComparer.Default, but older bundled analyzers do not recognize custom comparers. The 4.14
// bundled analyzers report no warning.
#pragma warning disable RS1024

namespace MessageProtocol.CodeGenerator
{
    /// <summary>
    /// Incremental source generator that finds partial types marked with the message attribute and generates
    /// Serialize/Deserialize/MessageId plus ModuleInitializer-based auto-registration code.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public class MessageCodeGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // [Message] is the only attribute of this kind on a declaration; [GenericMessage] is the carrier attribute for construction declarations.
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
                // Scan all construction declarations in the compilation first and promote duplicates (a module-load crash cause) to compile-time diagnostics.
                var conflicts = GenericConstruction.CollectConstructionConflicts(types, attributeReferences);
                // Concrete message bases that have derived message types — using such a type as a member's static type
                // serializes by the declared type, silently dropping derived members (MSGPROT012, Known-Issues KI-29).
                var polymorphicBases = CollectPolymorphicMessageBases(types, attributeReferences);
                // Uniqueness state for carrier registration class names — disambiguates on identical-suffix collisions.
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
            // Per-attribute syntax provider + Collect: filters to type declarations to keep the incremental pipeline.
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

            // Construction declarations: a type carrying [GenericMessage(typeof(construction), ClassId)]
            // emits a registration class, with no declaration/carrier distinction.
            var constructionEntries = GenericConstruction.ParseConstructionEntries(typeSymbol, attributeReferences);
            if (constructionEntries.Count > 0)
            {
                if (GenericConstruction.ValidateConstructionEntries(typeSymbol, constructionEntries, attributeReferences, conflicts, context, location))
                {
                    GenericConstruction.EmitConstructionRegistration(typeSymbol, constructionEntries, context, usedCarrierSuffixes);
                }
            }

            // Pure carriers without the message attribute end here.
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
                // Left unvalidated, the emitter masks with 0x0F and produces a **different wire MessageId** — if it
                // collides with another message of the same ID, assembly load fails on a module-initializer
                // registration conflict, and the error message does not point at the cause (KI-8).
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

            // Abstract group roots are inheritance-only, so no code is generated for them.
            if (typeMeta.IsGroupRootMessage && typeSymbol.IsAbstract)
            {
                return;
            }

            // If the [Message] hash assembles 0 in a Child position (probability 1/2^24), 0 is forbidden just like
            // for manual ids. No automatic rehash: it would corrupt the wire by changing existing IDs when a later
            // message is added — instead, guide the user to rename the type or switch to an explicit ID attribute.
            if (typeMeta.IsHashZeroGroupElement)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.GroupElementHashZero,
                    location,
                    typeSymbol.Name));
                return;
            }

            // Message types must be instantiable via a parameterless constructor (rejects abstract classes and positional records).
            if (!IsConstructibleMessageType(typeSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.UnconstructibleMessageType, location, typeSymbol.Name));
                return;
            }

            // Two messages assembling the same wire MessageId fail **assembly load** at module load with a
            // `_registeredMessageIds` registration conflict (TypeInitializationException) — reject here rather than
            // waiting for runtime, and name the peer type so the cause can be found (Known-Issues KI-31).
            uint wireMessageId = typeMeta.GetMessageId();
            if (conflicts.TryGetMessageIdPeers(wireMessageId, typeSymbol, out string messageIdPeers))
            {
                // [Message] hash ID collisions have a dedicated resolution path — rename or switch to an explicit
                // attribute — so a separate diagnostic guides the user.
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

            // Non-blocking warning — emitting only base fields can be a valid design, so the judgment is left to the consumer.
            ReportPolymorphicMembers(typeMeta, polymorphicBases, context);

            context.AddSource($"{GetGeneratedFileName(typeMeta.Symbol)}.g.cs", SourceText.From(serializeCode!, Encoding.UTF8));
        }

        /// <summary>
        /// The set of bases inherited by [Message] types **declared in this compilation**. Used as the grounds for
        /// auto-promoting [Message] bases to GroupRoot. Bases from referenced assemblies are excluded — their wire
        /// flags were already fixed in the declaring assembly, so reinterpreting them as Root in the consuming
        /// compilation would create cross-assembly flag mismatches (cross-assembly derivation is supported via
        /// loose root validation, which accepts any [Message] ancestor as a root).
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
                    // Only bases with source in this compilation — reference-assembly bases have empty DeclaringSyntaxReferences.
                    if (baseType.DeclaringSyntaxReferences.Length > 0)
                    {
                        builder.Add(baseType);
                    }
                }
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// The set of **concrete message types that have derived message types** in this compilation.
        /// Using such a type as a member's static type serializes by the declared type and silently drops derived members.
        /// Abstract bases are excluded — abstract message-typed members dispatch at runtime and the concrete element is
        /// written with its header, so nothing is lost (KI-24).
        /// </summary>
        static ImmutableHashSet<INamedTypeSymbol> CollectPolymorphicMessageBases(
            ImmutableArray<INamedTypeSymbol> types,
            AttributeReferences attributeReferences)
        {
            var builder = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            foreach (var typeSymbol in types)
            {
                // The derived side must be a message to have a wire identity (MessageId) and be dispatchable.
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
        /// Reports <see cref="DiagnosticDescriptors.PolymorphicMemberSerializesByDeclaredType"/> warnings on wire
        /// members (inherited included) whose static type is a concrete message type with derived message types.
        /// Collection members are judged by their element type. Bases whose only derived types live in other
        /// assemblies are unknown to this compilation and therefore not reported.
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
                    // The `?` (nullable annotation) is noise in a diagnostic sentence — "declare 'EventBase?' abstract" does not read.
                    string declaredType = named.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString();

                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.PolymorphicMemberSerializesByDeclaredType,
                        member.Symbol.Locations.FirstOrDefault() ?? Location.None,
                        declaredType,
                        member.Name));
                }
            }
        }

        /// <summary>Checks the type is a concrete type constructible via a parameterless constructor. Generated partials may call private constructors within the type.</summary>
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
            // [Message] is the only attribute of this kind on a declaration — AllowMultiple = false, so duplicate attachment is itself a compile error.
            return typeSymbol.ContainAttribute(attributeReferences.MessageAttributeType);
        }

        /// <summary>Whether any of the type's own declarations is partial. Used by the MSGPROT001 diagnostic and the conflict-detection gate (KI-31, KI-43).</summary>
        internal static bool IsPartial(INamedTypeSymbol typeSymbol)
        {
            return typeSymbol.DeclaringSyntaxReferences
                .Select(static reference => reference.GetSyntax())
                .Any(static syntax => syntax is TypeDeclarationSyntax declarationSyntax
                    && declarationSyntax.Modifiers.Any(static modifier => modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)));
        }

        /// <summary>Whether all containing types of a nested declaration are partial. The MSGPROT002 diagnostic and the conflict-detection gate share this judgment (KI-43).</summary>
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
            // Hint names must be unique, including namespace + nesting + generic arity.
            // Using the simple name alone would collide same-named types from different namespaces, throwing
            // out of AddSource and losing the entire generated source for the compilation.
            var typeNames = new Stack<string>();
            for (var current = typeSymbol; current != null; current = current.ContainingType)
            {
                typeNames.Push(current.MetadataName);
            }

            string typeName = string.Join("+", typeNames); // nesting separator: avoids confusion with namespace dots (metadata convention '+')
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
                // Allow `+` on the whitelist (KI-40): identifiers can never contain `+`, so it is injective as a
                // nesting separator. Previously `+` was replaced with `_`, so `Ns.A+B` (nested) produced the same
                // hint name as a real `Ns.A_B` type; if both were messages, AddSource threw ArgumentException
                // (duplicate hint) as AD0001 and the compilation lost all of its generated source.
                bool allowed = char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-' || c == '(' || c == ')' || c == '`' || c == '+';
                sb.Append(allowed ? c : '_');
            }
            return sb.ToString();
        }

        /// <summary>Element messages must have a root in their inheritance hierarchy, and a root message's ancestors must not be roots (MSGPROT003/004).
        /// GenericConstruction's conflict-detection gate (KI-43) reuses this judgment.</summary>
        internal static bool ValidateRootHierarchy(INamedTypeSymbol typeSymbol, TypeMetadata typeMeta, AttributeReferences attributeReferences)
        {
            // Element messages must have a root in their inheritance hierarchy.
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

                // A [Message] ancestor acts as the group's root itself — if that ancestor lives in a referenced
                // assembly, this compilation resolves it as Standalone (the declaring assembly fixes the wire
                // flags), but the loose check still satisfies the root requirement of cross-assembly derived elements.
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

            // A root message's ancestor cannot be a root — checked via the metadata chain so inferred roots are caught too.
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
