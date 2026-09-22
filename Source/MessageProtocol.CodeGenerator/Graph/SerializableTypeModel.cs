using MessageProtocol.CodeGenerator.Metadata;
using Microsoft.CodeAnalysis;

namespace MessageProtocol.CodeGenerator.Graph
{
    /// <summary>A single type in the serialization graph plus its helper-method name scheme.</summary>
    internal sealed class SerializableTypeModel
    {
        public SerializableTypeModel(TypeMetadata metadata, string helperSuffix)
        {
            Metadata = metadata;
            HelperSuffix = helperSuffix;
        }

        public TypeMetadata Metadata { get; }
        public string HelperSuffix { get; }
        public bool IsReferenceType => Metadata.Symbol.IsReferenceType;
        public string TypeName => Metadata.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        /// <summary>Helper that writes the member bodies.</summary>
        public string WritePayloadMethodName => $"__WritePayload_{HelperSuffix}";
        /// <summary>Helper that populates members into an existing instance.</summary>
        public string PopulatePayloadMethodName => $"__PopulatePayload_{HelperSuffix}";
        /// <summary>Helper that reads a value-type body and returns the new instance.</summary>
        public string ReadPayloadMethodName => $"__ReadPayload_{HelperSuffix}";
        /// <summary>Helper that creates an empty instance before reference-type deserialization.</summary>
        public string CreateInstanceMethodName => $"__CreateInstance_{HelperSuffix}";
    }
}
