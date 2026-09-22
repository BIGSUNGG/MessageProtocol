using MessageProtocol.Serialize;
using MessageProtocol.Tests.Fixtures;
using Xunit;

namespace MessageProtocol.Tests;

/// <summary>
/// KI-30 regression: the reference-tracking contexts use `_firstObject is null` as an **empty-slot sentinel**, so
/// registering null left the slot unoccupied and the next object also received id 1 (experimentally confirmed:
/// `RegisterObject(null)` → 1, then `RegisterObject(obj)` → 1 again). The read side mirrored this, so
/// `GetObject(1)` resolved a back-reference to the wrong instance — the object graph corrupted silently with
/// no exception — hence null is rejected at the public boundary.
/// Generated code filters nulls out as `ReferenceKind.Null` first and never hits this path (the contract for
/// hand-written implementations).
/// </summary>
public class ReferenceContextTests
{
    [Fact]
    public void serialize_context_rejects_null_registration()
    {
        var context = default(MessageSerializer.SerializeContext);

        Assert.Throws<ArgumentNullException>(() => context.RegisterObject(null!));
    }

    [Fact]
    public void serialize_context_rejects_null_id_lookup()
    {
        var context = default(MessageSerializer.SerializeContext);

        Assert.Throws<ArgumentNullException>(() => context.TryGetObjectId(null!, out _));
    }

    [Fact]
    public void deserialize_context_rejects_null_registration()
    {
        var context = default(MessageSerializer.DeserializeContext);

        Assert.Throws<ArgumentNullException>(() => context.RegisterNewObject(null!));
    }

    [Fact]
    public void context_remains_unpoisoned_and_usable_after_a_rejection()
    {
        var context = default(MessageSerializer.SerializeContext);
        Assert.Throws<ArgumentNullException>(() => context.RegisterObject(null!));

        var value = new object();

        Assert.Equal(1, context.RegisterObject(value));
        Assert.True(context.TryGetObjectId(value, out int objectId));
        Assert.Equal(1, objectId);
    }

    [Fact]
    public void normal_path_id_assignment_and_promoted_backreference_restoration_still_work()
    {
        // Pins that the guard did not break the empty-slot sentinel or the Dictionary promotion path.
        var first = new object();
        var second = new object();
        var third = new object();

        var write = default(MessageSerializer.SerializeContext);
        Assert.Equal(1, write.RegisterObject(first));
        Assert.Equal(2, write.RegisterObject(second));   // promotes to Dictionary on the second registration
        Assert.Equal(3, write.RegisterObject(third));

        Assert.True(write.TryGetObjectId(first, out int firstId));
        Assert.Equal(1, firstId);
        Assert.True(write.TryGetObjectId(third, out int thirdId));
        Assert.Equal(3, thirdId);
        Assert.False(write.TryGetObjectId(new object(), out _));

        var read = default(MessageSerializer.DeserializeContext);
        Assert.Equal(1, read.RegisterNewObject(first));
        Assert.Equal(2, read.RegisterNewObject(second));
        Assert.Equal(3, read.RegisterNewObject(third));

        Assert.Same(first, read.GetObject(1));
        Assert.Same(third, read.GetObject(3));
    }
}

// ---------- Back-reference reads with base-type member sharing (audit ledger HIGH — 2026-09-07 experiment & mitigation pin) ----------

public class SharedBaseBackReferenceTests
{
    [Fact]
    public void instance_shared_across_base_and_derived_members_is_rejected_with_informative_invaliddatexception()
    {
        // Experiment (2026-09-07, 2.2.0 generated code): when a concrete base member (EventBase) wrote only the
        // base fields and registered the instance first, the back-reference read for the derived member
        // (LoginEvent) cast the registered EventBase instance to LoginEvent. Pre-fix this surfaced as an
        // InvalidCastException with no cause — now an InvalidDataException explains the situation and the fix.
        var login = new LoginEvent { Timestamp = 5, User = "kim" };
        var host = new SharedBaseDerivedHost { First = login, Second = login };

        var bytes = MessageSerializer.Serialize(host);
        var exception = Assert.Throws<System.IO.InvalidDataException>(
            () => MessageSerializer.Deserialize<SharedBaseDerivedHost>(bytes));

        Assert.Contains("EventBase", exception.Message);
        Assert.Contains("LoginEvent", exception.Message);
        Assert.Contains(nameof(SharedBaseDerivedHost.Second), exception.Message);
        Assert.Contains("less derived", exception.Message);
    }

    [Fact]
    public void sharing_across_two_base_members_restores_via_silent_type_narrowing_current_behavior_pinned()
    {
        // KI-34 constraint pin: when both members are of base type the frame round-trips without exception,
        // but the derived field (User) is lost and both members share the same **base** instance. A full fix
        // needs a wire change (nested message dispatch) and is a policy decision — for polymorphism, declare
        // the root abstract and send via runtime dispatch (see the MSGPROT012 guidance).
        var login = new LoginEvent { Timestamp = 5, User = "kim" };
        var host = new SharedBaseBaseHost { First = login, Second = login };

        var back = MessageSerializer.Deserialize<SharedBaseBaseHost>(MessageSerializer.Serialize(host));

        Assert.Equal(5L, back.First!.Timestamp);          // base fields survive
        Assert.IsType<EventBase>(back.First);             // restored as the base, not the derived instance = User lost
        Assert.Same(back.First, back.Second);             // reference identity preserved (2.2.0, KI-9)
    }

    [Fact]
    public void instance_shared_across_dispatch_and_concrete_members_is_restored_including_derived_fields()
    {
        // Control group: when the first occurrence goes through runtime dispatch (abstract member), the
        // concrete type is written with its header, so every later member's back-reference receives the full
        // concrete instance — the healthy combination of KI-24 dispatch and KI-9 reference tracking.
        var start = new StartCommand { Seq = 9, Target = "t" };
        var host = new SharedDispatchConcreteHost { Command = start, Concrete = start };

        var back = MessageSerializer.Deserialize<SharedDispatchConcreteHost>(MessageSerializer.Serialize(host));

        var command = Assert.IsType<StartCommand>(back.Command);
        Assert.Equal(9L, command.Seq);
        Assert.Equal("t", command.Target);
        Assert.Same(back.Command, back.Concrete);
    }
}


// ---------- Unknown reference tag rejection (trust boundary — 2026-09-08 audit) ----------

/// <summary>
/// By spec the reference tag byte is only 0 (Null), 1 (NewObject), or 2 (BackReference). Pre-fix generated code
/// interpreted every other value (3–255) silently as NewObject, parsing corrupted/tampered frames (a frame
/// desynchronized by an attacker is reconstituted as attacker-shaped objects). All three read paths (in-graph,
/// out-of-graph delegation, runtime dispatch) now reject immediately with InvalidDataException.
/// </summary>
public class UnknownReferenceKindTests
{
    static byte[] SerializeWithLeadingReferenceMember<T>(T host) where T : class
    {
        var bytes = MessageSerializer.Serialize(host);
        // The first byte after the root header (4-byte embedded id) is the first reference member's tag
        // (NewObject = 1) — layout pin.
        Assert.True(bytes.Length > 4, "Serialized payload is too short to contain a reference tag.");
        Assert.Equal((byte)MessageSerializer.ReferenceKind.NewObject, bytes[4]);
        return bytes;
    }

    [Fact]
    public void unknown_reference_tag_on_in_graph_member_is_rejected_immediately()
    {
        var host = new SharedBaseBaseHost { First = new LoginEvent { Timestamp = 1, User = "u" } };
        var bytes = SerializeWithLeadingReferenceMember(host);

        foreach (byte hostile in new byte[] { 3, 0xFF })
        {
            bytes[4] = hostile;
            var exception = Assert.Throws<System.IO.InvalidDataException>(
                () => MessageSerializer.Deserialize<SharedBaseBaseHost>(bytes));
            Assert.Contains($"Unknown reference kind {hostile}", exception.Message);
        }
    }

    [Fact]
    public void unknown_reference_tag_on_out_of_graph_delegated_member_is_rejected_immediately()
    {
        var host = new SharedOutOfGraphHost { First = new MessageProtocol.NetStandardFixtures.FallbackCollections() };
        var bytes = SerializeWithLeadingReferenceMember(host);

        bytes[4] = 3;
        var exception = Assert.Throws<System.IO.InvalidDataException>(
            () => MessageSerializer.Deserialize<SharedOutOfGraphHost>(bytes));
        Assert.Contains("Unknown reference kind 3", exception.Message);
    }

    [Fact]
    public void unknown_reference_tag_on_runtime_dispatch_member_is_rejected_immediately()
    {
        var host = new SharedDispatchConcreteHost { Command = new StartCommand { Seq = 1, Target = "t" } };
        var bytes = SerializeWithLeadingReferenceMember(host);

        bytes[4] = 3;
        var exception = Assert.Throws<System.IO.InvalidDataException>(
            () => MessageSerializer.Deserialize<SharedDispatchConcreteHost>(bytes));
        Assert.Contains("Unknown reference kind 3", exception.Message);
    }

    [Fact]
    public void legitimate_tags_still_round_trip()
    {
        // Pins that the guard does not break legitimate frames (0/1/2) — one round trip per read path.
        var host = new SharedDispatchConcreteHost { Command = new StartCommand { Seq = 7, Target = "t" } };
        var back = MessageSerializer.Deserialize<SharedDispatchConcreteHost>(MessageSerializer.Serialize(host));
        Assert.Equal(7L, Assert.IsType<StartCommand>(back.Command).Seq);
    }
}
