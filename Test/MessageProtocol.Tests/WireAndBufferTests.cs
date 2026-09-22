using System.Text;
using MessageProtocol;
using MessageProtocol.Serialize;
using Xunit;

namespace MessageProtocol.Tests;

public class WireFormatTests
{
    [Fact]
    public void header_is_composed_of_flags_high_nibble_and_category_low_nibble()
    {
        byte header = MessageWireFormat.ComposeHeaderByte(MessageFlag.Standalone, 5);
        Assert.Equal(0x25, header);
        Assert.Equal(MessageFlag.Standalone, MessageWireFormat.GetFlags(header));
        Assert.Equal(5, MessageWireFormat.GetCategory(header));
    }

    [Fact]
    public void message_id_is_composed_from_header_byte_and_24bit_value()
    {
        uint id = MessageWireFormat.ComposeMessageId(MessageFlag.Parent, 3, 0xABCDEF);
        Assert.Equal((uint)0x43ABCDEF, id);
    }

    [Fact]
    public void message_id_value_is_masked_to_24_bits()
    {
        uint id = MessageWireFormat.ComposeMessageId(MessageFlag.Standalone, 0, 0xFFFF_FFFF);
        Assert.Equal(0x00FF_FFFFu, id & MessageWireFormat.MessageIdValueMask);
    }

    [Theory]
    [InlineData(MessageFlag.NonIdMessage, false)]
    [InlineData(MessageFlag.Standalone, true)]
    [InlineData(MessageFlag.Parent, true)]
    [InlineData(MessageFlag.Child, true)]
    public void only_nonid_lacks_an_embedded_id(MessageFlag flag, bool expected)
    {
        byte header = MessageWireFormat.ComposeHeaderByte(flag, 0);
        Assert.Equal(expected, MessageWireFormat.HasEmbeddedMessageId(header));
    }

    [Fact]
    public void header_size_constants()
    {
        Assert.Equal(1, MessageWireFormat.NonIdHeaderSize);
        Assert.Equal(4, MessageWireFormat.IdHeaderSize);
    }
}

public class BufferIOTests
{
    [Fact]
    public void all_primitive_types_round_trip_as_little_endian()
    {
        var writer = MessageBufferWriter.Create(1);
        writer.WriteBoolean(true);
        writer.WriteByte(0xAB);
        writer.WriteSByte(-5);
        writer.WriteInt16(short.MinValue);
        writer.WriteUInt16(ushort.MaxValue);
        writer.WriteInt32(int.MinValue);
        writer.WriteUInt32(uint.MaxValue);
        writer.WriteInt64(long.MinValue);
        writer.WriteUInt64(ulong.MaxValue);
        writer.WriteSingle(-1.5f);
        writer.WriteDouble(double.MaxValue);
        writer.WriteDecimal(-12345.6789m);
        writer.WriteChar('Z');

        // Little-endian check: int32 -2 (0xFFFFFFFE)
        writer.WriteInt32(-2);

        var reader = new MessageBufferReader(writer.WrittenReadOnlySpan);
        Assert.True(reader.ReadBoolean());
        Assert.Equal(0xAB, reader.ReadByte());
        Assert.Equal(-5, reader.ReadSByte());
        Assert.Equal(short.MinValue, reader.ReadInt16());
        Assert.Equal(ushort.MaxValue, reader.ReadUInt16());
        Assert.Equal(int.MinValue, reader.ReadInt32());
        Assert.Equal(uint.MaxValue, reader.ReadUInt32());
        Assert.Equal(long.MinValue, reader.ReadInt64());
        Assert.Equal(ulong.MaxValue, reader.ReadUInt64());
        Assert.Equal(-1.5f, reader.ReadSingle());
        Assert.Equal(double.MaxValue, reader.ReadDouble());
        Assert.Equal(-12345.6789m, reader.ReadDecimal());
        Assert.Equal('Z', reader.ReadChar());

        int start = reader.Position;
        Assert.Equal(-2, reader.ReadInt32());
        var leBytes = writer.WrittenReadOnlySpan.Slice(start, 4).ToArray();
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF }, leBytes);

        writer.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ascii")]
    [InlineData("한글·日本語·🌟")] // intentional non-ASCII payload: exercises UTF-8 round-trip
    public void string_round_trips_with_length_prefix(string? value)
    {
        var writer = MessageBufferWriter.Create();
        writer.WriteString(value);

        var reader = new MessageBufferReader(writer.WrittenReadOnlySpan);
        Assert.Equal(value, reader.ReadString());
        writer.Dispose();
    }

    [Fact]
    public void null_string_uses_length_minus_one()
    {
        var writer = MessageBufferWriter.Create();
        writer.WriteString(null);
        Assert.Equal(4, writer.Length);
        Assert.Equal(-1, new MessageBufferReader(writer.WrittenReadOnlySpan).ReadInt32());
        writer.Dispose();
    }

    [Fact]
    public void lone_surrogate_string_is_rejected_on_write()
    {
        // KI-20 regression: surfaces the encoding failure instead of silently replacing lone surrogates with replacement bytes.
        Assert.ThrowsAny<ArgumentException>(WriteLoneSurrogate);
    }

    static void WriteLoneSurrogate()
    {
        var writer = MessageBufferWriter.Create();
        try
        {
            // intentional non-ASCII payload containing a lone surrogate
            writer.WriteString("앞 \uD800 뒤");
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void invalid_utf8_string_payload_is_rejected_on_read()
    {
        // KI-20 regression: length prefix 2 + a 2-byte sequence whose lead byte 0xC2 is followed by non-continuation 0x01 → invalid UTF-8.
        byte[] bytes = { 2, 0, 0, 0, 0xC2, 0x01 };
        Assert.Throws<InvalidDataException>(() => new MessageBufferReader(bytes).ReadString());
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(int.MinValue)]
    public void negative_length_prefix_other_than_minus_one_is_rejected_on_read(int length)
    {
        // KI-6 regression: only -1 maps to null — if other negatives silently decoded as null, corrupted packets would go unnoticed.
        Assert.Throws<InvalidDataException>(() => ReadStringWithLengthPrefix(length));
    }

    static void ReadStringWithLengthPrefix(int length)
    {
        var writer = MessageBufferWriter.Create();
        try
        {
            writer.WriteInt32(length);
            _ = new MessageBufferReader(writer.WrittenReadOnlySpan).ReadString();
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void minus_one_length_prefix_decodes_as_null()
    {
        var writer = MessageBufferWriter.Create();
        writer.WriteInt32(-1);
        Assert.Null(new MessageBufferReader(writer.WrittenReadOnlySpan).ReadString());
        writer.Dispose();
    }

    [Fact]
    public void read_beyond_range_throws_end_of_stream_exception()
    {
        Assert.Throws<EndOfStreamException>(ReadPastEnd);
        Assert.Throws<EndOfStreamException>(ReadBlockPastEnd);
    }

    static void ReadPastEnd()
    {
        var reader = new MessageBufferReader(new byte[] { 1, 2 });
        reader.ReadByte();
        reader.ReadByte();
        reader.ReadByte();
    }

    static void ReadBlockPastEnd()
    {
        var reader = new MessageBufferReader(new byte[] { 1 });
        reader.ReadInt32();
    }

    [Fact]
    public void writer_grows_automatically_when_capacity_is_insufficient()
    {
        var writer = MessageBufferWriter.Create(4);
        for (int i = 0; i < 1000; i++)
        {
            writer.WriteInt32(i);
        }
        Assert.Equal(4000, writer.Length);

        var reader = new MessageBufferReader(writer.WrittenReadOnlySpan);
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(i, reader.ReadInt32());
        }
        writer.Dispose();
    }

    [Fact]
    public void pooledbuffer_provides_span_view_and_copied_array()
    {
        var writer = MessageBufferWriter.Create();
        writer.WriteInt32(77);
        var pooled = writer.ToPooledBuffer();

        Assert.Equal(4, pooled.Length);
        Assert.Equal(77, new MessageBufferReader(pooled.Span).ReadInt32());
        Assert.Equal(4, pooled.ToArray().Length);

        pooled.Dispose();
        Assert.Equal(0, pooled.Length);
        pooled.Dispose(); // idempotent
    }

    [Fact]
    public void decimal_scale_above_28_is_rejected_on_read()
    {
        byte[] bytes = WriteDecimalBytes(12.34m);
        bytes[14] = 78; // scale byte (bits 16–23) set to 78 — a known DecCalc crash range
        Assert.Throws<InvalidDataException>(() => new MessageBufferReader(bytes).ReadDecimal());
    }

    [Fact]
    public void decimal_flags_with_reserved_bits_are_rejected_on_read()
    {
        byte[] bytes = WriteDecimalBytes(12.34m);
        bytes[12] |= 0x01; // set flags bit 0 (reserved)
        Assert.Throws<InvalidDataException>(() => new MessageBufferReader(bytes).ReadDecimal());
    }

    [Fact]
    public void decimal_boundary_scale_28_is_allowed()
    {
        var writer = MessageBufferWriter.Create();
        writer.WriteDecimal(0.0000000000000000000000000001m); // scale 28 (maximum allowed)
        Assert.Equal(0.0000000000000000000000000001m, new MessageBufferReader(writer.WrittenReadOnlySpan).ReadDecimal());
        writer.Dispose();
    }

    static byte[] WriteDecimalBytes(decimal value)
    {
        var writer = MessageBufferWriter.Create();
        writer.WriteDecimal(value);
        byte[] bytes = writer.ToArray();
        writer.Dispose();
        return bytes;
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void negative_skip_is_rejected(int count)
    {
        // KI-21 regression: blocks Skip(-n) from moving the reader backward and breaking the forward-only contract.
        Assert.Throws<ArgumentOutOfRangeException>(() => SkipAfterFourBytes(count));
    }

    static void SkipAfterFourBytes(int count)
    {
        var reader = new MessageBufferReader(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        reader.Skip(4);
        reader.Skip(count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void negative_advance_is_rejected(int count)
    {
        // KI-21 regression: blocks Advance(-n) from rewinding the write position so later writes cannot overwrite existing payload.
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvanceAfterOneByte(count));
    }

    static void AdvanceAfterOneByte(int count)
    {
        var writer = MessageBufferWriter.Create();
        try
        {
            writer.WriteByte(0xAA);
            writer.Advance(count);
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void bytes_consumed_cannot_be_reread_via_negative_skip()
    {
        // Before the fix, Skip(-1) rewound the position without throwing, letting the same byte be consumed twice.
        Assert.False(TryRewindAndReread());
    }

    static bool TryRewindAndReread()
    {
        var reader = new MessageBufferReader(new byte[] { 0xAA, 0xBB });
        reader.ReadByte();
        try
        {
            reader.Skip(-1);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        return reader.ReadByte() == 0xAA; // if it rewound, the same byte is read again
    }

    [Fact]
    public void written_payload_cannot_be_overwritten_via_negative_advance()
    {
        // Before the fix, Advance(-1) shrank the length so the next write would overwrite the first byte.
        Assert.False(TryRewindAndOverwrite());
    }

    static bool TryRewindAndOverwrite()
    {
        var writer = MessageBufferWriter.Create();
        try
        {
            writer.WriteByte(0xAA);
            try
            {
                writer.Advance(-1);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            writer.WriteByte(0xBB);
            return writer.Length == 1 && writer.WrittenSpan[0] == 0xBB; // if it rewound, the first byte gets overwritten
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void only_zero_and_positive_position_advances_are_allowed()
    {
        // Preserves the happy path: Skip(0) and Advance(0) are harmless, and positive advances work as before.
        var writer = MessageBufferWriter.Create();
        writer.WriteInt32(11);
        writer.WriteInt32(22);
        writer.Advance(0);
        Assert.Equal(8, writer.Length);

        var reader = new MessageBufferReader(writer.WrittenReadOnlySpan);
        reader.Skip(0);
        Assert.Equal(11, reader.ReadInt32());
        reader.Skip(4);
        Assert.Equal(0, reader.Remaining);
        writer.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(1024)]
    [InlineData(1_000_000)]
    [InlineData(715_827_881)] // near the largest char count whose encoding upper bound still fits an int
    public void string_buffer_requirement_matches_the_utf8_encoding_upper_bound(int charCount)
    {
        // KI-22 regression: the custom long formula must equal `Encoding.GetMaxByteCount` + the 4-byte length prefix,
        // so it neither narrows the bound (buffer too small) nor wastes memory (over-allocation).
        int maxBytes = StrictUtf8().GetMaxByteCount(charCount);
        Assert.Equal(4L + maxBytes, MessageBufferWriter.GetStringBufferRequirement(charCount));
    }

    [Fact]
    public void string_buffer_requirement_does_not_overflow_past_the_int_limit()
    {
        // KI-22 regression: from 715,827,882 chars the required capacity exceeds int.MaxValue, so int arithmetic cannot even represent it.
        const int charCount = 715_827_882;
        long required = MessageBufferWriter.GetStringBufferRequirement(charCount);
        Assert.True(required > int.MaxValue);                       // exactly representable as a long
        Assert.True(unchecked(4 + (charCount * 3 + 3)) < 0);        // the old int expression overflows negative → growth would be missed
    }

    [Fact]
    public void string_buffer_requirement_is_monotonically_increasing_in_char_count()
    {
        long previous = MessageBufferWriter.GetStringBufferRequirement(0);
        foreach (int charCount in new[] { 1, 1000, 715_827_882, int.MaxValue })
        {
            long current = MessageBufferWriter.GetStringBufferRequirement(charCount);
            Assert.True(current > previous);
            previous = current;
        }
    }

    [Fact]
    public void large_strings_still_grow_normally_and_round_trip()
    {
        // KI-22 happy path: verifies the new long capacity arithmetic did not change the existing growth and write behavior.
        string value = new string('가', 100_000); // U+AC00 → 3 bytes per UTF-8 char (intentional non-ASCII payload)
        var writer = MessageBufferWriter.Create(4);
        writer.WriteString(value);
        Assert.Equal(4 + 300_000, writer.Length);
        Assert.Equal(value, new MessageBufferReader(writer.WrittenReadOnlySpan).ReadString());
        writer.Dispose();
    }

    static Encoding StrictUtf8() =>
        Encoding.GetEncoding(65001, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
}

// ---------- PooledBuffer copy ownership sharing (KI-37) ----------

/// <summary>
/// PooledBuffer is a struct, so every assignment/pass creates a copy. Before the fix, each copy's Dispose was invisible
/// to the others, so the same rented array was returned to the pool twice (the next renter saw someone else's data).
/// All copies now share a reference-type holder: the buffer is returned exactly once, and whichever copy Disposes first
/// leaves the rest looking at an empty view.
/// </summary>
public class PooledBufferCopyOwnershipTests
{
    [Fact]
    public void disposing_each_copy_returns_to_the_pool_exactly_once()
    {
        var writer = MessageBufferWriter.Create();
        writer.WriteInt32(0x0A0B0C0D);
        var original = writer.ToPooledBuffer();
        var copy = original; // struct copy — before the fix, disposing this copy double-returned

        Assert.Equal(4, copy.Length);

        copy.Dispose();

        // Same ownership state: after return, every copy's view is empty.
        Assert.Equal(0, copy.Length);
        Assert.Equal(0, original.Length);
        Assert.True(original.Span.IsEmpty);
        Assert.Empty(original.ToArray());

        original.Dispose(); // already returned — idempotent, no exception
    }

    [Fact]
    public void disposing_the_original_empties_the_copy_views_too()
    {
        var writer = MessageBufferWriter.Create();
        writer.WriteString("data");
        var original = writer.ToPooledBuffer();
        var copy = original;

        original.Dispose();

        Assert.Equal(0, copy.Length);
        Assert.True(copy.Span.IsEmpty);
    }

    [Fact]
    public void serializepooled_copies_share_the_same_ownership()
    {
        var message = new Fixtures.FlatMessage { Value = 77 };
        using var pooled = MessageSerializer.SerializePooled(message);
        var copy = pooled;

        Assert.True(copy.Span.SequenceEqual(pooled.Span));

        var roundTrip = MessageSerializer.Deserialize<Fixtures.FlatMessage>(pooled.Span.ToArray());
        Assert.Equal(77, roundTrip.Value);

        copy.Dispose();
        Assert.Equal(0, pooled.Length); // double Dispose via using is also safe
    }

    [Fact]
    public void topooledbuffer_of_empty_writer_does_not_throw_on_dispose()
    {
        var writer = MessageBufferWriter.Create();
        var pooled = writer.ToPooledBuffer(); // Array.Empty singleton — not a pool-return target

        Assert.Equal(0, pooled.Length);
        Assert.True(pooled.Span.IsEmpty);
        pooled.Dispose();
    }

    [Fact]
    public void negative_getspan_is_rejected_with_contract_exception()
    {
        var writer = MessageBufferWriter.Create();
        writer.WriteInt32(1);
        int positionBefore = writer.Length;

        // The writer is a ref struct — it cannot be captured in a lambda, so the contract exception is checked via try/catch.
        ArgumentOutOfRangeException? exception = null;
        try
        {
            writer.GetSpan(-1);
        }
        catch (ArgumentOutOfRangeException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal("size", exception.ParamName);
        Assert.Equal(positionBefore, writer.Length); // position unchanged — no state corruption
    }

    [Fact]
    public void getspan_happy_path_preserves_forward_writes()
    {
        var writer = MessageBufferWriter.Create();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), 77);

        Assert.Equal(4, writer.Length);
        Assert.Equal(77, new MessageBufferReader(writer.WrittenReadOnlySpan).ReadInt32());
    }
}

// ---------- Create and FromRented contracts (2026-09-08 test-gap batch closure) ----------

/// <summary>Pins the empty-buffer start path and FromRented argument validation (implemented but previously untested).</summary>
public class WriterCreateAndFromRentedContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void create_with_non_positive_initial_capacity_starts_empty_and_grows_on_first_write(int initialCapacity)
    {
        var writer = MessageBufferWriter.Create(initialCapacity);

        Assert.Equal(0, writer.Capacity); // starts on Array.Empty
        writer.WriteInt32(77);
        writer.WriteString("ok");

        Assert.True(writer.Length > 0);
        var reader = new MessageBufferReader(writer.WrittenReadOnlySpan);
        Assert.Equal(77, reader.ReadInt32());
        Assert.Equal("ok", reader.ReadString());
    }

    [Fact]
    public void fromrented_rejects_null_array()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => PooledBuffer.FromRented(null!, 0));
        Assert.Equal("rented", exception.ParamName);
    }

    [Fact]
    public void fromrented_rejects_length_overrun()
    {
        var rented = new byte[8];

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => PooledBuffer.FromRented(rented, 9));

        Assert.Equal("length", exception.ParamName);
    }

    [Fact]
    public void fromrented_rejects_negative_length_too()
    {
        var rented = new byte[8];

        // The (uint)length > (uint)rented.Length comparison catches negatives as huge positives.
        Assert.Throws<ArgumentOutOfRangeException>(() => PooledBuffer.FromRented(rented, -1));
    }
}

/// <summary>
/// The exact-consumption check (<c>DeserializeExact</c>) contract — turns trailing bytes caused by schema drift
/// (an ADR-0006 layout-freeze violation) into InvalidDataException instead of silent data loss.
/// The default Deserialize keeps allowing trailing bytes as transport-layer framing slack (the control group).
/// </summary>
public class DeserializeExactTests
{
    [Fact]
    public void generic_entry_round_trips_a_clean_frame_as_is()
    {
        byte[] frame = MessageSerializer.Serialize(new MessageProtocol.Tests.Fixtures.FlatMessage { Value = 42 });

        var restored = MessageSerializer.DeserializeExact<MessageProtocol.Tests.Fixtures.FlatMessage>(frame);

        Assert.Equal(42, restored.Value);
    }

    [Fact]
    public void generic_entry_rejects_frames_with_remaining_bytes()
    {
        byte[] clean = MessageSerializer.Serialize(new MessageProtocol.Tests.Fixtures.FlatMessage { Value = 7 });
        byte[] padded = new byte[clean.Length + 3];
        clean.CopyTo(padded, 0);
        padded[^1] = 0xFF;

        var exception = Assert.Throws<System.IO.InvalidDataException>(
            () => MessageSerializer.DeserializeExact<MessageProtocol.Tests.Fixtures.FlatMessage>(padded));

        Assert.Contains("trailing", exception.Message);
        // Control group: the default Deserialize keeps allowing trailing slack bytes (transport-layer framing slack).
        Assert.Equal(7, MessageSerializer.Deserialize<MessageProtocol.Tests.Fixtures.FlatMessage>(padded).Value);
    }

    [Fact]
    public void object_dispatch_entry_round_trips_a_clean_frame_as_is()
    {
        byte[] frame = MessageSerializer.Serialize(new MessageProtocol.Tests.Fixtures.FlatMessage { Value = 11 });

        var restored = Assert.IsType<MessageProtocol.Tests.Fixtures.FlatMessage>(MessageSerializer.DeserializeExact(frame));

        Assert.Equal(11, restored.Value);
    }

    [Fact]
    public void object_dispatch_entry_rejects_frames_with_remaining_bytes()
    {
        byte[] clean = MessageSerializer.Serialize(new MessageProtocol.Tests.Fixtures.FlatMessage { Value = 9 });
        byte[] padded = new byte[clean.Length + 1];
        clean.CopyTo(padded, 0);

        var exception = Assert.Throws<System.IO.InvalidDataException>(
            () => MessageSerializer.DeserializeExact(padded));

        Assert.Contains("schema drift", exception.Message);
        Assert.IsType<MessageProtocol.Tests.Fixtures.FlatMessage>(MessageSerializer.Deserialize(padded));
    }

    [Fact]
    public void generic_construction_frames_also_pass_the_exact_consumption_check()
    {
        var envelope = new MessageProtocol.Tests.Fixtures.GenericEnvelope<MessageProtocol.Tests.Fixtures.FlatMessage> { Value = new MessageProtocol.Tests.Fixtures.FlatMessage { Value = 3 }, Note = "n" };
        byte[] frame = MessageSerializer.Serialize(envelope);

        var restored = MessageSerializer.DeserializeExact<MessageProtocol.Tests.Fixtures.GenericEnvelope<MessageProtocol.Tests.Fixtures.FlatMessage>>(frame);

        Assert.Equal(3, restored.Value!.Value);
        Assert.Equal("n", restored.Note);
        // The object dispatch path (generic header routing) follows the same contract.
        Assert.IsType<MessageProtocol.Tests.Fixtures.GenericEnvelope<MessageProtocol.Tests.Fixtures.FlatMessage>>(MessageSerializer.DeserializeExact(frame));
    }

    [Fact]
    public void empty_span_is_rejected_with_argumentexception_not_invaliddataexception()
    {
        // Entry validation (argument errors) is distinct from wire errors (InvalidDataException) — mixing caller-side bugs
        // and malicious frames under one type leaves production server exception filters unable to classify them. Both entries are pinned.
        Assert.Throws<ArgumentException>(
            () => MessageSerializer.DeserializeExact<MessageProtocol.Tests.Fixtures.FlatMessage>(ReadOnlySpan<byte>.Empty));
        Assert.Throws<ArgumentException>(() => MessageSerializer.DeserializeExact(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void nonid_frames_also_pass_the_exact_consumption_check()
    {
        // A NonId frame has a 1-byte header — pins that the remaining-bytes check also works on 1-byte-header frames
        // (KI-41 interaction: the generic entry bypasses the NonId rejection).
        var message = new MessageProtocol.Tests.Fixtures.NoIdMessage { Flag = 7, Note = "nonid" };
        byte[] frame = MessageSerializer.Serialize(message);

        var restored = MessageSerializer.DeserializeExact<MessageProtocol.Tests.Fixtures.NoIdMessage>(frame);

        Assert.Equal(7, restored.Flag);
        Assert.Equal("nonid", restored.Note);

        byte[] padded = new byte[frame.Length + 1];
        frame.CopyTo(padded, 0);

        Assert.Throws<System.IO.InvalidDataException>(
            () => MessageSerializer.DeserializeExact<MessageProtocol.Tests.Fixtures.NoIdMessage>(padded));
    }
}
