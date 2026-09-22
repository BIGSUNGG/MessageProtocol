using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace MessageProtocol.CodeGenerator.Reference
{
    /// <summary>Cache bundling the symbols frequently looked up during a compilation.</summary>
    internal class AttributeReferences
    {
        public INamedTypeSymbol? MessageAttributeType { get; }
        public INamedTypeSymbol? MessageKindType { get; }
        public INamedTypeSymbol? MessageIgnoreAttributeType { get; }
        public INamedTypeSymbol? MessageIncludeAttributeType { get; }
        public INamedTypeSymbol? MessageSerializableInterfaceType { get; }
        public INamedTypeSymbol? HasIdMessageSerializableInterfaceType { get; }
        public INamedTypeSymbol? GenericMessageAttributeType { get; }

        /// <summary>
        /// The set of bases inherited by [Message] types declared in this compilation — the grounds for
        /// auto-promoting [Message] bases to GroupRoot. Bases from referenced assemblies are excluded: their wire
        /// identity (flags) is already fixed in the declaring assembly, and reinterpreting them as a different kind in
        /// the consuming compilation would create cross-assembly flag mismatches.
        /// </summary>
        public ImmutableHashSet<INamedTypeSymbol> MessageDescendantBases { get; }

        /// <summary>Whether a [Message] descendant exists in this compilation, promoting <paramref name="typeSymbol"/> to GroupRoot.</summary>
        public bool HasMessageDescendant(INamedTypeSymbol typeSymbol) => MessageDescendantBases.Contains(typeSymbol);

        public AttributeReferences(Compilation compilation, ImmutableHashSet<INamedTypeSymbol>? messageDescendantBases = null)
        {
            MessageDescendantBases = messageDescendantBases
                ?? ImmutableHashSet.Create<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            MessageAttributeType = compilation.GetTypeByMetadataName(MetadataNames.MessageAttribute);
            MessageKindType = compilation.GetTypeByMetadataName(MetadataNames.MessageKind);
            MessageIgnoreAttributeType = compilation.GetTypeByMetadataName(MetadataNames.MessageIgnoreAttribute);
            MessageIncludeAttributeType = compilation.GetTypeByMetadataName(MetadataNames.MessageIncludeAttribute);
            MessageSerializableInterfaceType = compilation.GetTypeByMetadataName(MetadataNames.MessageSerializableInterface);
            HasIdMessageSerializableInterfaceType = compilation.GetTypeByMetadataName(MetadataNames.HasIdMessageSerializableInterface);
            GenericMessageAttributeType = compilation.GetTypeByMetadataName(MetadataNames.GenericMessageAttribute);
        }
    }
}
