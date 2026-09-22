using MessageProtocol.CodeGenerator.Graph;
using MessageProtocol.CodeGenerator.Metadata;
using MessageProtocol.CodeGenerator.Reference;
using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Text;

namespace MessageProtocol.CodeGenerator.Generate
{
    /// <summary>Generated-code emitter for a single message type.</summary>
    internal static partial class MessageSerializeCodeEmitter
    {
        public static bool TryEmit(
            TypeMetadata typeMeta,
            AttributeReferences attributeReferences,
            bool hasCollectionsMarshal,
            IAssemblySymbol consumerAssembly,
            out string? code,
            out ImmutableArray<UnsupportedMemberInfo> unsupportedMembers)
        {
            var state = new EmitState(hasCollectionsMarshal);
            var serializationGraph = SerializationGraph.Create(typeMeta, attributeReferences);
            var sb = new StringBuilder();

            sb.Append(Header.Emit(typeMeta, out bool hasNamespace));
            sb.Append(Define.Emit(typeMeta, serializationGraph, attributeReferences, state, consumerAssembly));

            if (hasNamespace)
            {
                sb.Append(Header.EmitCloseNamespace());
            }

            if (state.UnsupportedMembers.Count > 0)
            {
                code = null;
                unsupportedMembers = state.UnsupportedMembers.ToImmutableArray();
                return false;
            }

            code = sb.ToString();
            unsupportedMembers = ImmutableArray<UnsupportedMemberInfo>.Empty;
            return true;
        }

        /// <summary>
        /// The `new` modifier for generated static members of derived message types — applied **only when the base
        /// actually emits the static contract**. Adding `new` when the base emits nothing raises CS0109 in the
        /// consumer build (warning accumulation; a build failure under `TreatWarningsAsErrors`). Bases that emit
        /// nothing: abstract group roots (inheritance-only, generation skipped), abstract and non-constructible
        /// types (MSGPROT010), non-partial types (MSGPROT001). The emitter's declaration half (`Define`) and method
        /// emission (`Method`) share this one implementation (Known-Issues KI-28).
        /// </summary>
        static string GetStaticHidingModifier(TypeMetadata typeMeta, bool isModuleInitializer = false, IAssemblySymbol? consumerAssembly = null)
        {
            var baseType = typeMeta.BaseTypeMetadata;
            if (baseType == null)
            {
                return string.Empty;
            }

            if (!baseType.IsNonIdMessage && !baseType.IsStandaloneMessage && !baseType.IsGroupMessage)
            {
                return string.Empty;
            }

            // Initialize() is internal — an Initialize on a base outside this assembly (metadata reference) is
            // inaccessible from this compilation by default, so there is nothing to hide in the first place. In
            // cross-assembly derivation (protocol DLL + separate server/client DLLs — a standard commercial layout),
            // keeping `new` guarantees a CS0109 per type. But if the base assembly opens internal access to the
            // consumer assembly via InternalsVisibleTo, Initialize becomes a hideable target again — removing `new`
            // then would put an unfixable CS0108 into generated code (the user cannot edit it), so the emission
            // judgment falls back to accessibility-opens-only.
            if (isModuleInitializer && !BaseIsInThisCompilation(baseType) && !BaseInternalsAreAccessible(baseType, consumerAssembly))
            {
                return string.Empty;
            }

            return BaseEmitsStaticContract(baseType) ? "new " : string.Empty;
        }

        /// <summary>Whether the base assembly opens internal access to the consumer (this compilation) assembly via InternalsVisibleTo.</summary>
        static bool BaseInternalsAreAccessible(TypeMetadata baseType, IAssemblySymbol? consumerAssembly)
        {
            return consumerAssembly != null && baseType.Symbol.ContainingAssembly.GivesAccessTo(consumerAssembly);
        }

        /// <summary>Whether the base is defined in this compilation's source (not a metadata reference).</summary>
        static bool BaseIsInThisCompilation(TypeMetadata baseType)
        {
            return baseType.Symbol.Locations.Any(static location => location.IsInSource);
        }

        /// <summary>Whether the base message type actually has generated static members.</summary>
        static bool BaseEmitsStaticContract(TypeMetadata baseType)
        {
            var symbol = baseType.Symbol;

            // Abstract message types never emit the static contract, regardless of source or metadata
            // (abstract group roots skip generation; other abstracts are rejected with MSGPROT010) —
            // abstractness is decidable from metadata alone, so this is safe to apply to cross-assembly bases too.
            // Source bases were already filtered here by KI-28; metadata bases are now aligned to the same rule.
            if (symbol.IsAbstract)
            {
                return false;
            }

            // A concrete base from another assembly (metadata) has no syntax references, so partialness is unknown —
            // assume it was generated in that compilation and keep `new` as before (guessing the other way inverts
            // the error into CS0108/CS0114).
            if (!BaseIsInThisCompilation(baseType))
            {
                return true;
            }

            return MessageCodeGenerator.IsPartial(symbol) && MessageCodeGenerator.IsConstructibleMessageType(symbol);
        }

        static string GetTypeDisplayName(ITypeSymbol typeSymbol)
        {
            return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
    }
}
