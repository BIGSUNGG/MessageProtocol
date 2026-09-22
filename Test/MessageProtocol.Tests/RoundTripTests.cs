using MessageProtocol;
using MessageProtocol.Serialize;
using MessageProtocol.Tests.Fixtures;
using Xunit;

namespace MessageProtocol.Tests;

public class RoundTripTests
{
    [Fact]
    public void all_member_types_round_trip()
    {
        var msg = new AllTypesMessage
        {
            Bool = true,
            Byte = 200,
            SByte = -100,
            Int16 = -30000,
            UInt16 = 60000,
            Int32 = -2_000_000_000,
            UInt32 = 4_000_000_000,
            Int64 = long.MaxValue,
            UInt64 = ulong.MaxValue,
            Single = 1.23456f,
            Double = -9.87654321,
            Decimal = 79228162514264337593543950335m,
            Char = '\u0007',
            // intentional non-ASCII payload: exercises UTF-8 round-trip
            Text = "경계값",
            Level = Level.Low,
            Blob = new byte[] { 0, 1, 255 },
            Samples = new List<double> { 0.1, -0.2 },
            Tags = new[] { "a", "b" },
            Codes = new List<byte> { 9, 8 },
            Nested = new FlatMessage { Value = -1 },
        };

        var rt = MessageSerializer.Deserialize<AllTypesMessage>(MessageSerializer.Serialize(msg));

        Assert.Equal(msg.Bool, rt.Bool);
        Assert.Equal(msg.Byte, rt.Byte);
        Assert.Equal(msg.SByte, rt.SByte);
        Assert.Equal(msg.Int16, rt.Int16);
        Assert.Equal(msg.UInt16, rt.UInt16);
        Assert.Equal(msg.Int32, rt.Int32);
        Assert.Equal(msg.UInt32, rt.UInt32);
        Assert.Equal(msg.Int64, rt.Int64);
        Assert.Equal(msg.UInt64, rt.UInt64);
        Assert.Equal(msg.Single, rt.Single);
        Assert.Equal(msg.Double, rt.Double);
        Assert.Equal(msg.Decimal, rt.Decimal);
        Assert.Equal(msg.Char, rt.Char);
        Assert.Equal(msg.Text, rt.Text);
        Assert.Equal(msg.Level, rt.Level);
        Assert.Equal(msg.Blob, rt.Blob);
        Assert.Equal(msg.Samples, rt.Samples);
        Assert.Equal(msg.Tags, rt.Tags);
        Assert.Equal(msg.Codes, rt.Codes!.ToList());
        Assert.Equal(msg.Nested!.Value, rt.Nested!.Value);
    }

    [Fact]
    public void standalone_wire_layout_is_pinned()
    {
        byte[] bytes = MessageSerializer.Serialize(new FlatMessage { Value = 0x11223344 });

        // header(Standalone|cat0)=0x20, id=100 → 00 00 64, payload LE
        Assert.Equal(new byte[] { 0x20, 0x00, 0x00, 0x64, 0x44, 0x33, 0x22, 0x11 }, bytes);
    }

    [Fact]
    public void nonid_wire_layout_is_pinned()
    {
        byte[] bytes = MessageSerializer.Serialize(new NoIdMessage { Flag = 7 });
        // header(NonId|cat0)=0x10, Flag=0x07, Note=null → int32(-1) LE
        Assert.Equal(new byte[] { 0x10, 0x07, 0xFF, 0xFF, 0xFF, 0xFF }, bytes);
    }

    [Fact]
    public void category_nibble_is_reflected_in_the_header_byte()
    {
        byte[] bytes = MessageSerializer.Serialize(new AllTypesMessage());
        Assert.Equal(MessageWireFormat.ComposeHeaderByte(MessageFlag.Standalone, 5), bytes[0]);
    }

    [Fact]
    public void messageid_static_properties_match_the_composition_rules()
    {
        Assert.Equal(MessageWireFormat.ComposeMessageId(MessageFlag.Standalone, 0, 100), FlatMessage.MessageId);
        Assert.Equal(MessageWireFormat.ComposeMessageId(MessageFlag.Standalone, 5, 101), AllTypesMessage.MessageId);
        Assert.Equal(MessageWireFormat.ComposeMessageId(MessageFlag.Parent, 0, 110), EventBase.MessageId);
        Assert.Equal(MessageWireFormat.ComposeMessageId(MessageFlag.Child, 0, 111), LoginEvent.MessageId);
    }

    [Fact]
    public void struct_messages_round_trip()
    {
        var msg = new PointMessage { X = -3, Y = 9 };
        var rt = MessageSerializer.Deserialize<PointMessage>(MessageSerializer.Serialize(msg));
        Assert.Equal(-3, rt.X);
        Assert.Equal(9, rt.Y);
    }

    [Fact]
    public void record_messages_round_trip()
    {
        var msg = new SettingsRecord { Theme = "dark", Volume = 11 };
        var rt = MessageSerializer.Deserialize<SettingsRecord>(MessageSerializer.Serialize(msg));
        Assert.Equal("dark", rt.Theme);
        Assert.Equal(11, rt.Volume);
    }

    [Fact]
    public void cyclic_and_shared_references_are_restored()
    {
        var a = new GraphMessage { Label = "a", Poco = new PlainPoco { Number = 1, Name = "p" } };
        var b = new GraphMessage { Label = "b" };
        a.Next = b;
        b.Next = a;        // cycle
        a.Other = b;       // shared (b appears twice)

        var rt = MessageSerializer.Deserialize<GraphMessage>(MessageSerializer.Serialize(a));

        Assert.Equal("a", rt.Label);
        Assert.Equal("b", rt.Next!.Label);
        Assert.True(ReferenceEquals(rt.Next.Next, rt));
        Assert.True(ReferenceEquals(rt.Other, rt.Next));
        Assert.Equal(1, rt.Poco!.Number);
        Assert.Equal("p", rt.Poco.Name);
    }

    [Fact]
    public void inherited_members_are_serialized_together()
    {
        var msg = new LoginEvent { Timestamp = 1234L, User = "kim" };
        var rt = MessageSerializer.Deserialize<LoginEvent>(MessageSerializer.Serialize(msg));
        Assert.Equal(1234L, rt.Timestamp);   // base member
        Assert.Equal("kim", rt.User);
    }

    [Fact]
    public void messageignore_excludes_and_messageinclude_includes()
    {
        var msg = new MemberControlMessage { Kept = 5, Excluded = 99 };
        msg.SetInternal(42);

        var rt = MessageSerializer.Deserialize<MemberControlMessage>(MessageSerializer.Serialize(msg));

        Assert.Equal(5, rt.Kept);
        Assert.Equal(0, rt.Excluded);
        Assert.Equal(42, rt.GetInternal());
    }

    [Fact]
    public void generic_entry_supports_span_and_memory_inputs()
    {
        byte[] bytes = MessageSerializer.Serialize(new FlatMessage { Value = 9 });

        Assert.Equal(9, MessageSerializer.Deserialize<FlatMessage>((ReadOnlySpan<byte>)bytes).Value);
        Assert.Equal(9, MessageSerializer.Deserialize<FlatMessage>(new ReadOnlyMemory<byte>(bytes)).Value);
        Assert.Equal(9, MessageSerializer.Deserialize<FlatMessage>(bytes).Value);
    }

    [Fact]
    public void deserializing_empty_data_throws()
    {
        Assert.Throws<ArgumentException>(() => MessageSerializer.Deserialize<FlatMessage>(Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => MessageSerializer.Deserialize(Array.Empty<byte>()));
        Assert.Throws<ArgumentNullException>(() => MessageSerializer.Deserialize<FlatMessage>((byte[])null!));
    }

    [Fact]
    public void pooled_path_produces_bytes_identical_to_the_compatible_path()
    {
        var msg = new AllTypesMessage { Int32 = 3, Text = "pooled", Samples = new List<double> { 1.5 } };
        using var pooled = MessageSerializer.SerializePooled(msg);
        Assert.Equal(MessageSerializer.Serialize(msg), pooled.ToArray());
    }

    // ---------- generic messages ----------

    [Fact]
    public void generic_messages_round_trip()
    {
        var msg = new GenericEnvelope<FlatMessage>
        {
            Note = "gen",
            Value = new FlatMessage { Value = 7 },
            Items = new List<FlatMessage?> { new() { Value = 1 }, null, new() { Value = 2 } },
        };

        var rt = MessageSerializer.Deserialize<GenericEnvelope<FlatMessage>>(MessageSerializer.Serialize(msg));

        Assert.Equal("gen", rt.Note);
        Assert.Equal(7, rt.Value!.Value);
        Assert.NotNull(rt.Items);
        Assert.Equal(3, rt.Items!.Count);
        Assert.Equal(1, rt.Items[0]!.Value);
        Assert.Null(rt.Items[1]);
        Assert.Equal(2, rt.Items[2]!.Value);
    }

    [Fact]
    public void generic_nonid_messages_round_trip()
    {
        var msg = new GenericPair<FlatMessage> { First = new FlatMessage { Value = 3 }, Tag = 9 };
        var rt = MessageSerializer.Deserialize<GenericPair<FlatMessage>>(MessageSerializer.Serialize(msg));
        Assert.Equal(3, rt.First!.Value);
        Assert.Equal(9, rt.Tag);
    }

    [Fact]
    public void generic_constructions_round_trip_via_object_dispatch()
    {
        var msg = new GenericEnvelope<FlatMessage> { Value = new FlatMessage { Value = 11 } };
        object? decoded = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)msg));
        var rt = Assert.IsType<GenericEnvelope<FlatMessage>>(decoded);
        Assert.Equal(11, rt.Value!.Value);
    }

    [Fact]
    public void generic_header_orders_flags0_messageid_then_classid()
    {
        var bytes = MessageSerializer.Serialize(new GenericEnvelope<FlatMessage> { Value = new FlatMessage { Value = 1 } });

        Assert.Equal(MessageWireFormat.ComposeHeaderByte(MessageFlag.Generic, 0), bytes[0]);
        Assert.Equal(0, bytes[1]);
        Assert.Equal(0, bytes[2]);
        Assert.Equal(120, bytes[3]); // MessageId 24 bits (standalone id)
        Assert.Equal(0, bytes[4]);
        Assert.Equal(0, bytes[5]);
        Assert.Equal(1, bytes[6]); // construction ClassId 24 bits (FlatMessage construction = 1)
    }

    [Fact]
    public void multiple_constructions_of_the_same_declaration_are_dispatched_together()
    {
        var a = new GenericEnvelope<FlatMessage> { Value = new FlatMessage { Value = 1 } };
        var b = new GenericEnvelope<SettingsRecord> { Value = new SettingsRecord { Theme = "dark", Volume = 3 } };

        var da = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)a));
        var db = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)b));

        var ra = Assert.IsType<GenericEnvelope<FlatMessage>>(da);
        var rb = Assert.IsType<GenericEnvelope<SettingsRecord>>(db);
        Assert.Equal(1, ra.Value!.Value);
        Assert.Equal("dark", rb.Value!.Theme);
    }

    [Fact]
    public void multi_type_parameter_generics_round_trip()
    {
        var msg = new GenericDuo<FlatMessage, SettingsRecord>
        {
            First = new FlatMessage { Value = 5 },
            Second = new SettingsRecord { Theme = "t", Volume = 2 },
        };

        var rt = MessageSerializer.Deserialize<GenericDuo<FlatMessage, SettingsRecord>>(MessageSerializer.Serialize(msg));
        Assert.Equal(5, rt.First!.Value);
        Assert.Equal("t", rt.Second!.Theme);

        var decoded = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)msg));
        var rd = Assert.IsType<GenericDuo<FlatMessage, SettingsRecord>>(decoded);
        Assert.Equal(2, rd.Second!.Volume);
    }

    [Fact]
    public void carrier_declared_construction_coexists_with_declaration_construction_and_round_trips()
    {
        // Construction declared on the carrier type (ClassId 3)
        var msg = new GenericEnvelope<PointMessage> { Value = new PointMessage { X = 3, Y = 4 } };

        var rt = MessageSerializer.Deserialize<GenericEnvelope<PointMessage>>(MessageSerializer.Serialize(msg));
        Assert.Equal(3, rt.Value!.X);

        object? decoded = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)msg));
        var rd = Assert.IsType<GenericEnvelope<PointMessage>>(decoded);
        Assert.Equal(4, rd.Value!.Y);

        // The declaration-side [GenericMessage] construction (ClassId 1) also keeps working
        var decl = new GenericEnvelope<FlatMessage> { Value = new FlatMessage { Value = 9 } };
        var dd = Assert.IsType<GenericEnvelope<FlatMessage>>(MessageSerializer.Deserialize(MessageSerializer.Serialize((object)decl)));
        Assert.Equal(9, dd.Value!.Value);
    }

    [Fact]
    public void two_constructions_of_the_same_generic_payload_round_trip_in_one_message()
    {
        var msg = new DuplicateGenericPayloadsMessage
        {
            IntPair = new GenericPair<int> { First = 42, Tag = 1 },
            TextPair = new GenericPair<string> { First = "pair", Tag = 2 },
        };

        var rt = MessageSerializer.Deserialize<DuplicateGenericPayloadsMessage>(MessageSerializer.Serialize(msg));

        Assert.Equal(42, rt.IntPair!.First);
        Assert.Equal(1, rt.IntPair.Tag);
        Assert.Equal("pair", rt.TextPair!.First);
        Assert.Equal(2, rt.TextPair.Tag);
    }

    [Fact]
    public void generic_message_without_a_construction_declaration_throws_on_serialization()
    {
        var msg = new UnregisteredGeneric<FlatMessage> { X = 1 };
        var ex = Assert.Throws<InvalidOperationException>(() => MessageSerializer.Serialize(msg));
        Assert.Contains("GenericMessage", ex.Message); // includes guidance on how to declare it
    }
}

// ---------- generated Deserialize header verification (KI-5) ----------

/// <summary>
/// The generated Deserialize(ref reader) used to skip the header only, so feeding it another type's bytes
/// silently reinterpreted the payload (KI-5). It now compares the frame's 4-byte MessageId (or the NonId
/// 1-byte header) against the type's own and rejects mismatches with an informative InvalidDataException.
/// The branch is based on frame bytes, so even a forged header claiming the NonId flag is caught by the
/// 1-byte comparison.
/// </summary>
public class WireHeaderValidationTests
{
    [Fact]
    public void bytes_of_a_different_type_are_rejected_at_the_header()
    {
        var wrongBytes = MessageSerializer.Serialize(new FlatMessage { Value = 7 });

        var exception = Assert.Throws<System.IO.InvalidDataException>(
            () => MessageSerializer.Deserialize<AllTypesMessage>(wrongBytes));

        Assert.Contains(nameof(AllTypesMessage), exception.Message);
        Assert.Contains("does not match", exception.Message);
    }

    [Fact]
    public void forged_nonid_header_is_rejected_by_the_one_byte_comparison()
    {
        var bytes = MessageSerializer.Serialize(new FlatMessage { Value = 7 });
        bytes[0] = 0xFF; // claims the NonId flag — a forgery attempting to bypass the 4-byte read

        Assert.Throws<System.IO.InvalidDataException>(() => MessageSerializer.Deserialize<FlatMessage>(bytes));
    }

    [Fact]
    public void tampering_with_the_last_messageid_byte_is_also_rejected()
    {
        var bytes = MessageSerializer.Serialize(new FlatMessage { Value = 7 });
        bytes[3] ^= 0x01; // flips low id bits — blocks decoding to the same type by coincidence

        Assert.Throws<System.IO.InvalidDataException>(() => MessageSerializer.Deserialize<FlatMessage>(bytes));
    }

    [Fact]
    public void legitimate_frames_round_trip_unchanged_after_verification_passes()
    {
        var message = new FlatMessage { Value = 12345 };
        var back = MessageSerializer.Deserialize<FlatMessage>(MessageSerializer.Serialize(message));

        Assert.Equal(12345, back.Value);
    }
}
