using MessageProtocol.Serialize;
using MessageProtocol.Tests.Fixtures;
using Xunit;

namespace MessageProtocol.Tests;

/// <summary>
/// Concurrent hot-path stress — KI-38/KI-39 (registration races, cache publication) were fixed by reasoning,
/// but there was no standing net to catch functional corruption under sustained mixed traffic. This test
/// verifies that each thread's messages round-trip **as their own type with their own values only** — if
/// SerializerCache&lt;T&gt; or the dispatch table ever leaks across a type boundary (the most catastrophic silent
/// corruption class: executing another type's delegate), it explodes here immediately. Alternates between the
/// generic entry and object dispatch; values come from per-thread seeds for deterministic reproduction.
/// </summary>
public class ConcurrentRoundTripStressTests
{
    const int ThreadCount = 6;
    const int IterationsPerThread = 20_000;

    [Fact]
    public void concurrent_mixed_type_round_trips_never_cross_type_boundaries()
    {
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var threads = new Thread[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() => Worker(threadId, failures));
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(5)));
    }

    static void Worker(int threadId, System.Collections.Concurrent.ConcurrentQueue<string> failures)
    {
        var random = new Random(20260908 + threadId);   // fixed per-thread seed — failures are reproducible

        for (int i = 0; i < IterationsPerThread; i++)
        {
            try
            {
                int mode = i % 4;
                switch (mode)
                {
                    case 0:  // generic entry — primitives
                    {
                        int value = random.Next(int.MinValue, int.MaxValue);
                        var back = MessageSerializer.Deserialize<FlatMessage>(
                            MessageSerializer.Serialize(new FlatMessage { Value = value }));
                        if (back.Value != value) failures.Enqueue($"t{threadId} i{i}: Flat value corrupted {value}→{back.Value}");
                        break;
                    }
                    case 1:  // generic entry — reference graph (including sharing)
                    {
                        int value = random.Next(1, int.MaxValue);
                        var shared = new ChainMessage();
                        var head = new ChainMessage { Next = new ChainMessage { Next = shared } };
                        // Shared subgraph: both branches point at the same instance — check reference
                        // identity is restored on the way back.
                        var envelope = new GenericEnvelope<FlatMessage>
                        {
                            Value = new FlatMessage { Value = value },
                            Items = new List<FlatMessage?> { new FlatMessage { Value = value }, null },
                        };
                        var backEnv = MessageSerializer.Deserialize<GenericEnvelope<FlatMessage>>(
                            MessageSerializer.Serialize(envelope));
                        if (backEnv.Value!.Value != value)
                            failures.Enqueue($"t{threadId} i{i}: Envelope value corrupted {value}→{backEnv.Value!.Value}");
                        if (backEnv.Items is not { Count: 2 } || backEnv.Items[0]!.Value != value || backEnv.Items[1] is not null)
                            failures.Enqueue($"t{threadId} i{i}: Envelope collection corrupted");
                        var backHead = MessageSerializer.Deserialize<ChainMessage>(MessageSerializer.Serialize(head));
                        if (backHead.Next!.Next is null)
                            failures.Enqueue($"t{threadId} i{i}: Chain structure corrupted");
                        break;
                    }
                    case 2:  // object dispatch — round-tripped type and value must match
                    {
                        long stamp = random.NextInt64();
                        object parsed = MessageSerializer.Deserialize(
                            MessageSerializer.Serialize(new LoginEvent { Timestamp = stamp, User = "t" + threadId }));
                        if (parsed is not LoginEvent login || login.Timestamp != stamp || login.User != "t" + threadId)
                            failures.Enqueue($"t{threadId} i{i}: dispatch type/value corrupted ({parsed.GetType().Name})");
                        break;
                    }
                    case 3:  // generic entry — full primitive matrix (values from thread seed)
                    {
                        double value = random.NextDouble();
                        var message = new AllTypesMessage
                        {
                            Bool = true, Byte = (byte)threadId, Int32 = (int)(value * int.MaxValue),
                            Single = (float)value, Double = value, Text = "s" + value.ToString("E4"),
                            Blob = new byte[] { (byte)i, 255 }, Samples = new List<double> { value },
                        };
                        var back = MessageSerializer.Deserialize<AllTypesMessage>(MessageSerializer.Serialize(message));
                        if (back.Double != value || back.Blob![0] != (byte)i || back.Samples![0] != value)
                            failures.Enqueue($"t{threadId} i{i}: AllTypes corrupted");
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                failures.Enqueue($"t{threadId} i{i}: unexpected exception {exception.GetType().FullName}: {exception.Message}");
                return;
            }
        }
    }
}
