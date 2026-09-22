using System;

namespace MessageProtocol
{
#if MESSAGE_PROTOCOL_CODE_GENERATOR
    internal static class MessageWireFormat
#else
    /// <summary>Header and MessageId composition rules. Single source shared by the runtime and the code generator.</summary>
    public static class MessageWireFormat
#endif
    {
        public const int NonIdHeaderSize = 1;
        public const int IdHeaderSize = 4;
        /// <summary>Generic message header: 1 header byte + 3 MessageId bytes + 3 construction type ID bytes.</summary>
        public const int GenericIdHeaderSize = 7;
        public const int NullSizedPayloadLength = -1;
        public const int DefaultStreamCapacity = 256;

        public const byte NibbleMask = 0x0F;
        public const uint MessageIdValueMask = 0x00FF_FFFF;

        /// <summary>Composes the first header byte from flags (upper nibble) + category (lower nibble).</summary>
        public static byte ComposeHeaderByte(MessageFlag flags, byte category)
        {
            return (byte)((((byte)flags) & NibbleMask) << 4 | (category & NibbleMask));
        }

        /// <summary>Composes a MessageId from a header byte + 24-bit ID value.</summary>
        public static uint ComposeMessageId(MessageFlag flags, byte category, uint messageIdValue)
        {
            return ((uint)ComposeHeaderByte(flags, category) << 24) | (messageIdValue & MessageIdValueMask);
        }

        public static MessageFlag GetFlags(byte headerByte)
        {
            return (MessageFlag)((headerByte >> 4) & NibbleMask);
        }

        public static byte GetCategory(byte headerByte)
        {
            return (byte)(headerByte & NibbleMask);
        }

        /// <summary>Whether a 3-byte ID value follows the header.</summary>
        public static bool HasEmbeddedMessageId(byte headerByte)
        {
            return (GetFlags(headerByte) & MessageFlag.NonIdMessage) == 0;
        }

        /// <summary>Whether this is a generic message header (flag nibble 0 is reserved). Three construction type ID bytes follow the header.</summary>
        public static bool IsGenericMessage(byte headerByte)
        {
            return GetFlags(headerByte) == MessageFlag.Generic;
        }
    }
}
