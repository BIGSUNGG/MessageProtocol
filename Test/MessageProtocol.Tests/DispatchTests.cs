using MessageProtocol;
using MessageProtocol.Serialize;
using MessageProtocol.Tests.Fixtures;
using Xunit;

namespace MessageProtocol.Tests;

public class DispatchTests
{
    [Fact]
    public void object_dispatch_routes_types_by_the_header_message_id()
    {
        object msg = new FlatMessage { Value = 12 };
        byte[] bytes = MessageSerializer.Serialize(msg);

        object? decoded = MessageSerializer.Deserialize(bytes);
        Assert.IsType<FlatMessage>(decoded);
        Assert.Equal(12, ((FlatMessage)decoded).Value);
    }

    [Fact]
    public void polymorphism_serializes_by_runtime_type()
    {
        EventBase e = new LogoutEvent { Timestamp = 5, Reason = 3 };
        byte[] bytes = MessageSerializer.Serialize((object)e);

        object? decoded = MessageSerializer.Deserialize(bytes);
        Assert.IsType<LogoutEvent>(decoded);
        var logout = Assert.IsType<LogoutEvent>(decoded);
        Assert.Equal(5, logout.Timestamp);
        Assert.Equal(3, logout.Reason);
    }

    [Fact]
    public void generic_path_uses_the_declared_type()
    {
        EventBase e = new LogoutEvent { Timestamp = 5, Reason = 3 };

        // Serialize<T>(T=EventBase) → ignores the runtime derived type, serializes as the base
        byte[] bytes = MessageSerializer.Serialize(e);
        Assert.Equal(EventBase.MessageId, ReadMessageId(bytes));
    }

    [Fact]
    public void group_element_types_are_routed_individually()
    {
        object? login = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)new LoginEvent { User = "u" }));
        object? logout = MessageSerializer.Deserialize(MessageSerializer.Serialize((object)new LogoutEvent { Reason = 1 }));

        Assert.IsType<LoginEvent>(login);
        Assert.IsType<LogoutEvent>(logout);
    }

    [Fact]
    public void nonid_is_rejected_on_object_deserialization()
    {
        byte[] bytes = MessageSerializer.Serialize(new NoIdMessage { Flag = 1 });
        // Illegal wire content (NonId flag) maps to InvalidDataException — InvalidCastException misleads about the failure kind
        // before any cast even happened, and dropped out of the trust-boundary rejection classification (2026-09-08 fuzzer, KI-41 family).
        Assert.Throws<System.IO.InvalidDataException>(() => MessageSerializer.Deserialize(bytes));
    }

    [Fact]
    public void unregistered_id_throws_key_not_found()
    {
        // Standalone flag + an id value nobody registered
        byte[] bytes =
        [
            MessageWireFormat.ComposeHeaderByte(MessageFlag.Standalone, 0),
            0x7F, 0xFF, 0xFE,
        ];
        Assert.Throws<KeyNotFoundException>(() => MessageSerializer.Deserialize(bytes));
    }

    [Fact]
    public void too_short_id_data_throws()
    {
        byte[] bytes = [MessageWireFormat.ComposeHeaderByte(MessageFlag.Standalone, 0), 0x00];
        Assert.Throws<ArgumentException>(() => MessageSerializer.Deserialize(bytes));
    }

    [Fact]
    public void serializetowriter_is_used_for_nested_writes()
    {
        var writer = MessageBufferWriter.Create();
        MessageSerializer.SerializeToWriter(new FlatMessage { Value = 21 }, ref writer);

        var decoded = (FlatMessage)MessageSerializer.Deserialize(writer.WrittenReadOnlySpan);
        Assert.Equal(21, decoded.Value);
        writer.Dispose();
    }

    static uint ReadMessageId(byte[] bytes)
    {
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}

public class RegistrationTests
{
    [Fact]
    public void registers_a_manually_implemented_type_via_registertype()
    {
        MessageSerializer.RegisterType(typeof(ManualStandalone));

        var msg = new ManualStandalone { Value = 555 };
        byte[] bytes = MessageSerializer.Serialize(msg);

        Assert.Equal(555, MessageSerializer.Deserialize<ManualStandalone>(bytes).Value);

        var decoded = Assert.IsType<ManualStandalone>(MessageSerializer.Deserialize(bytes));
        Assert.Equal(555, decoded.Value);
    }

    [Fact]
    public void duplicate_registration_throws()
    {
        // FlatMessage is already registered by the module initializer
        Assert.Throws<InvalidOperationException>(() => MessageSerializer.RegisterHasIdMessage<FlatMessage>());
    }

    [Fact]
    public void registering_a_type_without_contract_implementation_throws()
    {
        Assert.Throws<InvalidOperationException>(() => MessageSerializer.RegisterType(typeof(NotAMessage)));
    }

    [Fact]
    public void id_conflicting_registration_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MessageSerializer.RegisterHasIdMessage<FlatMessage>(
                FlatMessage.Serialize,
                FlatMessage.Deserialize,
                LoginEvent.MessageId)); // the id already occupied by LoginEvent
    }

    [Fact]
    public void unregistered_type_object_serialization_attempts_lazy_registration_and_fails()
    {
        Assert.Throws<InvalidOperationException>(() => MessageSerializer.Serialize((object)new NotAMessage()));
    }

    [Fact]
    public void nonid_delegate_registration_never_appears_in_message_id_routing()
    {
        // NoIdMessage is registered by the module initializer — re-registering throws a duplicate exception
        Assert.Throws<InvalidOperationException>(() => MessageSerializer.RegisterNonIdMessage<NoIdMessage>());
    }

    // ---------- Abstract message type members (KI-24) ----------

    [Fact]
    public void abstract_group_root_member_round_trips_as_the_concrete_element()
    {
        var envelope = new CommandEnvelope
        {
            Command = new StartCommand { Seq = 7, Target = "alpha" },
            History = new List<AbstractCommand>
            {
                new StartCommand { Seq = 1, Target = "a" },
                new StopCommand { Seq = 2, Code = 9 },
            },
        };

        var roundTrip = MessageSerializer.Deserialize<CommandEnvelope>(MessageSerializer.Serialize(envelope));

        // Runtime dispatch restores the concrete element type, not the declared type (abstract root), so derived members are not lost.
        var command = Assert.IsType<StartCommand>(roundTrip.Command);
        Assert.Equal(7, command.Seq);          // base (root) member
        Assert.Equal("alpha", command.Target); // derived member

        Assert.Equal(2, roundTrip.History!.Count);
        Assert.Equal("a", Assert.IsType<StartCommand>(roundTrip.History[0]).Target);
        Assert.Equal(9, Assert.IsType<StopCommand>(roundTrip.History[1]).Code);
    }

    [Fact]
    public void null_abstract_group_root_member_round_trips_as_null()
    {
        var roundTrip = MessageSerializer.Deserialize<CommandEnvelope>(
            MessageSerializer.Serialize(new CommandEnvelope()));

        Assert.Null(roundTrip.Command);
        Assert.Null(roundTrip.History);
    }

    [Fact]
    public void messages_with_abstract_group_root_members_also_round_trip_via_object_dispatch()
    {
        object envelope = new CommandEnvelope { Command = new StopCommand { Seq = 3, Code = 5 } };

        var roundTrip = (CommandEnvelope)MessageSerializer.Deserialize(MessageSerializer.Serialize(envelope))!;

        Assert.Equal(3, roundTrip.Command!.Seq);
        Assert.Equal(5, Assert.IsType<StopCommand>(roundTrip.Command).Code);
    }

    [Fact]
    public void concrete_base_member_serializes_by_declared_type_and_loses_derived_members()
    {
        // Pins the current KI-29 behavior: using a **concrete** base (which has derived message types) as a member's static type
        // writes by declared type, silently dropping derived members without exceptions, and the restored type becomes the base
        // (verified by experiment: LoginEvent.User lost in a 13-byte frame). The generator warns about this shape with MSGPROT012;
        // when polymorphism is needed, declare the root abstract and rely on runtime dispatch (KI-24).
        var host = new EventHost { Event = new LoginEvent { Timestamp = 5, User = "kim" } };

        var roundTrip = MessageSerializer.Deserialize<EventHost>(MessageSerializer.Serialize(host));

        Assert.Equal(5, roundTrip.Event!.Timestamp);   // base members are kept
        Assert.IsType<EventBase>(roundTrip.Event);     // restored as a base instance, not the derived one = User is not on the wire
    }

    // ---------- Shared references through dispatch members (KI-9 resolution) ----------

    [Fact]
    public void shared_reference_via_abstract_dispatch_member_restores_reference_identity()
    {
        // When the same instance appears in two abstract members, the second occurrence is written as a back-reference — before the
        // fix, a fresh SerializeContext per dispatch duplicated the full frame, producing two separate instances on the receiving side.
        var shared = new StartCommand { Seq = 42, Target = "t" };
        var envelope = new CommandEnvelope { Command = shared, History = new List<AbstractCommand> { shared } };

        var back = MessageSerializer.Deserialize<CommandEnvelope>(MessageSerializer.Serialize(envelope));

        var command = Assert.IsType<StartCommand>(back!.Command);
        Assert.Equal(42L, command.Seq);
        Assert.Same(back.Command, back.History![0]);
    }

    [Fact]
    public void shared_reference_via_type_parameter_dispatch_member_also_restores_reference_identity()
    {
        var shared = new FlatMessage { Value = 7 };
        var envelope = new GenericEnvelope<FlatMessage> { Value = shared, Items = new List<FlatMessage?> { shared, null } };

        var back = MessageSerializer.Deserialize<GenericEnvelope<FlatMessage>>(MessageSerializer.Serialize(envelope));

        Assert.Equal(7, back!.Value!.Value);
        Assert.Same(back.Value, back.Items![0]);
        Assert.Null(back.Items[1]);   // a non-shared element stays as is
    }

    [Fact]
    public void shared_reference_via_out_of_graph_delegation_member_also_restores_reference_identity()
    {
        // Concrete message members from another assembly (EmitOutOfGraphMessage*) follow the same contract — the other half of KI-9.
        var shared = new MessageProtocol.NetStandardFixtures.FallbackCollections { Bulk = new List<int> { 1, 2 } };
        var host = new SharedOutOfGraphHost { First = shared, Second = shared };

        var back = MessageSerializer.Deserialize<SharedOutOfGraphHost>(MessageSerializer.Serialize(host));

        Assert.Equal(new[] { 1, 2 }, back!.Second!.Bulk!);
        Assert.Same(back.First, back.Second);
    }
}

// ---------- object entry point contract guards (2026-09-08 test-gap batch closure) ----------

/// <summary>Pins the null and unregistered contracts of the object dispatch entry points by executing them (implemented but previously untested).</summary>
public class ObjectEntryGuardTests
{
    [Fact]
    public void serialize_object_rejects_null()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => MessageSerializer.Serialize(null!));
        Assert.Equal("message", exception.ParamName);
    }

    [Fact]
    public void serializepooled_object_rejects_null()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => MessageSerializer.SerializePooled(null!));
        Assert.Equal("message", exception.ParamName);
    }

    [Fact]
    public void serializetowriter_rejects_null()
    {
        var writer = MessageBufferWriter.Create();
        ArgumentNullException? exception = null;
        try
        {
            MessageSerializer.SerializeToWriter(null!, ref writer);
        }
        catch (ArgumentNullException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal("message", exception.ParamName);
    }

    [Fact]
    public void serializetowriter_rejects_unregistered_type_with_registration_guidance()
    {
        // An unregistered type goes through GetWriterInvoker's lazy RegisterType — a type without a message implementation gets the
        // "no IMessageSerializable implementation" guidance; a type with one lazily registers and works (pinned by other tests).
        var writer = MessageBufferWriter.Create();
        InvalidOperationException? exception = null;
        try
        {
            MessageSerializer.SerializeToWriter(new NotAMessage(), ref writer);
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Contains(nameof(NotAMessage), exception.Message);
        Assert.Contains("IMessageSerializable", exception.Message);
        Assert.Equal(0, writer.Length); // rejected before any depth escalation — no state corruption
    }
}
