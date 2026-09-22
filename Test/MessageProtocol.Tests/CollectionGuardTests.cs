using System.Buffers.Binary;
using MessageProtocol;
using MessageProtocol.Serialize;
using MessageProtocol.Tests.Fixtures;
using Xunit;

namespace MessageProtocol.Tests;

/// <summary>
/// KI-13 regression: when a collection length/count prefix exceeds the remaining bytes, throw before
/// allocating, blocking oversized allocations (OOM DoS) from malicious frames.
/// </summary>
public class CollectionGuardTests
{
    [Fact]
    public void fixed_size_array_length_exceeding_remaining_bytes_throws_before_allocation()
    {
        byte[] bytes = MessageSerializer.Serialize(new AllTypesMessage { Blob = new byte[] { 1, 2, 3 } });
        // Blob length prefix (3) + payload pattern
        int offset = FindPattern(bytes, new byte[] { 3, 0, 0, 0, 1, 2, 3 });
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), int.MaxValue);

        Assert.Throws<EndOfStreamException>(() => MessageSerializer.Deserialize<AllTypesMessage>(bytes));
    }

    [Fact]
    public void fixed_size_list_count_exceeding_remaining_bytes_throws_before_allocation()
    {
        byte[] bytes = MessageSerializer.Serialize(new AllTypesMessage { Samples = new List<double> { 1.5, 2.5 } });
        // count (2) + little-endian bytes of first element 1.5
        int offset = FindPattern(bytes, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xF8, 0x3F });
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), int.MaxValue);

        Assert.Throws<EndOfStreamException>(() => MessageSerializer.Deserialize<AllTypesMessage>(bytes));
    }

    [Fact]
    public void variable_size_array_count_exceeding_remaining_bytes_throws_before_allocation()
    {
        byte[] bytes = MessageSerializer.Serialize(new AllTypesMessage { Tags = new[] { "a" } });
        // count (1) + string "a" pattern (length 1 + 0x61)
        int offset = FindPattern(bytes, new byte[] { 1, 0, 0, 0, 1, 0, 0, 0, (byte)'a' });
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), int.MaxValue);

        Assert.Throws<EndOfStreamException>(() => MessageSerializer.Deserialize<AllTypesMessage>(bytes));
    }

    [Fact]
    public void well_formed_collections_still_round_trip_after_guard_introduction()
    {
        var msg = new AllTypesMessage
        {
            Blob = new byte[] { 1, 2, 3 },
            Samples = new List<double> { 1.5, 2.5 },
            Tags = new[] { "a", "bb" },
            Codes = new List<byte> { 9, 8 },
        };

        var rt = MessageSerializer.Deserialize<AllTypesMessage>(MessageSerializer.Serialize(msg));

        Assert.Equal(msg.Blob, rt.Blob);
        Assert.Equal(msg.Samples, rt.Samples);
        Assert.Equal(msg.Tags, rt.Tags);
        Assert.Equal(msg.Codes, rt.Codes);
    }

    static int FindPattern(byte[] data, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= data.Length; i++)
        {
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        }
        throw new InvalidOperationException("Test fixture wire pattern not found.");
    }
}
