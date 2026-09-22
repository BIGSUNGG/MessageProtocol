using MessageProtocol;
using MessageProtocol.Serialize;
using MessageProtocol.Tests.Fixtures;
using Xunit;

namespace MessageProtocol.Tests;

/// <summary>
/// Pool integrity under write-side exceptions — when a member getter throws mid-serialization, the rented
/// buffer returns to the pool **in a partially written state** (finally-dispose). Any double-return, leak, or
/// dirty-state leakage there would silently corrupt later serializations. Verified at integration level by
/// back-to-back round trips immediately after an abort (real pool cycling, not the sum of unit guards).
/// </summary>
public partial class WriterAbortPoolIntegrityTests
{
    /// <summary>Controllable fixture that throws in the second member getter — aborts with the first member already written.</summary>
    [Message(MessageKind.Standalone, 160)]
    public partial class AbortProbeMessage
    {
        public int First { get; set; }

        public int Boom
        {
            get => throw new InvalidOperationException("getter exploded");
            set { }
        }

        public int Last { get; set; }
    }

    [Fact]
    public void pool_stays_clean_after_serialization_aborted_by_throwing_getter()
    {
        var aborting = new AbortProbeMessage { First = 1, Last = 2 };

        // Abort: the causing exception must propagate as-is (not swallowed or wrapped).
        for (int abort = 0; abort < 20; abort++)
        {
            var propagated = Assert.Throws<InvalidOperationException>(
                () => MessageSerializer.Serialize(aborting));
            Assert.Contains("getter exploded", propagated.Message);

            // Back-to-back round trips right after the abort — even if the partially written pooled buffer
            // is re-rented, results must always be exact.
            for (int followUp = 0; followUp < 10; followUp++)
            {
                int value = abort * 100 + followUp;
                var back = MessageSerializer.Deserialize<FlatMessage>(
                    MessageSerializer.Serialize(new FlatMessage { Value = value }));
                if (back.Value != value)
                {
                    Assert.Fail($"value corrupted {value}→{back.Value} after abort {abort}, follow-up {followUp} — pool integrity broken");
                }
            }
        }
    }

    [Fact]
    public void abort_on_pooledbuffer_path_keeps_subsequent_pooled_round_trips_exact()
    {
        var aborting = new AbortProbeMessage { First = 1, Last = 2 };

        for (int abort = 0; abort < 10; abort++)
        {
            Assert.Throws<InvalidOperationException>(() => MessageSerializer.SerializePooled(aborting));

            using var pooled = MessageSerializer.SerializePooled(new FlatMessage { Value = abort });
            var back = MessageSerializer.Deserialize<FlatMessage>(pooled.Span.ToArray());
            Assert.Equal(abort, back.Value);
        }
    }

    [Fact]
    public void aborts_do_not_poison_the_type_cache()
    {
        // Abort-success cycles on the same type — SerializerCache state must be immutable past registration.
        var aborting = new AbortProbeMessage();
        var working = new AbortProbeMessage { First = 7, Last = 9 };

        for (int i = 0; i < 5; i++)
        {
            Assert.Throws<InvalidOperationException>(() => MessageSerializer.Serialize(aborting));
        }

        // Boom always throws, so a successful round trip of this type is impossible — verify cache immutability
        // with a different type instead: the aborts must not have seeped into another type's cache.
        var back = MessageSerializer.Deserialize<FlatMessage>(
            MessageSerializer.Serialize(new FlatMessage { Value = 42 }));
        Assert.Equal(42, back.Value);
    }
}
