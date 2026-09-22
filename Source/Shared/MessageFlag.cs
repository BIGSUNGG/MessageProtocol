using System;

namespace MessageProtocol
{
#if MESSAGE_PROTOCOL_CODE_GENERATOR
    [Flags]
    internal enum MessageFlag : byte
#else
    /// <summary>Message kind flags written to the header's upper nibble.</summary>
    [Flags]
    public enum MessageFlag : byte
#endif
    {
        None = 0,
        /// <summary>Reserved header flag for generic standalone messages (value 0). Three construction type ID bytes follow the header.</summary>
        Generic = 0,
        NonIdMessage = 1 << 0,
        Standalone = 1 << 1,
        Parent = 1 << 2,
        Child = 1 << 3,
        /// <summary>Combination of the three ID-bearing kinds (Standalone/Parent/Child) — the opposite of NonIdMessage.</summary>
        IdMessage = Standalone | Parent | Child,
    }
}
