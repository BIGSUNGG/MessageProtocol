using MessageProtocol.Serialize;
using MessageProtocol.Tests.Fixtures;
using Xunit;

namespace MessageProtocol.Tests;

/// <summary>
/// KI-14 regression: nested-object deserialization recursion had no depth limit. A hostile peer could send a small frame of
/// nothing but <c>ReferenceKind.NewObject</c> bytes for a self-referencing message (verified by experiment: 20,005 bytes) and
/// kill the process instantly with a stack overflow (uncatchable — 5,005 bytes survived, 20,005 bytes died).
/// The reader now counts nesting depth, and generated code plus <see cref="MessageSerializer.DeserializeFromReader"/> call
/// Enter/Leave at recursion points, rejecting over-limit input with <see cref="InvalidDataException"/>.
/// </summary>
public class NestingDepthTests
{
    [Fact]
    public void nesting_beyond_the_default_limit_is_rejected_with_invaliddataexception_instead_of_stack_overflow()
    {
        byte[] payload = BuildChainPayload(MessageBufferReader.DefaultMaxNestingDepth + 1);

        var exception = Assert.Throws<InvalidDataException>(
            () => MessageSerializer.Deserialize<ChainMessage>(payload));

        Assert.Contains(MessageBufferReader.DefaultMaxNestingDepth.ToString(), exception.Message);
    }

    [Fact]
    public void object_dispatch_path_applies_the_same_limit()
    {
        byte[] payload = BuildChainPayload(MessageBufferReader.DefaultMaxNestingDepth + 1);

        Assert.Throws<InvalidDataException>(() => MessageSerializer.Deserialize(payload));
    }

    [Fact]
    public void nesting_exactly_at_the_default_limit_decodes_normally()
    {
        int depth = MessageBufferReader.DefaultMaxNestingDepth;
        byte[] payload = BuildChainPayload(depth);

        var roundTrip = MessageSerializer.Deserialize<ChainMessage>(payload);

        Assert.Equal(depth, CountChain(roundTrip));
    }

    [Fact]
    public void raising_the_limit_via_the_reader_constructor_allows_deeper_graphs()
    {
        int depth = 200;
        byte[] payload = BuildChainPayload(depth);
        var reader = new MessageBufferReader(payload, 512);

        var roundTrip = MessageSerializer.Deserialize<ChainMessage>(ref reader);

        Assert.Equal(depth, CountChain(roundTrip));
        // The counter decrements in matching pairs as the recursion unwinds.
        Assert.Equal(0, reader.NestingDepth);
    }

    [Fact]
    public void shallow_but_wide_graphs_do_not_hit_the_limit()
    {
        var message = new WideChainMessage
        {
            Items = Enumerable.Range(0, 500).Select(_ => new ChainMessage()).ToList(),
        };

        var roundTrip = MessageSerializer.Deserialize<WideChainMessage>(MessageSerializer.Serialize(message));

        Assert.Equal(500, roundTrip.Items!.Count);
    }

    [Fact]
    public void existing_self_referencing_graph_round_trips_still_work_after_the_guard()
    {
        var a = new GraphMessage { Label = "a" };
        var b = new GraphMessage { Label = "b" };
        a.Next = b;
        b.Next = a;   // cycle — restored via a back-reference
        a.Other = b;

        var roundTrip = MessageSerializer.Deserialize<GraphMessage>(MessageSerializer.Serialize(a));

        Assert.Equal("b", roundTrip.Next!.Label);
        Assert.True(ReferenceEquals(roundTrip.Next.Next, roundTrip));
        Assert.True(ReferenceEquals(roundTrip.Other, roundTrip.Next));
    }

    [Fact]
    public void enter_is_rejected_at_the_limit_and_leave_never_goes_below_zero()
    {
        var reader = new MessageBufferReader(new byte[8], 2);

        reader.EnterNestedObject();
        reader.EnterNestedObject();
        Assert.Equal(2, reader.NestingDepth);

        // ref struct locals cannot be captured in lambdas, so this verifies directly via try/catch.
        Exception? thrown = null;
        try
        {
            reader.EnterNestedObject();
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        Assert.IsType<InvalidDataException>(thrown);
        Assert.Equal(2, reader.NestingDepth);   // a rejected Enter does not raise the depth

        reader.LeaveNestedObject();
        reader.LeaveNestedObject();
        reader.LeaveNestedObject();             // unpaired call — a negative depth must not disarm the guard
        Assert.Equal(0, reader.NestingDepth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void non_positive_limit_is_rejected_at_reader_construction(int maxNestingDepth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MessageBufferReader(new byte[4], maxNestingDepth));
    }

    // ---------- Write path (KI-25) ----------

    [Fact]
    public void writer_and_reader_default_limits_are_intentionally_identical()
    {
        // With an asymmetry, a frame the sender wrote successfully could not be read by the receiver at default settings.
        Assert.Equal(MessageBufferReader.DefaultMaxNestingDepth, MessageBufferWriter.DefaultMaxNestingDepth);
    }

    [Fact]
    public void deep_chain_serialization_is_rejected_with_invalidoperationexception_instead_of_stack_overflow()
    {
        ChainMessage head = BuildChain(MessageBufferReader.DefaultMaxNestingDepth + 1);

        var exception = Assert.Throws<InvalidOperationException>(() => MessageSerializer.Serialize(head));

        Assert.Contains(MessageBufferWriter.DefaultMaxNestingDepth.ToString(), exception.Message);
    }

    [Fact]
    public void dispatch_member_cyclic_graph_is_written_finitely_via_back_references()
    {
        // Before the KI-9 resolution, dispatch members did not track back-references, so this cycle made write recursion infinitely
        // deep (process death); the KI-25 depth guard only converted that into InvalidOperationException.
        // The caller-side SerializeContext's object-id tracking now propagates into dispatch writes, terminating the cycle's second
        // visit with a back-reference — reference identity is restored with finite wire instead of an exception.
        var envelope = new CommandEnvelope();
        envelope.Command = new WrapCommand { Seq = 1, Inner = envelope };

        byte[] bytes = MessageSerializer.Serialize(envelope);
        var back = MessageSerializer.Deserialize<CommandEnvelope>(bytes)!;

        var wrap = Assert.IsType<WrapCommand>(back.Command);
        Assert.Equal(1L, wrap.Seq);
        // The wrap instance reached via both paths (directly from the root, and around through Inner.Command) is identical.
        Assert.Same(wrap, Assert.IsType<WrapCommand>(wrap.Inner!.Command));
        // Being a cycle, deserializing again yields the same shape (finite and stable).
        var again = MessageSerializer.Deserialize<CommandEnvelope>(MessageSerializer.Serialize(back));
        Assert.IsType<WrapCommand>(again!.Command);
    }

    [Fact]
    public void deep_dispatch_chain_is_rejected_with_invalidoperationexception_instead_of_stack_overflow()
    {
        // The KI-25 guard remains valid: back-reference termination only works for "revisiting an already-registered instance",
        // so a deep dispatch chain where every level is a new instance (no sharing) is still rejected by the depth limit.
        var head = BuildDispatchChain(MessageBufferReader.DefaultMaxNestingDepth + 1);

        var exception = Assert.Throws<InvalidOperationException>(() => MessageSerializer.Serialize(head));

        Assert.Contains(MessageBufferWriter.DefaultMaxNestingDepth.ToString(), exception.Message);
    }

    static CommandEnvelope BuildDispatchChain(int depth)
    {
        // envelope → wrap (dispatch) → envelope → … a new instance per level — a deep chain with no sharing or cycles.
        var tail = new CommandEnvelope();
        for (int i = 0; i < depth; i++)
        {
            tail = new CommandEnvelope { Command = new WrapCommand { Seq = depth - i, Inner = tail } };
        }
        return tail;
    }

    [Fact]
    public void raising_the_writer_limit_writes_deep_chains_and_rereads_with_a_matching_reader_limit()
    {
        int links = 200;
        ChainMessage head = BuildChain(links);

        var writer = MessageBufferWriter.Create(256, 512);
        byte[] bytes;
        try
        {
            MessageSerializer.Serialize(head, ref writer);
            bytes = writer.ToArray();
            Assert.Equal(0, writer.NestingDepth);   // decrements in matching pairs as the recursion unwinds
        }
        finally
        {
            writer.Dispose();
        }

        var reader = new MessageBufferReader(bytes, 512);
        var roundTrip = MessageSerializer.Deserialize<ChainMessage>(ref reader);

        Assert.Equal(links, CountChain(roundTrip));
    }

    [Fact]
    public void shallow_but_wide_graph_serialization_does_not_hit_the_write_limit()
    {
        var message = new WideChainMessage
        {
            Items = Enumerable.Range(0, 500).Select(_ => new ChainMessage()).ToList(),
        };

        var roundTrip = MessageSerializer.Deserialize<WideChainMessage>(MessageSerializer.Serialize(message));

        Assert.Equal(500, roundTrip.Items!.Count);
    }

    [Fact]
    public void writer_enter_is_rejected_at_the_limit_and_leave_never_goes_below_zero()
    {
        var writer = MessageBufferWriter.Create(8, 2);

        writer.EnterNestedObject();
        writer.EnterNestedObject();
        Assert.Equal(2, writer.NestingDepth);

        // ref struct locals cannot be captured in lambdas, so this verifies directly via try/catch.
        Exception? thrown = null;
        try
        {
            writer.EnterNestedObject();
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal(2, writer.NestingDepth);   // a rejected Enter does not raise the depth

        writer.LeaveNestedObject();
        writer.LeaveNestedObject();
        writer.LeaveNestedObject();             // unpaired call — a negative depth must not disarm the guard
        Assert.Equal(0, writer.NestingDepth);
        writer.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void non_positive_limit_is_rejected_at_writer_construction(int maxNestingDepth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageBufferWriter.Create(16, maxNestingDepth));
    }

    /// <summary>Builds a chain of self-references links long (node count is links + 1). Assembled iteratively, without recursion.</summary>
    static ChainMessage BuildChain(int links)
    {
        var head = new ChainMessage();
        var tail = head;
        for (int i = 0; i < links; i++)
        {
            tail.Next = new ChainMessage();
            tail = tail.Next;
        }

        return head;
    }

    /// <summary>
    /// Assembles the hostile frame directly as bytes — building an object graph and serializing it would make the write side
    /// recurse just as deep first, so verifying only the receive path requires raw wire bytes.
    /// 4-byte header + 1 NewObject byte per level + 1 terminating Null byte.
    /// </summary>
    static byte[] BuildChainPayload(int depth)
    {
        byte[] serialized = MessageSerializer.Serialize(new ChainMessage());
        var payload = new byte[MessageWireFormat.IdHeaderSize + depth + 1];
        Array.Copy(serialized, payload, MessageWireFormat.IdHeaderSize);

        int position = MessageWireFormat.IdHeaderSize;
        for (int i = 0; i < depth; i++)
        {
            payload[position++] = (byte)MessageSerializer.ReferenceKind.NewObject;
        }

        payload[position] = (byte)MessageSerializer.ReferenceKind.Null;
        return payload;
    }

    /// <summary>Decoded chain length. Counted iteratively rather than recursively, so the verification itself uses no stack.</summary>
    static int CountChain(ChainMessage? head)
    {
        int count = 0;
        while (head?.Next is not null)
        {
            head = head.Next;
            count++;
        }

        return count;
    }
}
