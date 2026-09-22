using System;

namespace MessageProtocol.Serialize
{
    /// <summary>
    /// Marker contract for message serialization. Implementing types expose the following public static members
    /// (filled in by the generator, or written by a manual implementation):
    /// <list type="bullet">
    ///   <item><c>static void Serialize(T, ref MessageBufferWriter)</c></item>
    ///   <item><c>static byte[] Serialize(T)</c></item>
    ///   <item><c>static T Deserialize(ref MessageBufferReader)</c></item>
    ///   <item><c>static T Deserialize(byte[])</c></item>
    /// </list>
    /// Avoids static abstract members, so it works on netstandard2.1 as well.
    /// </summary>
    public interface IMessageSerializable<T>
    {
    }

    /// <summary>
    /// Contract for messages carrying a protocol identifier (MessageId).
    /// Implementing types must additionally expose <c>public static uint MessageId { get; }</c>.
    /// </summary>
    public interface IHasIdMessageSerializable<T> : IMessageSerializable<T>
    {
    }
}
