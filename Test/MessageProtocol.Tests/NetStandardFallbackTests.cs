using System.Buffers.Binary;
using System.Runtime.Versioning;
using MessageProtocol.NetStandardFixtures;
using MessageProtocol.Serialize;
using Xunit;

namespace MessageProtocol.Tests;

/// <summary>
/// Verifies generated code from the netstandard2.1 (Unity-compatible profile) assembly **by execution**.
/// That target lacks `CollectionsMarshal`, so the generator emits a fallback variant (indexer loop) instead of
/// the fast `List&lt;T&gt;` path — but because this repo's tests run on net8.0/net9.0, only the fast path ever
/// executed and the fallback path was covered by generated-text assertions alone. These tests pin, by running
/// them, that collection round trips, allocation guards (KI-17), and nesting depth guards (KI-14 read, KI-25
/// write) really work on the fallback path.
/// </summary>
public class NetStandardFallbackTests
{
    [Fact]
    public void fixture_assembly_targets_the_netstandard2_1_profile()
    {
        // If this assembly ever moves to a different TFM it gains CollectionsMarshal and the fallback-path
        // coverage silently disappears — pin the profile itself so coverage loss surfaces as a failure.
        var framework = typeof(FallbackCollections).Assembly
            .GetCustomAttributes(typeof(TargetFrameworkAttribute), false)
            .Cast<TargetFrameworkAttribute>()
            .Single();

        Assert.Equal(".NETStandard,Version=v2.1", framework.FrameworkName);
    }

    [Fact]
    public void all_five_fallback_collection_shapes_round_trip()
    {
        var message = new FallbackCollections
        {
            Bulk = new List<int> { 1, 2, 3 },
            // intentional non-ASCII payload: exercises UTF-8 round-trip
            Texts = new List<string> { "a", "bb", "한글" },
            Codes = new List<byte> { 9, 8, 7 },
            Tags = new[] { "x", "y" },
            Samples = new[] { 1.5, -2.5 },
        };

        var roundTrip = MessageSerializer.Deserialize<FallbackCollections>(MessageSerializer.Serialize(message));

        Assert.Equal(message.Bulk, roundTrip.Bulk);
        Assert.Equal(message.Texts, roundTrip.Texts);
        Assert.Equal(message.Codes, roundTrip.Codes);
        Assert.Equal(message.Tags, roundTrip.Tags);
        Assert.Equal(message.Samples, roundTrip.Samples);
    }

    [Fact]
    public void null_and_empty_collection_contract_holds_on_fallback_path()
    {
        var nulls = MessageSerializer.Deserialize<FallbackCollections>(
            MessageSerializer.Serialize(new FallbackCollections()));

        Assert.Null(nulls.Bulk);
        Assert.Null(nulls.Texts);
        Assert.Null(nulls.Codes);
        Assert.Null(nulls.Tags);
        Assert.Null(nulls.Samples);

        var empties = MessageSerializer.Deserialize<FallbackCollections>(MessageSerializer.Serialize(new FallbackCollections
        {
            Bulk = new List<int>(),
            Texts = new List<string>(),
            Codes = new List<byte>(),
            Tags = Array.Empty<string>(),
            Samples = Array.Empty<double>(),
        }));

        Assert.Empty(empties.Bulk!);
        Assert.Empty(empties.Texts!);
        Assert.Empty(empties.Codes!);
        Assert.Empty(empties.Tags!);
        Assert.Empty(empties.Samples!);
    }

    [Fact]
    public void deserialize_exact_round_trip_works_on_fallback_path()
    {
        // KI-43 entry guard: the full-consumption check entry point must round-trip correctly on Unity
        // (netstandard2.1) fallback generated code too.
        var message = new FallbackCollections
        {
            Bulk = new List<int> { 1, 2, 3 },
            Texts = new List<string> { "a", "bb" },
            Tags = new[] { "x", "y" },
        };

        var exact = MessageSerializer.DeserializeExact<FallbackCollections>(MessageSerializer.Serialize(message));

        Assert.Equal(message.Bulk, exact.Bulk);
        Assert.Equal(message.Texts, exact.Texts);
        Assert.Equal(message.Tags, exact.Tags);
    }

    [Fact]
    public void deserialize_exact_rejects_trailing_bytes_on_fallback_path()
    {
        // KI-43 entry guard: bytes left over after reading with fallback generated code (a schema-drifted
        // frame) must be rejected with InvalidDataException instead of being silently dropped — same on the
        // Unity profile.
        byte[] bytes = MessageSerializer.Serialize(new FallbackCollections { Bulk = new List<int> { 1, 2, 3 } });
        byte[] padded = bytes.Concat(new byte[2]).ToArray();

        Assert.Throws<InvalidDataException>(() => MessageSerializer.DeserializeExact<FallbackCollections>(padded));
    }

    [Fact]
    public void fallback_list_bulk_allocation_guard_executes()
    {
        // KI-17: List<T> bulk reads on CollectionsMarshal-less targets must verify `count × elementSize ≤
        // Remaining` before allocating. This was previously covered only by emitter-text assertions; here we
        // confirm by execution that the guard actually throws.
        byte[] bytes = MessageSerializer.Serialize(new FallbackCollections { Bulk = new List<int> { 1, 2, 3 } });

        int offset = FindPattern(bytes, new byte[] { 3, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0 });
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), int.MaxValue);

        Assert.Throws<EndOfStreamException>(() => MessageSerializer.Deserialize<FallbackCollections>(bytes));
    }

    [Fact]
    public void write_side_nesting_depth_guard_executes_on_fallback_path()
    {
        FallbackNode shallow = BuildChain(10);
        Assert.Equal(10, CountChain(MessageSerializer.Deserialize<FallbackNode>(MessageSerializer.Serialize(shallow))));

        // KI-25: a self-referencing chain beyond the default limit (64) must be rejected with an exception
        // instead of a stack overflow — fallback generated code included.
        FallbackNode tooDeep = BuildChain(MessageBufferWriter.DefaultMaxNestingDepth + 1);
        Assert.Throws<InvalidOperationException>(() => MessageSerializer.Serialize(tooDeep));
    }

    [Fact]
    public void read_side_nesting_depth_guard_executes_on_fallback_path()
    {
        // Build a 100-level frame by raising only the write limit, then read it with the default-limit (64)
        // reader — it must be rejected (KI-14).
        FallbackNode head = BuildChain(100);
        var writer = MessageBufferWriter.Create(256, 512);
        byte[] bytes;
        try
        {
            MessageSerializer.Serialize(head, ref writer);
            bytes = writer.ToArray();
        }
        finally
        {
            writer.Dispose();
        }

        Assert.Throws<InvalidDataException>(() => MessageSerializer.Deserialize<FallbackNode>(bytes));

        // Raising both limits decodes the same frame fine.
        var reader = new MessageBufferReader(bytes, 512);
        Assert.Equal(100, CountChain(MessageSerializer.Deserialize<FallbackNode>(ref reader)));
    }

    static FallbackNode BuildChain(int links)
    {
        var head = new FallbackNode { Label = "n0" };
        FallbackNode tail = head;
        for (int i = 1; i <= links; i++)
        {
            tail.Next = new FallbackNode { Label = "n" + i.ToString() };
            tail = tail.Next;
        }

        return head;
    }

    static int CountChain(FallbackNode? head)
    {
        int count = 0;
        while (head?.Next is not null)
        {
            head = head.Next;
            count++;
        }

        return count;
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
