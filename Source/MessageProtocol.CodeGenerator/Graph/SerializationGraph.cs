using MessageProtocol.CodeGenerator.Metadata;
using MessageProtocol.CodeGenerator.Reference;
using Microsoft.CodeAnalysis;

namespace MessageProtocol.CodeGenerator.Graph
{
    /// <summary>
    /// Collects every serializable type reachable from the root message by following members.
    /// class/struct types defined in source become nested-payload targets even without a message attribute.
    /// </summary>
    internal sealed class SerializationGraph
    {
        readonly AttributeReferences _references;
        readonly Dictionary<ITypeSymbol, SerializableTypeModel> _lookup;
        readonly HashSet<string> _usedHelperSuffixes;
        readonly SerializableTypeModel[] _reachableTypes;

        SerializationGraph(
            SerializableTypeModel rootType,
            AttributeReferences references,
            Dictionary<ITypeSymbol, SerializableTypeModel> lookup,
            HashSet<string> usedHelperSuffixes,
            SerializableTypeModel[] reachableTypes)
        {
            RootType = rootType;
            _references = references;
            _lookup = lookup;
            _usedHelperSuffixes = usedHelperSuffixes;
            _reachableTypes = reachableTypes;
        }

        public SerializableTypeModel RootType { get; }

        /// <summary>
        /// Returns reachable types in **ascending type-name order**. Previously the helper-method emission order rode
        /// on a BCL implementation detail via `Dictionary.Values` enumeration (.NET Dictionary enumerates in insertion
        /// order when nothing was removed, but that is not a documented contract — audit ledger LOW, 2026-09-08).
        /// Sorting stabilizes only the generated **text layout** — wire bytes are owned by member order
        /// (`TypeMetadata.GetWireMembers`) and remain unchanged. Helper suffixes are already fixed at graph-collection
        /// time and are independent of emission order.
        /// </summary>
        public IReadOnlyCollection<SerializableTypeModel> ReachableTypes => _reachableTypes;

        public static SerializationGraph Create(TypeMetadata rootType, AttributeReferences references)
        {
            var usedHelperSuffixes = new HashSet<string>();
            var rootModel = new SerializableTypeModel(rootType, SymbolNaming.MakeUniqueSuffix(rootType.Symbol, usedHelperSuffixes));
            var lookup = new Dictionary<ITypeSymbol, SerializableTypeModel>(SymbolEqualityComparer.Default)
            {
                [rootType.Symbol] = rootModel,
            };
            // Collect is an instance method, so collect into a temporary graph, then build the final graph with the sorted array.
            var collector = new SerializationGraph(rootModel, references, lookup, usedHelperSuffixes, Array.Empty<SerializableTypeModel>());
            collector.Collect(rootType);
            // Emission-order stabilization: sort by type name (KI-4 family — removes BCL Dictionary enumeration-order dependence).
            var sorted = lookup.Values
                .OrderBy(static model => model.TypeName, StringComparer.Ordinal)
                .ToArray();
            return new SerializationGraph(rootModel, references, lookup, usedHelperSuffixes, sorted);
        }

        public bool IsMessageType(ITypeSymbol typeSymbol)
        {
            if (typeSymbol is not INamedTypeSymbol namedType)
            {
                return false;
            }

            return namedType.ContainAttribute(_references.MessageAttributeType);
        }

        public bool TryGetSerializableObjectType(ITypeSymbol typeSymbol, out SerializableTypeModel typeModel)
        {
            return _lookup.TryGetValue(typeSymbol, out typeModel);
        }

        /// <summary>Returns the element type of an array or List&lt;T&gt;/IList&lt;T&gt;.</summary>
        public static bool TryGetCollectionElementType(ITypeSymbol typeSymbol, out ITypeSymbol elementType)
        {
            if (typeSymbol is IArrayTypeSymbol arrayType)
            {
                elementType = arrayType.ElementType;
                return true;
            }

            if (typeSymbol is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                string genericTypeName = namedType.ConstructedFrom.ToDisplayString();
                if (genericTypeName.StartsWith("System.Collections.Generic.List<") ||
                    genericTypeName.StartsWith("System.Collections.Generic.IList<"))
                {
                    elementType = namedType.TypeArguments[0];
                    return true;
                }
            }

            elementType = null!;
            return false;
        }

        void Collect(TypeMetadata typeMeta)
        {
            foreach (var member in TypeMetadata.GetWireMembers(typeMeta))
            {
                Collect(member.Type);
            }
        }

        void Collect(ITypeSymbol typeSymbol)
        {
            if (TryGetCollectionElementType(typeSymbol, out var elementType))
            {
                Collect(elementType);
                return;
            }

            if (IsPrimitiveLike(typeSymbol))
            {
                return;
            }

            if (typeSymbol is not INamedTypeSymbol namedType || !IsSerializableObjectType(namedType))
            {
                return;
            }

            if (_lookup.ContainsKey(namedType))
            {
                return;
            }

            var typeModel = new SerializableTypeModel(new TypeMetadata(namedType, _references), SymbolNaming.MakeUniqueSuffix(namedType, _usedHelperSuffixes));
            _lookup[namedType] = typeModel;

            Collect(typeModel.Metadata);
        }

        static bool IsPrimitiveLike(ITypeSymbol typeSymbol)
        {
            if (typeSymbol.TypeKind == TypeKind.Enum)
            {
                return true;
            }

            switch (typeSymbol.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                case SpecialType.System_Char:
                case SpecialType.System_String:
                    return true;
                default:
                    return false;
            }
        }

        static bool IsSerializableObjectType(INamedTypeSymbol namedType)
        {
            if (namedType.TypeKind != TypeKind.Class &&
                namedType.TypeKind != TypeKind.Struct)
            {
                return false;
            }

            if (!namedType.Locations.Any(location => location.IsInSource))
            {
                return false;
            }

            if (namedType.IsAnonymousType)
            {
                return false;
            }

            // Deserialization creates instances via the default constructor — reference types without an accessible
            // parameterless constructor (abstract classes, positional records, etc.) are filtered out by per-member diagnostics.
            if (namedType.TypeKind == TypeKind.Class && !HasAccessibleParameterlessConstructor(namedType))
            {
                return false;
            }

            return true;
        }

        /// <summary>Generated code is placed inside the root message's partial class, so the constructor must be at least internal.</summary>
        static bool HasAccessibleParameterlessConstructor(INamedTypeSymbol namedType)
        {
            if (namedType.IsAbstract)
            {
                return false;
            }

            foreach (var constructor in namedType.InstanceConstructors)
            {
                if (constructor.Parameters.Length != 0)
                {
                    continue;
                }

                return constructor.DeclaredAccessibility == Accessibility.Public ||
                       constructor.DeclaredAccessibility == Accessibility.Internal;
            }

            return false;
        }
    }
}
