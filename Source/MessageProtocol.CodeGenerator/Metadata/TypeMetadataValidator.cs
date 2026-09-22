using MessageProtocol.CodeGenerator.Reference;
using Microsoft.CodeAnalysis;

namespace MessageProtocol.CodeGenerator.Metadata
{
    /// <summary>Attribute value validation (MSGPROT005 ID value, MSGPROT013 category value, MSGPROT018 argument/kind consistency).</summary>
    internal static class TypeMetadataValidator
    {
        /// <summary>Upper bound of the header's category nibble (<see cref="MessageWireFormat.NibbleMask"/>) — 4 bits on the wire.</summary>
        public const uint MaxCategoryValue = MessageWireFormat.NibbleMask;

        /// <summary>
        /// Checks that the [Message] constructor's `MessageCategory` argument is within the 4-bit nibble range (0..15)
        /// (MSGPROT013). Out of range, the emitter **silently masks with 0x0F** and the wire MessageId diverges from
        /// the developer's intent — on collision with another message's ID, the module initializer throws a
        /// registration-conflict exception (assembly load failure); without a collision, peers route on an unintended
        /// category (Known-Issues KI-8). The attribute is `Inherited = false`, so only the declaration itself is checked.
        /// </summary>
        public static bool TryValidateCategoryRange(
            INamedTypeSymbol typeSymbol,
            AttributeReferences references,
            out string categoryValue)
        {
            categoryValue = string.Empty;

            var attribute = typeSymbol.FindAttribute(references.MessageAttributeType);
            if (attribute == null)
            {
                return true;
            }

            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.Type?.TypeKind != TypeKind.Enum || argument.Type.Name == nameof(MessageKind))
                {
                    continue;
                }

                var rawValue = argument.Value;
                if (!TypeMetadata.TryConvertToUInt32(rawValue, out uint value) || value > MaxCategoryValue)
                {
                    categoryValue = rawValue?.ToString() ?? "null";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks [Message] constructor arguments against the declared kind (MSGPROT018):
        /// (1) the kind is not an undefined value (a cast value above 4), and
        /// (2) NonId takes no id/category arguments — the NonId header is 1 byte (no id slot) and its
        /// category nibble is fixed at 0.
        /// </summary>
        public static bool TryValidateMessageAttributeConsistency(
            INamedTypeSymbol typeSymbol,
            AttributeReferences references,
            out string detail)
        {
            detail = string.Empty;

            var attribute = typeSymbol.FindAttribute(references.MessageAttributeType);
            if (attribute == null)
            {
                return true;
            }

            if (!TypeMetadata.TryDecodeMessageAttribute(attribute, out MessageKind kind, out uint manualId, out byte category))
            {
                uint rawKind = 0;
                foreach (var argument in attribute.ConstructorArguments)
                {
                    if (argument.Type?.Name == nameof(MessageKind)
                        && TypeMetadata.TryConvertToUInt32(argument.Value, out uint value))
                    {
                        rawKind = value;
                    }
                }

                detail = $"'{rawKind}' is not a defined MessageKind value";
                return false;
            }

            if (kind == MessageKind.NonId && (manualId != 0 || category != 0))
            {
                detail = "MessageKind.NonId cannot take id or category arguments";
                return false;
            }

            return true;
        }

        public static bool TryValidateMessageIdRange(
            INamedTypeSymbol typeSymbol,
            AttributeReferences references,
            out string attributeName,
            out string attributeValue)
        {
            attributeName = string.Empty;
            attributeValue = string.Empty;

            var attribute = typeSymbol.FindAttribute(references.MessageAttributeType);
            if (attribute == null)
            {
                return true;
            }

            foreach (var argument in attribute.ConstructorArguments)
            {
                // enum arguments (kind, category) are each owned by MSGPROT018 and MSGPROT013;
                // parameterless or id-omitting (hash-id) declarations have no integer argument to check.
                if (argument.Type?.TypeKind == TypeKind.Enum)
                {
                    continue;
                }

                var rawValue = argument.Value;
                if (!TypeMetadata.TryConvertToUInt32(rawValue, out uint value) || value > TypeMetadata.MaxMessageAttributeValue)
                {
                    attributeName = attribute.AttributeClass?.Name ?? "MessageAttribute";
                    attributeValue = rawValue?.ToString() ?? "null";
                    return false;
                }
            }

            return true;
        }
    }
}
