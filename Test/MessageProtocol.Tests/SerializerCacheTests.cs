using MessageProtocol.Serialize;
using MessageProtocol.Tests.Fixtures;
using Xunit;

namespace MessageProtocol.Tests;

/// <summary>
/// KI-11 regression: blocks the two ways <see cref="MessageSerializer"/>'s per-type static cache (`SerializerCache{T}`)
/// could become **permanently** broken by a registration-time problem.
/// ① If the cache cctor threw on reflection failure, the CLR cached that failure per type — even after delegate registration
/// later succeeded, the type kept failing with `TypeInitializationException` forever. ② The cctor fields were readonly, so if
/// early access ran the cctor before registration, Prefill was ignored forever. The cctor now never throws (unresolved = null),
/// and registration repairs the cache by filling it directly.
/// </summary>
public class SerializerCacheTests
{
    [Fact]
    public void early_access_to_a_type_without_contract_members_throws_a_clear_exception_not_a_permanent_initialization_failure()
    {
        // Before the fix: the cctor threw and the CLR cached it → TypeInitializationException (that type failed forever after).
        var exception = Assert.Throws<InvalidOperationException>(
            () => MessageSerializer.Serialize(new UnregisteredContractMessage { Value = 1 }));

        Assert.Contains(nameof(UnregisteredContractMessage), exception.Message);
        Assert.Contains("Serialize", exception.Message);

        // Touching the same type again yields the same guidance exception, not an initialization failure = no state corruption.
        Assert.Throws<InvalidOperationException>(
            () => MessageSerializer.Serialize(new UnregisteredContractMessage { Value = 2 }));
    }

    [Fact]
    public void early_access_running_the_cctor_first_still_recovers_via_later_delegate_registration()
    {
        // 1) Early access before registration — the cache cctor takes the reflection path and fills nothing.
        Assert.Throws<InvalidOperationException>(
            () => MessageSerializer.Serialize(new LateBoundMessage { Value = 1 }));

        // 2) Then delegate registration. Before the fix this also failed forever (cctor cannot re-run + readonly fields).
        MessageSerializer.RegisterNonIdMessage<LateBoundMessage>(
            static (LateBoundMessage message, ref MessageBufferWriter writer) => writer.WriteInt32(message.Value),
            static (ref MessageBufferReader reader) => new LateBoundMessage { Value = reader.ReadInt32() });

        // 3) Both the generic hot path and the object dispatch path must actually work.
        var roundTrip = MessageSerializer.Deserialize<LateBoundMessage>(
            MessageSerializer.Serialize(new LateBoundMessage { Value = 7 }));
        Assert.Equal(7, roundTrip.Value);

        var viaDispatch = MessageSerializer.Deserialize<LateBoundMessage>(
            MessageSerializer.Serialize((object)new LateBoundMessage { Value = 9 }));
        Assert.Equal(9, viaDispatch.Value);
    }

    [Fact]
    public void reflection_registration_of_a_type_without_contract_members_fails_at_registration_time_not_with_a_later_null_delegate()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => MessageSerializer.RegisterNonIdMessage<UnregisteredContractMessage>());

        Assert.Contains(nameof(UnregisteredContractMessage), exception.Message);
    }

    [Fact]
    public void reflection_registration_of_manually_implemented_types_still_works()
    {
        // Reverse guard: making the cctor non-throwing must not weaken the normal reflection path.
        byte[] bytes = MessageSerializer.Serialize(new ManualStandalone { Value = 42 });

        var roundTrip = MessageSerializer.Deserialize<ManualStandalone>(bytes);

        Assert.Equal(42, roundTrip.Value);
    }

    // ---------- KI-11 residue: cache leftovers from rejected registrations (resolved 2026-09-07) ----------

    [Fact]
    public void rejected_hasid_registration_leaves_no_colliding_message_id_in_the_serializer_cache()
    {
        // Before the fix: prefill ran before registration validation, so a rejected registration's MessageId/HasId stayed in the cache
        // permanently; re-registering with the correct id later skipped the repair block (Serialize is null), leaving the wrong MessageId
        // (RegisterGenericConstruction assembles runtime keys from the cache's MessageId, so the corruption escalated into key collisions).
        Assert.Throws<InvalidOperationException>(() =>
            MessageSerializer.RegisterHasIdMessage<ManualIdMessage>(
                ManualIdMessage.Serialize, ManualIdMessage.Deserialize, FlatMessage.MessageId)); // an already-occupied id

        // The rejection did not corrupt the cache — this access may run the cctor, but it fills only the type's own MessageId.
        Assert.Equal(ManualIdMessage.MessageId, MessageSerializer.SerializerCache<ManualIdMessage>.MessageId);

        // Re-registering with the correct id succeeds, and the object dispatch round trip works.
        MessageSerializer.RegisterHasIdMessage<ManualIdMessage>(
            ManualIdMessage.Serialize, ManualIdMessage.Deserialize, ManualIdMessage.MessageId);
        Assert.Equal(ManualIdMessage.MessageId, MessageSerializer.SerializerCache<ManualIdMessage>.MessageId);

        var roundTrip = (ManualIdMessage)MessageSerializer.Deserialize(
            MessageSerializer.Serialize((object)new ManualIdMessage { Value = 7 }));
        Assert.Equal(7, roundTrip.Value);
    }

    [Fact]
    public void hasid_registration_with_the_nonid_bit_set_is_rejected_at_registration_time_not_silently_half_registered()
    {
        // Before the fix: RegisterCore silently skipped the MessageId·reader registration, leaving only object serialization working,
        // and a later Deserialize(object) failed with a KeyNotFoundException that gave no cause (audit ledger LOW).
        uint nonIdFlagged = MessageProtocol.MessageWireFormat.ComposeMessageId(
            MessageProtocol.MessageFlag.NonIdMessage, 0, 777);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MessageSerializer.RegisterHasIdMessage<ManualFlagProbeMessage>(
                ManualFlagProbeMessage.Serialize, ManualFlagProbeMessage.Deserialize, nonIdFlagged));

        Assert.Contains("NonId", exception.Message);
        Assert.Contains(nameof(MessageSerializer.RegisterNonIdMessage), exception.Message);
    }

    // ---------- RegisterGenericConstruction publish order (resolved 2026-09-07) ----------

    [Fact]
    public void registergenericconstruction_publishes_classid_before_writer()
    {
        // Audit ledger MEDIUM: with the old order (writer → reader → classId), a Serialize entering via object dispatch after the writer
        // dispatch became visible but before the classId write read GetGenericClassId=0 and threw an unexplained
        // "not registered" exception. The moment a sampler can see the writer, it must also see the classId.
        var violations = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var stop = new ManualResetEventSlim(false);
        var envelope = new GenericEnvelope<ChainMessage> { Value = new ChainMessage() };
        Type constructionType = typeof(GenericEnvelope<ChainMessage>);

        var samplers = Enumerable.Range(0, 3).Select(_ => new Thread(() =>
        {
            while (!stop.IsSet)
            {
                try
                {
                    MessageSerializer.Serialize((object)envelope);
                }
                catch (Exception ex)
                {
                    // Unlike the normal pre-publish failure (unregistered · generic-flag guidance), the classId=0 race appears
                    // only as the generated code's dedicated message — observing it means a publish-order violation.
                    if (ex.Message.Contains("This generic construction is not registered for serialization"))
                    {
                        violations.Enqueue(ex);
                    }
                }
            }
        })).ToArray();

        foreach (var sampler in samplers) sampler.Start();
        MessageSerializer.RegisterGenericConstruction<GenericEnvelope<ChainMessage>>(9);
        Thread.SpinWait(500_000);   // keep up the pressure after registration completes — no violation may appear in the completed state
        stop.Set();
        foreach (var sampler in samplers) sampler.Join();

        Assert.Empty(violations);
        Assert.Equal(9u, MessageSerializer.GetGenericClassId<GenericEnvelope<ChainMessage>>());
        var back = (GenericEnvelope<ChainMessage>)MessageSerializer.Deserialize(
            MessageSerializer.Serialize((object)new GenericEnvelope<ChainMessage> { Value = new ChainMessage() }));
        Assert.NotNull(back.Value);
    }

    [Fact]
    public void registergenericconstruction_failure_rolls_back_the_classid_too()
    {
        // Pre-claims the (MessageId, ClassId) reader key to force a failure at the reader-registration step — verifies that the
        // rollback of the relocated publish order (classId first) also reverts the classId. A missed rollback would let later
        // retries succeed with a wrong classId.
        // Claim: GenericEnvelope<FlatMessage> is registered with ClassId=1 (module init) — the same (messageId, 1) key
        // forces the failure at the reader-registration step. Verifies the relocated order's rollback reverts the classId too.
        Assert.Throws<InvalidOperationException>(() =>
            MessageSerializer.RegisterGenericConstruction<GenericEnvelope<MemberControlMessage>>(1));

        // Since it failed, the classId must not be recorded either.
        Assert.Equal(0u, MessageSerializer.GetGenericClassId<GenericEnvelope<MemberControlMessage>>());
    }
}

// ---------- Concurrent registration races (KI-38) ----------

/// <summary>
/// When registration ran as validate→prefill→claim, concurrently registering the same type with different delegates let both
/// threads reach prefill, overwrite the authoritative SerializerCache&lt;T&gt;, and only the TryAdd loser fail — the loser's
/// delegates (or an A/B mix) lingered, quietly running a serializer from a rejected registration. Claim preemption (the loser
/// throws before prefill) makes that impossible.
/// </summary>
public class RegistrationRaceTests
{
    [Fact]
    public void concurrently_registering_the_same_type_with_different_delegates_fails_exactly_one_side_and_the_cache_holds_only_the_winner()
    {
        var barrier = new Barrier(2);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        void Register(int offset)
        {
            barrier.SignalAndWait(10_000);
            try
            {
                MessageSerializer.RegisterHasIdMessage<ManualRaceMessage>(
                    (message, ref writer) =>
                    {
                        uint id = ManualRaceMessage.MessageId;
                        writer.WriteByte((byte)(id >> 24));
                        writer.WriteByte((byte)(id >> 16));
                        writer.WriteByte((byte)(id >> 8));
                        writer.WriteByte((byte)id);
                        writer.WriteInt32(message.Value + offset);
                    },
                    (ref MessageBufferReader reader) =>
                    {
                        reader.Skip(MessageProtocol.MessageWireFormat.IdHeaderSize);
                        return new ManualRaceMessage { Value = reader.ReadInt32() - offset };
                    },
                    ManualRaceMessage.MessageId);
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        }

        var first = Task.Run(() => Register(0));
        var second = Task.Run(() => Register(1_000));
        Task.WaitAll(first, second);

        // Exactly one side gets "already registered" — observing a different exception type means the registration itself is corrupted.
        var failure = Assert.Single(failures);
        var invalid = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("already registered", invalid.Message);

        // The cache holds only the winner's delegate pair — an A(serialize)+B(deserialize) mix would skew values.
        var back = MessageSerializer.Deserialize<ManualRaceMessage>(
            MessageSerializer.Serialize(new ManualRaceMessage { Value = 77 }));
        Assert.Equal(77, back.Value);
    }

    [Fact]
    public void registration_failed_by_validation_rolls_back_the_claim_so_reregistration_is_possible()
    {
        uint flatMessageId = Fixtures.FlatMessage.MessageId; // the wire id already occupied by a registered type

        // The claim happens before validation — on rejection the claim must roll back too, leaving nothing behind.
        var rejected = Assert.Throws<InvalidOperationException>(() =>
            MessageSerializer.RegisterHasIdMessage<ManualRollbackMessage>(
                (message, ref writer) => { },
                (ref reader) => new ManualRollbackMessage(),
                flatMessageId));
        Assert.Contains("already registered", rejected.Message);

        // If the rejected attempt's claim lingered, this re-registration would be blocked by "already registered".
        uint ownId = ManualRollbackMessage.MessageId;
        MessageSerializer.RegisterHasIdMessage<ManualRollbackMessage>(
            (message, ref writer) =>
            {
                uint id = ownId;
                writer.WriteByte((byte)(id >> 24));
                writer.WriteByte((byte)(id >> 16));
                writer.WriteByte((byte)(id >> 8));
                writer.WriteByte((byte)id);
            },
            (ref reader) =>
            {
                reader.Skip(MessageProtocol.MessageWireFormat.IdHeaderSize);
                return new ManualRollbackMessage();
            },
            ownId);
    }
}

// ---------- Entry point contract guards (2026-09-08 test-gap batch closure) ----------

/// <summary>
/// The null guards on the serialization entry points were implemented but never tested anywhere (2026-09-08 audit) —
/// pins the contract (ArgumentNullException + ParamName "message") by executing it.
/// </summary>
public class SerializeEntryGuardTests
{
    [Fact]
    public void generic_serialize_with_ref_writer_rejects_null_message()
    {
        var writer = MessageBufferWriter.Create();
        ArgumentNullException? exception = null;
        try
        {
            MessageSerializer.Serialize<Fixtures.FlatMessage>(null!, ref writer);
        }
        catch (ArgumentNullException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Equal("message", exception.ParamName);
        Assert.Equal(0, writer.Length); // no state corruption
    }

    [Fact]
    public void generic_serialize_rejects_null_message()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => MessageSerializer.Serialize<Fixtures.FlatMessage>(null!));

        Assert.Equal("message", exception.ParamName);
    }

    [Fact]
    public void serializepooled_rejects_null_message()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => MessageSerializer.SerializePooled<Fixtures.FlatMessage>(null!));

        Assert.Equal("message", exception.ParamName);
    }
}
