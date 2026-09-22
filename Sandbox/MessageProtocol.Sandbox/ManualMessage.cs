using MessageProtocol;
using MessageProtocol.Serialize;

namespace SandboxMessages;

/// <summary>
/// A manually implemented message: the contract interface is implemented directly, without the source generator.
/// The implementation writes the 4-byte header itself; no [Message] attributes are applied.
/// </summary>
public class ManualMessage : IHasIdMessageSerializable<ManualMessage>
{
    public int Value { get; set; }

    public static uint MessageId => MessageWireFormat.ComposeMessageId(
        MessageFlag.Standalone, (byte)MessageCategory.Category0, 20);

    public static void Serialize(ManualMessage message, ref MessageBufferWriter writer)
    {
        // Write the header directly in wire order: header byte, then the 3 ID bytes.
        uint id = MessageId;
        writer.WriteByte((byte)(id >> 24));
        writer.WriteByte((byte)(id >> 16));
        writer.WriteByte((byte)(id >> 8));
        writer.WriteByte((byte)id);
        writer.WriteInt32(message.Value);
    }

    public static byte[] Serialize(ManualMessage message)
    {
        var writer = MessageBufferWriter.Create();
        try
        {
            Serialize(message, ref writer);
            return writer.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    public static ManualMessage Deserialize(ref MessageBufferReader reader)
    {
        reader.Skip(MessageWireFormat.IdHeaderSize); // the 4 header bytes were already consumed by routing
        return new ManualMessage { Value = reader.ReadInt32() };
    }

    public static ManualMessage Deserialize(byte[] data)
    {
        var reader = new MessageBufferReader(data);
        return Deserialize(ref reader);
    }
}
