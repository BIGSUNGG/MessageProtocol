using System;

namespace MessageProtocol
{
    static class MessageAttributeRange
    {
        public const uint MaxValue = MessageWireFormat.MessageIdValueMask;

        public static void Validate(uint value, string parameterName)
        {
            if (value > MaxValue)
            {
                throw new InvalidOperationException($"{parameterName} must be between 0 and {MaxValue} (2^24 - 1).");
            }
        }
    }

    /// <summary>
    /// Message declaration attribute — the single entry point for kind, ID, and category.
    /// <para>
    /// <c>Kind</c> fixes the kind to the given <see cref="MessageKind"/> value; <see cref="MessageKind.Automatic"/>
    /// infers it from the hierarchy. Omitting <c>Id</c> (0) derives the MessageId from the FNV-1a hash (24-bit,
    /// <see cref="MessageIdHash"/>) of the type's FullName; specifying it is a manual assignment (but since 0 means "omitted",
    /// a manual 0 is not possible). Hash collisions and a hash of 0 in Child position are rejected with diagnostic errors.
    /// Using <see cref="MessageKind.NonId"/> together with id/category arguments is a diagnostic error (MSGPROT018).
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
    public class MessageAttribute : Attribute
    {
        /// <summary>Message kind. Defaults to Automatic (inferred from the hierarchy).</summary>
        public MessageKind Kind { get; }

        /// <summary>Manual MessageId. When 0 (omitted), it is derived from the FullName hash.</summary>
        public uint Id { get; }

        /// <summary>Lower nibble of the header (0–15). Defaults to Category0. Not usable with NonId.</summary>
        public MessageCategory Category { get; }

        public MessageAttribute(
            MessageKind kind = MessageKind.Automatic,
            uint id = 0,
            MessageCategory category = MessageCategory.Category0)
        {
            MessageAttributeRange.Validate(id, nameof(id));
            Kind = kind;
            Id = id;
            Category = category;
        }
    }

    /// <summary>
    /// Declares serialization-supported constructions (closed generics) of a generic message. Attach repeatedly, one per
    /// construction, on any type declaration such as the declaration itself or a carrier:
    /// <c>[GenericMessage(typeof(Envelope&lt;Ping&gt;), ClassId = 1)]</c>.
    /// Generated code auto-registers declared constructions under their (MessageId, ClassId) key at module load, making
    /// object dispatch work on both the sending and receiving sides. Serializing an undeclared construction throws.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
    public class GenericMessageAttribute : Attribute
    {
        public Type Construction { get; }

        uint _classId;

        /// <summary>Construction class identifier. Written as 3 bytes after the header MessageId. Range 1 .. 2^24-1.</summary>
        public uint ClassId
        {
            get => _classId;
            set
            {
                if (value == 0)
                {
                    throw new InvalidOperationException("ClassId cannot be 0");
                }
                MessageAttributeRange.Validate(value, nameof(value));
                _classId = value;
            }
        }

        public GenericMessageAttribute(Type construction)
        {
            Construction = construction ?? throw new ArgumentNullException(nameof(construction));
        }
    }
}
