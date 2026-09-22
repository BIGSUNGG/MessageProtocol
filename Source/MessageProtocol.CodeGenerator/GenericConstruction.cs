using MessageProtocol.CodeGenerator.Generate;
using MessageProtocol.CodeGenerator.Metadata;
using MessageProtocol.CodeGenerator.Reference;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace MessageProtocol.CodeGenerator
{
    // ------- Generic construction registration sub-feature (split out of MessageCodeGenerator — structural audit FINDING 3, 2026-09-08) -------
    //
    // Owns parsing, validation, conflict collection, and registration-carrier emission for
    // [GenericMessage(typeof(construction), ClassId)] declarations. The MSGPROT008/014/015 diagnostics and the
    // KI-27/KI-31/KI-32 contracts live here; its only coupling to message emission (MessageCodeGenerator.Generate)
    // is that Generate calls this class's four entry points.

    internal static class GenericConstruction
    {
        /// <summary>
        /// The generic wire MessageId that construction registration will **actually assemble** at module load.
        /// Returns false for declarations that will not be registered: non-generic, not a [Message] declaration,
        /// or already rejected as non-partial / nested in a non-partial containing type / not constructible
        /// (MSGPROT001, MSGPROT002, MSGPROT010) or by ID/category range or argument-kind mismatch
        /// (MSGPROT005, MSGPROT013, MSGPROT018). Such declarations are never generated or registered, so they are
        /// excluded from conflict detection — same anti-cascading-false-positive contract as
        /// <see cref="TryGetRegisteredWireMessageId"/> (KI-31, KI-43). Construction-carrier emission gates on
        /// this one judgment as well (KI-43).
        /// </summary>
        static bool TryGetRegisteredGenericWireMessageId(
            INamedTypeSymbol declaration,
            AttributeReferences attributeReferences,
            out uint messageId)
        {
            messageId = 0;

            if (!declaration.IsGenericType
                || !declaration.ContainAttribute(attributeReferences.MessageAttributeType))
            {
                return false;
            }

            if (!MessageCodeGenerator.IsPartial(declaration) || !MessageCodeGenerator.IsConstructibleMessageType(declaration))
            {
                return false;
            }

            // Generation of a declaration whose nested containing types are non-partial is rejected as MSGPROT002
            // (KI-43 second pass) — same judgment as Generate.
            if (!MessageCodeGenerator.IsNestedContainingTypesPartial(declaration))
            {
                return false;
            }

        if (!TypeMetadataValidator.TryValidateMessageIdRange(declaration, attributeReferences, out _, out _) ||
            !TypeMetadataValidator.TryValidateCategoryRange(declaration, attributeReferences, out _) ||
            !TypeMetadataValidator.TryValidateMessageAttributeConsistency(declaration, attributeReferences, out _))
        {
            return false;
        }

        var typeMeta = new TypeMetadata(declaration, attributeReferences);
        if (!typeMeta.IsGenericWireMessage)
        {
            return false;
        }

        messageId = typeMeta.GetMessageId();
        return true;
    }

    /// <summary>
    /// Assembles and returns the wire MessageId that will **actually be registered** in `_registeredMessageIds`
    /// at module load. Returns false for shapes that will not be registered: NonId (no embedded ID), generic
    /// declarations (their runtime key is (MessageId, ClassId), so construction-conflict checking owns them),
    /// non-partial / nested in a non-partial containing type / not constructible / abstract group root
    /// (MSGPROT001, MSGPROT002, MSGPROT010 — inheritance-only, so generation is skipped), and violations of
    /// ID/category range, argument kind, hierarchy, or hash-zero Child (MSGPROT005, MSGPROT013, MSGPROT018,
    /// MSGPROT003/004, MSGPROT017). Only types passing this gate are counted for conflict detection — counting
    /// types that would not be generated anyway produces false positives (KI-31, KI-43).
    /// </summary>
    static bool TryGetRegisteredWireMessageId(
        INamedTypeSymbol typeSymbol,
        AttributeReferences attributeReferences,
        out uint messageId)
    {
        messageId = 0;

        if (typeSymbol.IsGenericType || !MessageCodeGenerator.HasMessageAttribute(typeSymbol, attributeReferences))
        {
            return false;
        }

        if (!MessageCodeGenerator.IsPartial(typeSymbol) || !MessageCodeGenerator.IsConstructibleMessageType(typeSymbol))
        {
            return false;
        }

        // Generation of a declaration whose nested containing types are non-partial is rejected as MSGPROT002
        // (KI-43 second pass) — same judgment as Generate.
        if (!MessageCodeGenerator.IsNestedContainingTypesPartial(typeSymbol))
        {
            return false;
        }

        // Types already rejected by MSGPROT005 (ID range), MSGPROT013 (category range), or MSGPROT018
        // (argument/kind mismatch) are never generated or registered, so they are excluded from conflict
        // detection — otherwise that one type would make **even blameless peer types** fail with MSGPROT014 and
        // block generation (cascading false positives). Counting only shapes that will actually be registered is
        // this gate's contract. A kind value outside MSGPROT018's definition fails decode, after which
        // TypeMetadata falls back to Automatic inference, so it must be filtered out explicitly here (KI-43).
        if (!TypeMetadataValidator.TryValidateMessageIdRange(typeSymbol, attributeReferences, out _, out _) ||
            !TypeMetadataValidator.TryValidateCategoryRange(typeSymbol, attributeReferences, out _) ||
            !TypeMetadataValidator.TryValidateMessageAttributeConsistency(typeSymbol, attributeReferences, out _))
        {
            return false;
        }

        var typeMeta = new TypeMetadata(typeSymbol, attributeReferences);
        if (typeMeta.IsNonIdMessage || (!typeMeta.IsStandaloneMessage && !typeMeta.IsGroupMessage))
        {
            return false;
        }

        // Types rejected by MSGPROT003 (element without a root) or MSGPROT004 (root with a root ancestor) are not
        // registered either (KI-43) — the gate and the generator share one source of truth by using the same
        // hierarchy judgment as Generate.
        if (!MessageCodeGenerator.ValidateRootHierarchy(typeSymbol, typeMeta, attributeReferences))
        {
            return false;
        }

        // Types rejected by MSGPROT017 (hash-zero Child) are not registered either (KI-43 second pass) — same invariant.
        // Generate refuses generation at this point, so excluding the type here keeps it from framing blameless
        // peers with an id it would never assemble anyway.
        if (typeMeta.IsHashZeroGroupElement)
        {
            return false;
        }

        messageId = typeMeta.GetMessageId();
        return MessageWireFormat.HasEmbeddedMessageId((byte)(messageId >> 24));
    }

    internal    static List<(INamedTypeSymbol? Construction, uint ClassId)> ParseConstructionEntries(
        INamedTypeSymbol typeSymbol,
        AttributeReferences attributeReferences)
    {
        var entries = new List<(INamedTypeSymbol?, uint)>();
        if (attributeReferences.GenericMessageAttributeType == null)
        {
            return entries;
        }

        foreach (var attribute in typeSymbol.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeReferences.GenericMessageAttributeType))
            {
                continue;
            }

            INamedTypeSymbol? construction = attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Kind == TypedConstantKind.Type
                && attribute.ConstructorArguments[0].Value is INamedTypeSymbol named
                    ? named
                    : null;

            uint classId = 0;
            bool classIdIsError = false;
            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key != "ClassId")
                {
                    continue;
                }

                // Error-typed arguments (e.g. undeclared constants) already produce a first-line error at the
                // attribute use site from the compiler (CS0103 etc.) — letting classId=0 flow through here used to
                // yield a misleading secondary "missing 'ClassId'" error that masked the real cause (metadata audit
                // FINDING 2, 2026-09-08). Skip this entry (no registration, no diagnostic).
                if (namedArgument.Value.Kind == TypedConstantKind.Error)
                {
                    classIdIsError = true;
                    continue;
                }

                if (namedArgument.Value.Kind == TypedConstantKind.Primitive
                    && namedArgument.Value.Value is uint parsed)
                {
                    classId = parsed;
                }
            }

            if (classIdIsError)
            {
                continue;
            }

            entries.Add((construction, classId));
        }

        return entries;
    }

    /// <summary>
    /// Finds duplicate construction declarations and (declaration, ClassId) collisions across the compilation.
    /// Left alone they crash at module load with a registration conflict, so they are promoted to compile-time diagnostics.
    /// </summary>
    internal    static ConstructionConflicts CollectConstructionConflicts(
        ImmutableArray<INamedTypeSymbol> types,
        AttributeReferences attributeReferences)
    {
        var constructionCounts = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
        var idCounts = new Dictionary<(INamedTypeSymbol Declaration, uint ClassId), int>(DeclarationClassIdComparer.Instance);
        var conflicts = new ConstructionConflicts();

        foreach (var type in types)
        {
            foreach (var (construction, classId) in ParseConstructionEntries(type, attributeReferences))
            {
                if (construction == null)
                {
                    continue;
                }

                constructionCounts[construction] = constructionCounts.TryGetValue(construction, out int c) ? c + 1 : 1;
                var idKey = (construction.OriginalDefinition, classId);
                idCounts[idKey] = idCounts.TryGetValue(idKey, out int n) ? n + 1 : 1;

                // Per-assembled-runtime-key (generic wire MessageId, ClassId) declaration owner — if two different
                // declarations share a key, `RegisterGenericReaderInvoker` collides inside the module initializer and
                // the assembly fails to load with TypeInitializationException. A (Declaration, ClassId) key treats
                // differing declarations as different keys, so it cannot catch this shape (audit ledger MEDIUM, 2026-09-06 pass).
                if (classId != 0 && classId <= TypeMetadata.MaxMessageAttributeValue
                    && TryGetRegisteredGenericWireMessageId(construction.OriginalDefinition, attributeReferences, out uint genericWireId))
                {
                    conflicts.AddRuntimeKeyOwner((genericWireId, classId), construction.OriginalDefinition);
                }
            }
        }

        // Wire MessageId owners of non-generic messages — two types assembling the same id fail assembly load at
        // module load with a `_registeredMessageIds` registration conflict, so this is caught at compile time (Known-Issues KI-31).
        foreach (var type in types)
        {
            if (!TryGetRegisteredWireMessageId(type, attributeReferences, out uint wireMessageId))
            {
                continue;
            }

            conflicts.AddMessageIdOwner(wireMessageId, type);
        }

        foreach (var pair in constructionCounts)
        {
            if (pair.Value > 1)
            {
                conflicts.AddDuplicateConstruction(pair.Key);
            }
        }
        foreach (var pair in idCounts)
        {
            if (pair.Value > 1)
            {
                conflicts.AddCollidedId(pair.Key);
            }
        }
        return conflicts;
    }

    internal    static bool ValidateConstructionEntries(
        INamedTypeSymbol host,
        List<(INamedTypeSymbol? Construction, uint ClassId)> entries,
        AttributeReferences attributeReferences,
        ConstructionConflicts conflicts,
        SourceProductionContext context,
        Location location)
    {
        var seenClassIds = new HashSet<uint>();
        var seenConstructions = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var (construction, classId) in entries)
        {
            if (construction == null)
            {
                ReportInvalidConstruction(context, location, host, "GenericMessage requires a closed generic construction type");
                return false;
            }

            if (classId == 0)
            {
                ReportInvalidConstruction(context, location, host, $"construction '{construction.ToDisplayString()}' is missing 'ClassId' (must be 1 .. {TypeMetadata.MaxMessageAttributeValue})");
                return false;
            }

            // ClassId lives in the same 24-bit wire slot as MessageId (the trailing 3 bytes of `GenericIdHeaderSize`).
            // Values above the cap get truncated on the wire, so runtime registration rejects them — and that
            // registration runs inside a **module initializer**, where ArgumentOutOfRangeException escalates into
            // TypeInitializationException (assembly load failure). Promote this to a compile-time diagnostic instead
            // of a runtime crash (Known-Issues KI-27).
            if (classId > TypeMetadata.MaxMessageAttributeValue)
            {
                ReportInvalidConstruction(context, location, host, $"ClassId {classId} is out of range for construction '{construction.ToDisplayString()}' (must be 1 .. {TypeMetadata.MaxMessageAttributeValue})");
                return false;
            }

            if (!seenClassIds.Add(classId))
            {
                ReportInvalidConstruction(context, location, host, $"ClassId {classId} is declared more than once");
                return false;
            }

            if (!seenConstructions.Add(construction))
            {
                ReportInvalidConstruction(context, location, host, $"construction '{construction.ToDisplayString()}' is declared more than once");
                return false;
            }

            if (construction.IsUnboundGenericType)
            {
                ReportInvalidConstruction(context, location, host, $"'{construction.ToDisplayString()}' is an unbound generic type; declare a closed construction like typeof({construction.Name}<...>)");
                return false;
            }

            var declaration = construction.OriginalDefinition;
            if (!construction.IsGenericType || !declaration.IsGenericType)
            {
                ReportInvalidConstruction(context, location, host, $"'{construction.ToDisplayString()}' is not a construction of a generic message declaration ('[Message]' required)");
                return false;
            }

            // Generic wire messages must be Standalone (NonId/Parent/Child declarations have no construction slot).
            var declarationMeta = new TypeMetadata(declaration, attributeReferences);
            if (!declarationMeta.IsStandaloneMessage)
            {
                ReportInvalidConstruction(context, location, host, $"'{construction.ToDisplayString()}' is not a construction of a generic message declaration ('[Message]' required)");
                return false;
            }

            // If the declaration will not be generated/registered for any reason (MSGPROT001 non-partial,
            // MSGPROT002 nested non-partial, MSGPROT005 ID range, MSGPROT010 not constructible,
            // MSGPROT013 category range, MSGPROT018 argument/kind mismatch), the construction cannot implement
            // IHasIdMessageSerializable<T> — emitting a carrier would produce uncompilable generated code with
            // CS0311 (KI-43). Unify on the single generic-gate judgment instead of re-duplicating the individual
            // conditions; the declaration site already reports the diagnostic naming the specific cause.
            // Apply this only to **source declarations in this compilation** — metadata-only declarations (PE from
            // another assembly, no DeclaringSyntaxReferences) cannot be judged partial at all, the original
            // compilation's gate already validated them, and cross-assembly duplicates follow ADR-0005's
            // runtime-detection contract. Without this condition, pure carriers referencing external declarations
            // would be false-positived (KI-43 second-pass unification regression).
            uint genericWireId = 0;
            bool declarationIsExternal = declaration.DeclaringSyntaxReferences.Length == 0;
            if (!declarationIsExternal && !TryGetRegisteredGenericWireMessageId(declaration, attributeReferences, out genericWireId))
            {
                ReportInvalidConstruction(context, location, host, $"'{construction.ToDisplayString()}' targets a [Message] declaration that will not be generated (MSGPROT001/002/005/010/013/018 on the declaration)");
                return false;
            }

            if (conflicts.IsConflicting(construction, classId))
            {
                ReportInvalidConstruction(context, location, host, $"construction '{construction.ToDisplayString()}' (or its ClassId) is declared more than once in this compilation");
                return false;
            }

            // Do different generic declarations assemble the same (MessageId, ClassId) runtime key? Duplicates of
            // the same declaration are caught by IsConflicting above. Left alone, RegisterGenericReaderInvoker
            // collides in the module initializer and the assembly fails to load (MSGPROT015). Reuse the gate
            // result (genericWireId) from the guard above to avoid a second call — external declarations were
            // skipped by the gate returning false (original-compilation gate + ADR-0005 runtime detection).
            if (!declarationIsExternal && conflicts.TryGetGenericRuntimeKeyPeers(genericWireId, classId, declaration, out string genericPeers))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.DuplicateGenericRuntimeKey,
                    location,
                    host.Name,
                    construction.ToDisplayString(),
                    genericWireId.ToString("X8"),
                    classId.ToString(),
                    genericPeers));
                return false;
            }
        }

        return true;
    }

    static void ReportInvalidConstruction(
        SourceProductionContext context,
        Location location,
        INamedTypeSymbol host,
        string reason)
    {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.InvalidGenericMessageDeclaration,
            location,
            host.Name,
            reason));
    }

    internal    static void EmitConstructionRegistration(
        INamedTypeSymbol host,
        List<(INamedTypeSymbol? Construction, uint ClassId)> entries,
        SourceProductionContext context,
        HashSet<string> usedCarrierSuffixes)
    {
        string suffix = SymbolNaming.MakeUniqueSuffix(host, usedCarrierSuffixes);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using MessageProtocol.Serialize;");
        sb.AppendLine();
        sb.AppendLine($"internal static class __GenericConstructionRegistration_{suffix}");
        sb.AppendLine("{");
        sb.AppendLine("    [ModuleInitializer]");
        sb.AppendLine("    internal static void Initialize()");
        sb.AppendLine("    {");
        foreach (var (construction, classId) in entries)
        {
            string constructionName = construction!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.AppendLine($"        MessageSerializer.RegisterGenericConstruction<{constructionName}>({classId});");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource(
            MessageCodeGenerator.SanitizeHintName($"__GenericConstructionRegistration_{suffix}"),
            SourceText.From(sb.ToString(), Encoding.UTF8));
    }
    }

    /// <summary>Duplicate construction-declaration state for the whole compilation.</summary>
    internal sealed class ConstructionConflicts
    {
        // State is private — only GenericConstruction.CollectConstructionConflicts mutates it through internal
        // mutators, and consumption (Generate's diagnostic reporting) goes through read accessors only. A public
        // mutable dictionary would let external code insert entries after the fact, bypassing the KI-31 invariant
        // "count only shapes that will actually be registered" (structural audit FINDING 4, 2026-09-08).
        readonly HashSet<INamedTypeSymbol> _duplicateConstructions = new(SymbolEqualityComparer.Default);
        readonly HashSet<(INamedTypeSymbol Declaration, uint ClassId)> _collidedIds = new(DeclarationClassIdComparer.Instance);

        /// <summary>Wire MessageId → types assembling that id (only shapes actually registered at module load).</summary>
        readonly Dictionary<uint, List<INamedTypeSymbol>> _messageIdOwners = new();

        /// <summary>Assembled generic runtime key (MessageId, ClassId) → generic declarations using that key (registered shapes only).</summary>
        readonly Dictionary<(uint MessageId, uint ClassId), List<INamedTypeSymbol>> _genericRuntimeKeyOwners = new();

        internal void AddDuplicateConstruction(INamedTypeSymbol construction) => _duplicateConstructions.Add(construction);
        internal void AddCollidedId((INamedTypeSymbol Declaration, uint ClassId) key) => _collidedIds.Add(key);

        internal void AddMessageIdOwner(uint messageId, INamedTypeSymbol type)
        {
            if (!_messageIdOwners.TryGetValue(messageId, out var owners))
            {
                owners = new List<INamedTypeSymbol>();
                _messageIdOwners[messageId] = owners;
            }
            owners.Add(type);
        }

        /// <summary>Ignores duplicate additions of the same declaration (the per-key owner list behaves like a set).</summary>
        internal void AddRuntimeKeyOwner((uint MessageId, uint ClassId) key, INamedTypeSymbol declaration)
        {
            if (!_genericRuntimeKeyOwners.TryGetValue(key, out var owners))
            {
                owners = new List<INamedTypeSymbol>();
                _genericRuntimeKeyOwners[key] = owners;
            }
            if (!owners.Any(owner => SymbolEqualityComparer.Default.Equals(owner, declaration)))
            {
                owners.Add(declaration);
            }
        }

        public bool IsConflicting(INamedTypeSymbol construction, uint classId)
        {
            return _duplicateConstructions.Contains(construction)
                || _collidedIds.Contains((construction.OriginalDefinition, classId));
        }

        /// <summary>Returns the names of other owners of the same wire MessageId, excluding self, if any (Known-Issues KI-31).</summary>
        public bool TryGetMessageIdPeers(uint messageId, INamedTypeSymbol self, out string peers)
        {
            peers = string.Empty;
            if (!_messageIdOwners.TryGetValue(messageId, out var owners) || owners.Count < 2)
            {
                return false;
            }

            peers = string.Join(
                ", ",
                owners
                    .Where(owner => !SymbolEqualityComparer.Default.Equals(owner, self))
                    .Select(owner => $"'{owner.ToDisplayString()}'"));
            return true;
        }

        /// <summary>Returns the names of other owners of the same runtime key (MessageId, ClassId), excluding self's declaration, if any (MSGPROT015).</summary>
        public bool TryGetGenericRuntimeKeyPeers(uint messageId, uint classId, INamedTypeSymbol selfDeclaration, out string peers)
        {
            peers = string.Empty;
            if (!_genericRuntimeKeyOwners.TryGetValue((messageId, classId), out var owners) || owners.Count < 2)
            {
                return false;
            }

            peers = string.Join(
                ", ",
                owners
                    .Where(owner => !SymbolEqualityComparer.Default.Equals(owner, selfDeclaration))
                    .Select(owner => $"'{owner.ToDisplayString()}'"));
            return peers.Length > 0;
        }
    }

    sealed class DeclarationClassIdComparer : IEqualityComparer<(INamedTypeSymbol Declaration, uint ClassId)>
    {
        public static readonly DeclarationClassIdComparer Instance = new();

        public bool Equals((INamedTypeSymbol Declaration, uint ClassId) x, (INamedTypeSymbol Declaration, uint ClassId) y)
        {
            return SymbolEqualityComparer.Default.Equals(x.Declaration, y.Declaration) && x.ClassId == y.ClassId;
        }

        public int GetHashCode((INamedTypeSymbol Declaration, uint ClassId) obj)
        {
            unchecked
            {
                return (SymbolEqualityComparer.Default.GetHashCode(obj.Declaration) * 397) ^ (int)obj.ClassId;
            }
        }
    }
}
