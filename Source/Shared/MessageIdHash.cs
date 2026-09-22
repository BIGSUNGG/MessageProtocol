using System.Text;

namespace MessageProtocol
{
#if MESSAGE_PROTOCOL_CODE_GENERATOR
    internal static class MessageIdHash
#else
    /// <summary>
    /// <c>[Message]</c> automatic MessageId hash — masks the FNV-1a 32-bit hash of the type's FullName to the 24-bit wire range (<c>0x00FF_FFFF</c>).
    /// Both the algorithm and the FullName string format are frozen for wire compatibility (changing them would invalidate every existing message ID).
    /// </summary>
    public static class MessageIdHash
#endif
    {
        /// <summary>FNV-1a 32-bit offset basis.</summary>
        public const uint OffsetBasis = 2166136261;
        /// <summary>FNV-1a 32-bit prime.</summary>
        public const uint Prime = 16777619;

        /// <summary>
        /// Returns the FNV-1a 32-bit hash of the FullName (namespace dots + nested <c>+</c> + generic arity <c>`n</c>, per the
        /// BCL <see cref="System.Type.FullName"/> convention), masked to 24 bits.
        /// </summary>
        public static uint FromFullName(string fullName)
        {
            var hash = OffsetBasis;
            foreach (var b in Encoding.UTF8.GetBytes(fullName))
            {
                hash ^= b;
                hash *= Prime;
            }
            return hash & MessageWireFormat.MessageIdValueMask;
        }
    }
}
