using MessageProtocol;
using MessageProtocol.NetStandardFixtures;

namespace MessageProtocol.Tests.Fixtures;

// ---------- Round trips ----------

public enum Level : short { Low = -1, Mid = 0, High = 1 }

[Message(MessageKind.Standalone, 100)]
public partial class FlatMessage
{
    public int Value { get; set; }
}

[Message(MessageKind.Standalone, 101, MessageCategory.Category5)]
public partial class AllTypesMessage
{
    public bool Bool { get; set; }
    public byte Byte { get; set; }
    public sbyte SByte { get; set; }
    public short Int16 { get; set; }
    public ushort UInt16 { get; set; }
    public int Int32 { get; set; }
    public uint UInt32 { get; set; }
    public long Int64 { get; set; }
    public ulong UInt64 { get; set; }
    public float Single { get; set; }
    public double Double { get; set; }
    public decimal Decimal { get; set; }
    public char Char { get; set; }
    public string? Text { get; set; }
    public Level Level { get; set; }
    public byte[]? Blob { get; set; }
    public List<double>? Samples { get; set; }
    public string[]? Tags { get; set; }
    public IList<byte>? Codes { get; set; }
    public FlatMessage? Nested { get; set; }
}

[Message(MessageKind.Standalone, 102)]
public partial struct PointMessage
{
    public int X { get; set; }
    public int Y { get; set; }
}

[Message(MessageKind.NonId)]
public partial class NoIdMessage
{
    public byte Flag { get; set; }
    public string? Note { get; set; }
}

[Message(MessageKind.Standalone, 103)]
public partial record SettingsRecord
{
    public string? Theme { get; set; }
    public int Volume { get; set; }
}

[Message(MessageKind.Standalone, 104)]
public partial class GraphMessage
{
    public string? Label { get; set; }
    public GraphMessage? Next { get; set; }
    public GraphMessage? Other { get; set; }
    public PlainPoco? Poco { get; set; }
}

public class PlainPoco
{
    public int Number { get; set; }
    public string? Name { get; set; }
}

[Message(MessageKind.Standalone, 105)]
public partial class MemberControlMessage
{
    public int Kept { get; set; }

    [MessageIgnore]
    public int Excluded { get; set; }

    [MessageInclude]
    int _internal;

    public void SetInternal(int value) => _internal = value;
    public int GetInternal() => _internal;
}

// ---------- Inheritance / groups ----------

[Message(MessageKind.Parent, 110)]
public partial class EventBase
{
    public long Timestamp { get; set; }
}

[Message(MessageKind.Child, 111)]
public partial class LoginEvent : EventBase
{
    public string? User { get; set; }
}

[Message(MessageKind.Child, 112)]
public partial class LogoutEvent : EventBase
{
    public int Reason { get; set; }
}

// ---------- Manual implementations ----------

public class ManualStandalone : MessageProtocol.Serialize.IHasIdMessageSerializable<ManualStandalone>
{
    public int Value { get; set; }

    public static uint MessageId => MessageProtocol.MessageWireFormat.ComposeMessageId(
        MessageProtocol.MessageFlag.Standalone, (byte)MessageProtocol.MessageCategory.Category0, 130);

    public static void Serialize(ManualStandalone message, ref MessageProtocol.Serialize.MessageBufferWriter writer)
    {
        uint id = MessageId;
        writer.WriteByte((byte)(id >> 24));
        writer.WriteByte((byte)(id >> 16));
        writer.WriteByte((byte)(id >> 8));
        writer.WriteByte((byte)id);
        writer.WriteInt32(message.Value);
    }

    public static byte[] Serialize(ManualStandalone message)
    {
        var writer = MessageProtocol.Serialize.MessageBufferWriter.Create();
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

    public static ManualStandalone Deserialize(ref MessageProtocol.Serialize.MessageBufferReader reader)
    {
        reader.Skip(MessageProtocol.MessageWireFormat.IdHeaderSize);
        return new ManualStandalone { Value = reader.ReadInt32() };
    }

    public static ManualStandalone Deserialize(byte[] data)
    {
        var reader = new MessageProtocol.Serialize.MessageBufferReader(data);
        return Deserialize(ref reader);
    }
}

// Type without a contract implementation (for registration-failure verification)
public class NotAMessage
{
    public int Value { get; set; }
}

// ---------- Generics ----------

[Message(MessageKind.Standalone, 120)]
[GenericMessage(typeof(GenericEnvelope<FlatMessage>), ClassId = 1)]
[GenericMessage(typeof(GenericEnvelope<SettingsRecord>), ClassId = 2)]
public partial class GenericEnvelope<T>
{
    public T? Value { get; set; }
    public string? Note { get; set; }
    public List<T?>? Items { get; set; }
}

[Message(MessageKind.Standalone, 121)]
[GenericMessage(typeof(GenericDuo<FlatMessage, SettingsRecord>), ClassId = 1)]
public partial class GenericDuo<TFirst, TSecond>
{
    public TFirst? First { get; set; }
    public TSecond? Second { get; set; }
}

[Message(MessageKind.NonId)]
public partial class GenericPair<T>
{
    public T? First { get; set; }
    public int Tag { get; set; }
}

// Two constructions of the same generic payload coexisting in one graph — helper name collision regression fixture
[Message(MessageKind.Standalone, 123)]
public partial class DuplicateGenericPayloadsMessage
{
    public GenericPair<int>? IntPair { get; set; }
    public GenericPair<string>? TextPair { get; set; }
}

// Generic message without a construction declaration — for verifying the exception on serialization (construction declarations are required)
[Message(MessageKind.Standalone, 122)]
public partial class UnregisteredGeneric<T>
{
    public int X { get; set; }
}

// ---------- Distributed declarations ----------

// Declares an additional construction of GenericEnvelope via a separate carrier type, without touching the declaration.
[GenericMessage(typeof(GenericEnvelope<PointMessage>), ClassId = 3)]
static class GenericEnvelopeExtraConstructions { }

// ---------- Nesting depth guard (KI-14) ----------

// Self-referencing chain — the minimal form of a hostile payload that packs deep nesting into a small frame to exhaust the recursion stack.
// Wire: 4-byte header + 1 byte ReferenceKind.NewObject per level + 1 terminating Null byte.
[Message(MessageKind.Standalone, 124)]
public partial class ChainMessage
{
    public ChainMessage? Next { get; set; }
}

// Fixture that packs many nested objects by *count* rather than depth — verifies the depth counter decrements in matching pairs (Leave).
[Message(MessageKind.Standalone, 125)]
public partial class WideChainMessage
{
    public List<ChainMessage>? Items { get; set; }
}

// ---------- Abstract group root polymorphic member (KI-24) ----------

// abstract [Message(MessageKind.Parent)] is the natural declaration for a polymorphic group, but the generator cannot instantiate it
// and emits no static Serialize/Deserialize — when used as a member type, the *concrete* element must be recorded with its header
// via runtime message dispatch (static delegation broke consumer builds with CS0117).
[Message(MessageKind.Parent, 126)]
public abstract partial class AbstractCommand
{
    public long Seq { get; set; }
}

[Message(MessageKind.Child, 127)]
public partial class StartCommand : AbstractCommand
{
    public string? Target { get; set; }
}

[Message(MessageKind.Child, 128)]
public partial class StopCommand : AbstractCommand
{
    public int Code { get; set; }
}

[Message(MessageKind.Standalone, 129)]
public partial class CommandEnvelope
{
    public AbstractCommand? Command { get; set; }
    public List<AbstractCommand>? History { get; set; }
}

// Circular graph fixture through a runtime-dispatch member (abstract message type) — the dispatch path does not track
// back-references, so a cycle leading back through this member would make write recursion infinitely deep (KI-25).
[Message(MessageKind.Child, 130)]
public partial class WrapCommand : AbstractCommand
{
    public CommandEnvelope? Inner { get; set; }
}

// ---------- Registration cache robustness (KI-11) ----------

// Type implementing only the contract marker, with no static contract members — the reflection path finds nothing.
// Fixture for verifying that early access before registration does not permanently break the cache (both tests only read, so order is irrelevant).
public class UnregisteredContractMessage : MessageProtocol.Serialize.IMessageSerializable<UnregisteredContractMessage>
{
    public int Value { get; set; }
}

// Same shape, but verifies recovery in the early access → delegate registration order inside a test.
public class LateBoundMessage : MessageProtocol.Serialize.IMessageSerializable<LateBoundMessage>
{
    public int Value { get; set; }
}

// ---------- Collection write snapshot (KI-26) ----------

// Fixture that counts collection property getter invocations — executes whether the generated code re-evaluates the member
// for the length prefix, loop condition, and each element access (getter 2N+2 times) or snapshots it once.
[Message(MessageKind.Standalone, 131)]
public partial class SnapshotCollectionMessage
{
    List<int> _codes = new() { 1, 2, 3 };
    string[] _tags = { "a", "b" };

    [MessageIgnore]
    public int CodesGetterCalls { get; set; }

    [MessageIgnore]
    public int TagsGetterCalls { get; set; }

    // Declared as IList<T> — misses the CollectionsMarshal fast path, so the indexer-loop variant is used.
    public IList<int> Codes
    {
        get { CodesGetterCalls++; return _codes; }
        set => _codes = (List<int>)value;
    }

    public string[] Tags
    {
        get { TagsGetterCalls++; return _tags; }
        set => _tags = value;
    }
}

// ---------- Derived field loss on concrete base members (KI-29) ----------

// Fixture that intentionally triggers MSGPROT012 — pins the current behavior (declared-type serialization → derived member loss)
// by execution. When polymorphism is needed, declare the root abstract and rely on runtime dispatch (KI-24);
// when intentionally sending only the base field like this fixture, silence the warning with #pragma (also verifies the suppression means).
#pragma warning disable MSGPROT012
[Message(MessageKind.Standalone, 132)]
public partial class EventHost
{
    public EventBase? Event { get; set; }
}
#pragma warning restore MSGPROT012

// ---------- Shared references through dispatch members (KI-9 resolution) ----------

// Fixture sharing a concrete out-of-graph (other-assembly) message across two members — a NetStandardFixtures message type,
// so it takes the Tests assembly's out-of-graph delegation path (EmitOutOfGraphMessage*).
[Message(MessageKind.Standalone, 133)]
public partial class SharedOutOfGraphHost
{
    public FallbackCollections? First { get; set; }
    public FallbackCollections? Second { get; set; }
}

// ---------- Registration validation order (KI-11 residue) ----------

// Manual message for verifying recovery via re-registration after a rejection — no test registers or touches it first
// (the first access must be the "rejected registration" so cache corruption is observable).
public class ManualIdMessage : MessageProtocol.Serialize.IHasIdMessageSerializable<ManualIdMessage>
{
    public int Value { get; set; }

    public static uint MessageId => MessageProtocol.MessageWireFormat.ComposeMessageId(
        MessageProtocol.MessageFlag.Standalone, (byte)MessageProtocol.MessageCategory.Category0, 140);

    public static void Serialize(ManualIdMessage message, ref MessageProtocol.Serialize.MessageBufferWriter writer)
    {
        uint id = MessageId;
        writer.WriteByte((byte)(id >> 24));
        writer.WriteByte((byte)(id >> 16));
        writer.WriteByte((byte)(id >> 8));
        writer.WriteByte((byte)(id));
        writer.WriteInt32(message.Value);
    }

    public static byte[] Serialize(ManualIdMessage message)
    {
        var writer = MessageProtocol.Serialize.MessageBufferWriter.Create();
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

    public static ManualIdMessage Deserialize(ref MessageProtocol.Serialize.MessageBufferReader reader)
    {
        reader.Skip(MessageProtocol.MessageWireFormat.IdHeaderSize);
        return new ManualIdMessage { Value = reader.ReadInt32() };
    }

    public static ManualIdMessage Deserialize(byte[] data)
    {
        var reader = new MessageProtocol.Serialize.MessageBufferReader(data);
        return Deserialize(ref reader);
    }
}

// Rejection fixture attempting a HasId registration with a NonId-flagged id — no successful registration, so test order is irrelevant.
public class ManualFlagProbeMessage : MessageProtocol.Serialize.IHasIdMessageSerializable<ManualFlagProbeMessage>
{
    public int Value { get; set; }

    public static uint MessageId => MessageProtocol.MessageWireFormat.ComposeMessageId(
        MessageProtocol.MessageFlag.Standalone, (byte)MessageProtocol.MessageCategory.Category0, 141);

    public static void Serialize(ManualFlagProbeMessage message, ref MessageProtocol.Serialize.MessageBufferWriter writer)
    {
        uint id = MessageId;
        writer.WriteByte((byte)(id >> 24));
        writer.WriteByte((byte)(id >> 16));
        writer.WriteByte((byte)(id >> 8));
        writer.WriteByte((byte)(id));
        writer.WriteInt32(message.Value);
    }

    public static byte[] Serialize(ManualFlagProbeMessage message)
    {
        var writer = MessageProtocol.Serialize.MessageBufferWriter.Create();
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

    public static ManualFlagProbeMessage Deserialize(ref MessageProtocol.Serialize.MessageBufferReader reader)
    {
        reader.Skip(MessageProtocol.MessageWireFormat.IdHeaderSize);
        return new ManualFlagProbeMessage { Value = reader.ReadInt32() };
    }

    public static ManualFlagProbeMessage Deserialize(byte[] data)
    {
        var reader = new MessageProtocol.Serialize.MessageBufferReader(data);
        return Deserialize(ref reader);
    }
}

// ---------- Shared base-type member back-reference reads (audit ledger HIGH — experiment pin) ----------

// Shares the same derived instance between a concrete base member (EventBase) and a derived member (LoginEvent).
// Writing: First (base) records only the base fields and registers the instance → Second (derived) is a back-reference.
// Reading: First creates and registers an EventBase instance, so Second's derived cast fails (KI-35).
#pragma warning disable MSGPROT012
[Message(MessageKind.Standalone, 134)]
public partial class SharedBaseDerivedHost
{
    public EventBase? First { get; set; }
    public LoginEvent? Second { get; set; }
}

// Two base members — derived fields are lost without exceptions and both members share the same base instance (silent type narrowing).
[Message(MessageKind.Standalone, 135)]
public partial class SharedBaseBaseHost
{
    public EventBase? First { get; set; }
    public EventBase? Second { get; set; }
}
#pragma warning restore MSGPROT012

// Control group: sharing the same instance between an abstract member (runtime dispatch — the concrete type is recorded with its header)
// and a concrete member restores the derived fields intact. Pins the difference from a *concrete* base member's declared-type recording (KI-29).
[Message(MessageKind.Standalone, 136)]
public partial class SharedDispatchConcreteHost
{
    public AbstractCommand? Command { get; set; }
    public StartCommand? Concrete { get; set; }
}

// ---------- Concurrent registration races (KI-38) ----------

// Manual message for claim-preemption registration verification — no test registers it first (the race test performs the first registration).
public class ManualRaceMessage : MessageProtocol.Serialize.IHasIdMessageSerializable<ManualRaceMessage>
{
    public int Value { get; set; }

    public static uint MessageId => MessageProtocol.MessageWireFormat.ComposeMessageId(
        MessageProtocol.MessageFlag.Standalone, (byte)MessageProtocol.MessageCategory.Category0, 150);

    public static void Serialize(ManualRaceMessage message, ref MessageProtocol.Serialize.MessageBufferWriter writer)
    {
        uint id = MessageId;
        writer.WriteByte((byte)(id >> 24));
        writer.WriteByte((byte)(id >> 16));
        writer.WriteByte((byte)(id >> 8));
        writer.WriteByte((byte)id);
        writer.WriteInt32(message.Value);
    }

    public static byte[] Serialize(ManualRaceMessage message)
    {
        var writer = MessageProtocol.Serialize.MessageBufferWriter.Create();
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

    public static ManualRaceMessage Deserialize(ref MessageProtocol.Serialize.MessageBufferReader reader)
    {
        reader.Skip(MessageProtocol.MessageWireFormat.IdHeaderSize);
        return new ManualRaceMessage { Value = reader.ReadInt32() };
    }

    public static ManualRaceMessage Deserialize(byte[] data)
    {
        var reader = new MessageProtocol.Serialize.MessageBufferReader(data);
        return Deserialize(ref reader);
    }
}

// Claim-rollback residue verification — the first attempt is rejected with someone else's MessageId; the retry must succeed with its own id.
public class ManualRollbackMessage : MessageProtocol.Serialize.IHasIdMessageSerializable<ManualRollbackMessage>
{
    public int Value { get; set; }

    public uint OwnId => MessageProtocol.MessageWireFormat.ComposeMessageId(
        MessageProtocol.MessageFlag.Standalone, (byte)MessageProtocol.MessageCategory.Category0, 151);

    public static uint MessageId => MessageProtocol.MessageWireFormat.ComposeMessageId(
        MessageProtocol.MessageFlag.Standalone, (byte)MessageProtocol.MessageCategory.Category0, 151);

    public static void Serialize(ManualRollbackMessage message, ref MessageProtocol.Serialize.MessageBufferWriter writer) { }
    public static byte[] Serialize(ManualRollbackMessage message) => System.Array.Empty<byte>();
    public static ManualRollbackMessage Deserialize(ref MessageProtocol.Serialize.MessageBufferReader reader) => new();
    public static ManualRollbackMessage Deserialize(byte[] data) => new();
}
