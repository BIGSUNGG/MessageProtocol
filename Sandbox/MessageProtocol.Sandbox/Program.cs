using MessageProtocol;
using MessageProtocol.Serialize;
using SandboxMessages;

// Sandbox acceptance conditions: verifies Feature-Spec F1–F7 by execution.
// Exits with a non-zero code on failure.

int failures = 0;

void Check(string name, bool condition)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}");
    if (!condition) failures++;
}

// ---------- S1: Standalone + all member types round-trip ----------
{
    var msg = new AllPrimitives
    {
        Bool = true,
        Byte = 255,
        SByte = -1,
        Int16 = short.MinValue,
        UInt16 = ushort.MaxValue,
        Int32 = int.MinValue,
        UInt32 = uint.MaxValue,
        Int64 = long.MinValue,
        UInt64 = ulong.MaxValue,
        Single = 3.14f,
        Double = -2.71828,
        Decimal = 12345.6789m,
        // intentional non-ASCII payload: exercises UTF-8 round-trip
        Char = '한',
        Text = "메시지 프로토콜",
        Color = Color.Green,
    };

    byte[] bytes = MessageSerializer.Serialize(msg);
    var roundTrip = MessageSerializer.Deserialize<AllPrimitives>(bytes);

    Check("S1 round-trip values are equal",
        roundTrip.Bool == msg.Bool && roundTrip.Byte == msg.Byte && roundTrip.SByte == msg.SByte &&
        roundTrip.Int16 == msg.Int16 && roundTrip.UInt16 == msg.UInt16 &&
        roundTrip.Int32 == msg.Int32 && roundTrip.UInt32 == msg.UInt32 &&
        roundTrip.Int64 == msg.Int64 && roundTrip.UInt64 == msg.UInt64 &&
        roundTrip.Single == msg.Single && roundTrip.Double == msg.Double &&
        roundTrip.Decimal == msg.Decimal && roundTrip.Char == msg.Char &&
        roundTrip.Text == msg.Text && roundTrip.Color == msg.Color);

    // Header: flags=Standalone (high nibble) + category=3 (low nibble)
    byte expectedHeader = MessageWireFormat.ComposeHeaderByte(MessageFlag.Standalone, 3);
    Check("S1 header byte", bytes[0] == expectedHeader);

    uint expectedId = MessageWireFormat.ComposeMessageId(MessageFlag.Standalone, 3, 1);
    Check("S1 MessageId composition", AllPrimitives.MessageId == expectedId);

    // null / empty strings
    var nullText = new AllPrimitives { Text = null };
    var emptyText = new AllPrimitives { Text = string.Empty };
    Check("S1 null string", MessageSerializer.Deserialize<AllPrimitives>(MessageSerializer.Serialize(nullText)).Text == null);
    Check("S1 empty string", MessageSerializer.Deserialize<AllPrimitives>(MessageSerializer.Serialize(emptyText)).Text == string.Empty);
}

// ---------- S2: NonId ----------
{
    var ping = new Ping { Seq = 42 };
    byte[] bytes = MessageSerializer.Serialize(ping);
    Check("S2 NonId header is 1 byte", bytes[0] == MessageWireFormat.ComposeHeaderByte(MessageFlag.NonIdMessage, 0) && bytes.Length == 5);
    Check("S2 round-trip", MessageSerializer.Deserialize<Ping>(bytes).Seq == 42);

    bool rejected = false;
    try { MessageSerializer.Deserialize(bytes); }
    catch (System.IO.InvalidDataException) { rejected = true; }   // illegal wire content → InvalidDataException (2026-09-08, corrected after a fuzzer finding)
    Check("S2 object Deserialize rejects NonId", rejected);
}

// ---------- S3: Group + object dispatch ----------
{
    var circle = new Circle { Name = "c1", Radius = 2.5 };
    byte[] bytes = MessageSerializer.Serialize((object)circle);
    object? decoded = MessageSerializer.Deserialize(bytes);

    Check("S3 object dispatch restores the element type",
        decoded is Circle c && c.Name == "c1" && c.Radius == 2.5);

    byte header = bytes[0];
    Check("S3 element header flags", MessageWireFormat.GetFlags(header) == MessageFlag.Child);
}

// ---------- S4: Collections ----------
{
    var msg = new Collections
    {
        Bytes = new byte[] { 1, 2, 3 },
        Ints = null,
        Names = new List<string> { "a", "b", null! },
        Items = new List<AllPrimitives> { new() { Int32 = 7, Text = "x" } },
        View = new List<int> { 10, 20 },
    };

    var rt = MessageSerializer.Deserialize<Collections>(MessageSerializer.Serialize(msg));
    Check("S4 byte[] round-trip", rt.Bytes != null && rt.Bytes.SequenceEqual(new byte[] { 1, 2, 3 }));
    Check("S4 null array", rt.Ints == null);
    Check("S4 List<string> round-trip", rt.Names != null && rt.Names.Count == 3 && rt.Names[0] == "a" && rt.Names[1] == "b");
    Check("S4 List<nested messages>", rt.Items != null && rt.Items.Count == 1 && rt.Items[0].Int32 == 7 && rt.Items[0].Text == "x");
    Check("S4 IList<int>", rt.View != null && rt.View.Count == 2 && rt.View[1] == 20);
}

// ---------- S5: Cyclic and shared references ----------
{
    var node = new TreeNode { Label = "root", Poco = new NestedPoco { X = 9, Tag = "t" } };
    node.Left = node;               // self-reference → back-reference
    node.Right = node.Left;         // shared reference

    var rt = MessageSerializer.Deserialize<TreeNode>(MessageSerializer.Serialize(node));
    Check("S5 self-reference restored", ReferenceEquals(rt.Left, rt));
    Check("S5 shared reference restored", ReferenceEquals(rt.Right, rt.Left));
    Check("S5 nested POCO restored", rt.Poco != null && rt.Poco.X == 9 && rt.Poco.Tag == "t");
}

// ---------- S6: MessageIgnore / MessageInclude ----------
{
    var msg = new MemberControl { Kept = 1, Skipped = 99 };
    msg.SetHidden(7);

    var rt = MessageSerializer.Deserialize<MemberControl>(MessageSerializer.Serialize(msg));
    Check("S6 regular member kept", rt.Kept == 1);
    Check("S6 MessageIgnore excluded", rt.Skipped == 0);
    Check("S6 MessageInclude included", rt.GetHidden() == 7);
}

// ---------- S7: Polymorphic Serialize(object) ----------
{
    ShapeRoot shape = new Circle { Name = "poly", Radius = 1.0 };
    byte[] bytes = MessageSerializer.Serialize((object)shape);   // serializes by runtime type
    object? decoded = MessageSerializer.Deserialize(bytes);
    Check("S7 derived type serialized", decoded is Circle pc && pc.Name == "poly" && pc.Radius == 1.0);

    // the root type itself is also registered and routable
    var root = new ShapeRoot { Name = "root" };
    var rt = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)root));
    Check("S7 root type routed", rt is ShapeRoot sr && sr.Name == "root");
}

// ---------- S8: PooledBuffer ----------
{
    var msg = new AllPrimitives { Int32 = 5, Text = "pooled" };
    using (var pooled = MessageSerializer.SerializePooled(msg))
    {
        byte[] compat = MessageSerializer.Serialize(msg);
        Check("S8 pooled == byte[] path", pooled.Span.SequenceEqual(compat));
        pooled.Dispose(); // Dispose is idempotent
        Check("S8 Dispose is idempotent", pooled.Length == 0);
    }
}

// ---------- S9: Manual implementation + RegisterType ----------
{
    MessageSerializer.RegisterType(typeof(ManualMessage));

    var msg = new ManualMessage { Value = 1234 };
    byte[] bytes = MessageSerializer.Serialize(msg);      // generic path
    Check("S9 manual message generic round-trip", MessageSerializer.Deserialize<ManualMessage>(bytes).Value == 1234);

    object? decoded = MessageSerializer.Deserialize(bytes); // routed by ID
    Check("S9 manual message object dispatch", decoded is ManualMessage m && m.Value == 1234);
}

// ---------- S10: Generic message ----------
{
    // T holds an ID message that supports object dispatch (AllPrimitives here).
    // NonId messages carry no ID, so the wire spec cannot route them through a generic-T construction.
    var msg = new Envelope<AllPrimitives>
    {
        Note = "gen",
        Value = new AllPrimitives { Int32 = 7, Text = "t" },
        Items = new List<AllPrimitives?> { new() { Int32 = 1 }, null, new() { Int32 = 2 } },
    };

    var rt = MessageSerializer.Deserialize<Envelope<AllPrimitives>>(MessageSerializer.Serialize(msg));
    Check("S10 generic round-trip", rt.Note == "gen" && rt.Value != null && rt.Value.Int32 == 7 && rt.Value.Text == "t");
    Check("S10 T collection round-trip", rt.Items != null && rt.Items.Count == 3 && rt.Items[0]!.Int32 == 1 && rt.Items[1] == null && rt.Items[2]!.Int32 == 2);

    // Closed constructions support object dispatch via declaration-based auto-registration (no manual RegisterType).
    object? decodedGeneric = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)msg));
    Check("S10 generic object dispatch", decodedGeneric is Envelope<AllPrimitives> env && env.Value!.Int32 == 7);
}

// ---------- S11: Generic construction coexistence + wire header ----------
{
    var a = new Envelope<AllPrimitives> { Value = new AllPrimitives { Int32 = 1 } };
    var b = new Envelope<Circle> { Value = new Circle { Name = "c", Radius = 2.0 } };

    byte[] bytesA = MessageSerializer.Serialize(a);
    Check("S11 generic header flags are 0", MessageWireFormat.GetFlags(bytesA[0]) == MessageFlag.Generic);
    Check("S11 class id bytes written", bytesA[4] == 0 && bytesA[5] == 0 && bytesA[6] == 1);

    object? da = MessageSerializer.Deserialize(bytesA);
    object? db = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)b));
    Check("S11 construction coexistence A", da is Envelope<AllPrimitives> ea && ea.Value!.Int32 == 1);
    Check("S11 construction coexistence B", db is Envelope<Circle> ec && ec.Value!.Radius == 2.0);
}

// ---------- S12: Distributed-declaration construction ----------
{
    // A construction declared via a separate carrier (Constructions.cs), not in the Envelope<T> declaration.
    var msg = new Envelope<TreeNode> { Value = new TreeNode { Label = "dist" } };
    object? decoded = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)msg));
    Check("S12 distributed-declaration construction dispatch", decoded is Envelope<TreeNode> env && env.Value!.Label == "dist");
}

// ---------- S13: Abstract group root with polymorphic member ----------
{
    // An abstract [Message(MessageKind.Parent)] member is written via runtime message dispatch, which records the
    // concrete element — the actual element type and its derived members must be restored, not the declared abstract root.
    var batch = new CommandBatch
    {
        Head = new DrawCommand { Seq = 1, Layer = "bg" },
        Queue = new List<ShapeCommand>
        {
            new ClearCommand { Seq = 2, Full = true },
            new DrawCommand { Seq = 3, Layer = "fg" },
        },
    };

    var roundTrip = MessageSerializer.Deserialize<CommandBatch>(MessageSerializer.Serialize(batch));

    Check("S13 abstract root member restores the concrete type",
        roundTrip.Head is DrawCommand head && head.Layer == "bg" && head.Seq == 1);
    Check("S13 abstract root collection restores polymorphic items",
        roundTrip.Queue is { Count: 2 }
        && roundTrip.Queue[0] is ClearCommand clear && clear.Full && clear.Seq == 2
        && roundTrip.Queue[1] is DrawCommand tail && tail.Layer == "fg");
    Check("S13 abstract root member null round-trip",
        MessageSerializer.Deserialize<CommandBatch>(MessageSerializer.Serialize(new CommandBatch())).Head is null);
}

// ---------- S14: Trust-boundary rejection (KI-5 header validation, KI-36 reference tag validation) ----------
{
    // Untrusted frames must be rejected at the entry point with a descriptive exception — never silently reinterpreted.
    string? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }

    bool RejectsWith(Action action, string expected) =>
        Capture(action) is { } rejection && rejection.Contains(expected);

    var foreignBytes = MessageSerializer.Serialize(new Collections { });
    Check("S14 bytes of another type are rejected at the header",
        RejectsWith(() => MessageSerializer.Deserialize<AllPrimitives>(foreignBytes), "does not match AllPrimitives"));

    var forged = MessageSerializer.Serialize(new AllPrimitives { });
    forged[0] = 0xFF;   // claims the NonId flag — attempts to bypass the 4-byte read
    Check("S14 forged NonId header is rejected",
        RejectsWith(() => MessageSerializer.Deserialize<AllPrimitives>(forged), "does not match AllPrimitives"));

    var batch = MessageSerializer.Serialize(new CommandBatch
    {
        Head = new DrawCommand { Seq = 1, Layer = "bg" },
    });
    batch[4] = 3;   // sets the ReferenceKind tag of the first reference member (Head) to an out-of-spec value
    Check("S14 unknown reference tag is rejected immediately",
        RejectsWith(() => MessageSerializer.Deserialize<CommandBatch>(batch), "Unknown reference kind 3"));
}

// ---------- S15: Automatic [Message] declaration (kind inference, FullName hash IDs) ----------
{
    // Standalone inference: registered as Standalone from the declaration alone — no kind or ID given.
    var note = new AutoNote { Text = "자동" }; // intentional non-ASCII payload: exercises UTF-8 round-trip
    var rtNote = MessageSerializer.Deserialize<AutoNote>(MessageSerializer.Serialize(note));
    Check("S15 Standalone inferred round-trip", rtNote.Text == "자동");

    uint noteHash = MessageIdHash.FromFullName(typeof(AutoNote).FullName!);
    Check("S15 Standalone MessageId = FullName hash",
        AutoNote.MessageId == MessageWireFormat.ComposeMessageId(MessageFlag.Standalone, 0, noteHash));

    // Group inference: derivatives exist → root; ancestor inheritance → element. Each element is restored via object dispatch.
    var join = new AutoJoin { Timestamp = 123L, PlayerId = 7 };
    var leave = new AutoLeave { Timestamp = 456L, Reason = "quit" };

    var dJoin = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)join));
    var dLeave = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)leave));

    Check("S15 GroupElement inferred dispatch",
        dJoin is AutoJoin j && j.Timestamp == 123L && j.PlayerId == 7
        && dLeave is AutoLeave l && l.Timestamp == 456L && l.Reason == "quit");

    // Wire check: header flag is the GroupElement nibble; the 3 ID bytes are the hash in big-endian.
    uint joinHash = MessageIdHash.FromFullName(typeof(AutoJoin).FullName!);
    var joinBytes = MessageSerializer.Serialize((object)join);
    Check("S15 element header flags and hashed id bytes",
        MessageWireFormat.GetFlags(joinBytes[0]) == MessageFlag.Child
        && joinBytes[1] == (byte)(joinHash >> 16)
        && joinBytes[2] == (byte)(joinHash >> 8)
        && joinBytes[3] == (byte)joinHash);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL SCENARIOS PASSED" : $"{failures} SCENARIO CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
