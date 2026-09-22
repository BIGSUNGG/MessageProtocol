namespace MessageProtocol
{
#if MESSAGE_PROTOCOL_CODE_GENERATOR
    internal enum MessageKind
#else
    /// <summary>Message kind specified via the [Message] constructor.</summary>
    public enum MessageKind
#endif
    {
        /// <summary>
        /// Inferred automatically from the hierarchy: Child if an ancestor is a message, Parent if there is no ancestor but
        /// a [Message]-derived type exists in the same compilation, Standalone otherwise.
        /// </summary>
        Automatic = 0,

        /// <summary>Standalone ID message. 4-byte header.</summary>
        Standalone = 1,

        /// <summary>Parent message. Top of an inheritance hierarchy.</summary>
        Parent = 2,

        /// <summary>Child message. Requires a parent in the inheritance hierarchy; a manual id cannot be 0.</summary>
        Child = 3,

        /// <summary>ID-less message. 1-byte header. Not a target of object Deserialize; id and category arguments cannot be used.</summary>
        NonId = 4,
    }
}
