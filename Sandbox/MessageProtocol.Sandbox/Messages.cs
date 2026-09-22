using MessageProtocol;

namespace SandboxMessages;

// ---------- S1: Standalone — all member types ----------

public enum Color : byte { Red, Green, Blue }

[Message(MessageKind.Standalone, 1, MessageCategory.Category3)]
public partial class AllPrimitives
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
    public Color Color { get; set; }
}

// ---------- S2: NonId ----------

[Message(MessageKind.NonId)]
public partial class Ping
{
    public int Seq { get; set; }
}

// ---------- S3: Group root/element ----------

[Message(MessageKind.Parent, 10)]
public partial class ShapeRoot
{
    public string? Name { get; set; }
}

[Message(MessageKind.Child, 11)]
public partial class Circle : ShapeRoot
{
    public double Radius { get; set; }
}

// ---------- S4: Collections ----------

[Message(MessageKind.Standalone, 2)]
public partial class Collections
{
    public byte[]? Bytes { get; set; }
    public int[]? Ints { get; set; }
    public List<string>? Names { get; set; }
    public List<AllPrimitives>? Items { get; set; }
    public IList<int>? View { get; set; }
}

// ---------- S5: Nested objects and graphs ----------

public class NestedPoco
{
    public int X { get; set; }
    public string? Tag { get; set; }
}

[Message(MessageKind.Standalone, 3)]
public partial class TreeNode
{
    public string? Label { get; set; }
    public TreeNode? Left { get; set; }
    public TreeNode? Right { get; set; }
    public NestedPoco? Poco { get; set; }
}

// ---------- S6: Member control ----------

[Message(MessageKind.Standalone, 4)]
public partial class MemberControl
{
    public int Kept { get; set; }

    [MessageIgnore]
    public int Skipped { get; set; }

    [MessageInclude]
    int _hidden;

    public void SetHidden(int value) => _hidden = value;
    public int GetHidden() => _hidden;
}

// ---------- S10: Generic message ----------
// GenericMessage construction declarations: declare each supported closed construction with its class ID.
// Declared constructions are auto-registered on module load on both the sending and receiving side.
[Message(MessageKind.Standalone, 40)]
[GenericMessage(typeof(Envelope<AllPrimitives>), ClassId = 1)]
[GenericMessage(typeof(Envelope<Circle>), ClassId = 2)]
public partial class Envelope<T>
{
    public T? Value { get; set; }
    public string? Note { get; set; }
    public List<T?>? Items { get; set; }
}

// ---------- S13: Abstract group root with polymorphic member ----------
// An abstract [Message(MessageKind.Parent)] cannot be instantiated, so no static Serialize/Deserialize is generated for it.
// When this type is used as a member, the generator records the *concrete* element (header included) via runtime
// message dispatch instead of a static delegate.
[Message(MessageKind.Parent, 70)]
public abstract partial class ShapeCommand
{
    public long Seq { get; set; }
}

[Message(MessageKind.Child, 71)]
public partial class DrawCommand : ShapeCommand
{
    public string? Layer { get; set; }
}

[Message(MessageKind.Child, 72)]
public partial class ClearCommand : ShapeCommand
{
    public bool Full { get; set; }
}

[Message(MessageKind.Standalone, 73)]
public partial class CommandBatch
{
    public ShapeCommand? Head { get; set; }
    public List<ShapeCommand>? Queue { get; set; }
}

// ---------- S15: Automatic [Message] declaration (kind inference, FullName hash IDs) ----------

[Message]                                   // no [Message] derivatives → inferred as Standalone
public partial class AutoNote
{
    public string? Text { get; set; }
}

[Message]                                   // AutoJoin/AutoLeave derivatives exist → inferred as GroupRoot
public partial class AutoEvent
{
    public long Timestamp { get; set; }
}

[Message]                                   // message attribute inherited from an ancestor → inferred as GroupElement
public partial class AutoJoin : AutoEvent
{
    public int PlayerId { get; set; }
}

[Message]
public partial class AutoLeave : AutoEvent
{
    public string? Reason { get; set; }
}
