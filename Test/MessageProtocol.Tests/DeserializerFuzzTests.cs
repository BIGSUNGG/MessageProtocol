using MessageProtocol;
using MessageProtocol.Serialize;
using System;
using MessageProtocol.Tests.Fixtures;
using System.Reflection;
using Xunit;

namespace MessageProtocol.Tests;

/// <summary>
/// Adversarial differential fuzzing of the deserialization trust boundary — verifies **integration
/// invariants** rather than unit-testing individual guards. Apply deterministic mutations to valid frames
/// (bit flips, truncation, extreme-value substitution, multi-bit, garbage suffix), then check that:
///  ① rejections always happen with known clean exception types (an unseen exception type = a new defect), and
///  ② a successful read is not silent corruption — reserializing the result and round-tripping again must
///     reproduce identical bytes (idempotent round trip; serialization is deterministic, so byte comparison
///     serves as the equivalence oracle).
/// Both entries are stressed — object dispatch (including header routing) and the generic entry (mismatched
/// headers are rejected by the KI-5 verification).
/// Seeded (reproducible) — on failure the entry and iteration number are exposed in the diagnostics.
/// </summary>
public class DeserializerFuzzTests
{
    static readonly System.Type[] CleanRejections =
    {
        typeof(System.IO.InvalidDataException),     // illegal wire content (header, tag, depth, UTF-8, decimal flags, dispatch type)
        typeof(System.IO.EndOfStreamException),     // boundary violations, over-allocation guard
        typeof(KeyNotFoundException),               // unregistered MessageId/(MessageId,ClassId)
        typeof(InvalidOperationException),           // registration/contract violations
        typeof(ArgumentException),                  // argument contracts such as empty input
        typeof(ArgumentNullException),
        typeof(ArgumentOutOfRangeException),
    };

    /// <summary>Fuzzing seed frames — pairs of the object dispatch entry and (optionally) the generic entry.</summary>
    static (byte[] Frame, System.Type? TypedEntry)[] BuildSeedFrames()
    {
        var allTypes = new AllTypesMessage
        {
            Bool = true, Byte = 200, SByte = -3, Int16 = -1234, UInt16 = 51234,
            Int32 = -987654, UInt32 = 3_000_000_000, Int64 = long.MinValue / 2, UInt64 = ulong.MaxValue / 2,
            Single = 3.5f, Double = -2.718281828, Decimal = 123456.789m, Char = '한', // Char: intentional non-ASCII payload (UTF-8)
            Text = "텍스트 with ASCII 123", Level = Level.Mid, // Text: intentional non-ASCII payload (UTF-8)
            Blob = new byte[] { 1, 2, 3, 250, 251 },
            Samples = new List<double> { 1.5, -2.5, double.Epsilon },
            Tags = new[] { "a", "bb", "ccc" },
            Codes = new List<byte> { 9, 8, 7 }.AsReadOnly(),
            Nested = new FlatMessage { Value = 77 },
        };
        var chain = new ChainMessage { Next = new ChainMessage { Next = new ChainMessage() } };
        var envelope = new GenericEnvelope<FlatMessage>
        {
            Value = new FlatMessage { Value = 5 },
            Note = "note",
            Items = new List<FlatMessage?> { new FlatMessage { Value = 1 }, null, new FlatMessage { Value = 2 } },
        };
        var noId = new NoIdMessage { Flag = 7, Note = "nonid" };
        var login = new LoginEvent { Timestamp = 123L, User = "kim" };

        // A chain just under the depth limit (64) — truncation and bit mutations interact with the depth guard.
        ChainMessage deep = new ChainMessage();
        for (int i = 0; i < 60; i++) deep = new ChainMessage { Next = deep };

        // Type generated under the netstandard2.1 fallback profile (Unity — no CollectionsMarshal): exercises
        // the indexer-loop reader and fallback bulk guards under mutation. Generated code in that assembly is
        // split by target framework, so the Tests (net8/9) corpus alone never ran the fallback path
        // (2026-09-08 coverage expansion).
        var fallback = new MessageProtocol.NetStandardFixtures.FallbackCollections
        {
            Bulk = new List<int> { 1, -2, 3, int.MaxValue },
            Texts = new List<string?> { "a", null, "ccc" },
            Codes = new List<byte> { 250, 0 }.AsReadOnly(),
            Tags = new[] { "x", null },
            Samples = new[] { double.NaN, -0.0, double.NegativeInfinity },
        };

        return new[]
        {
            (MessageSerializer.Serialize(allTypes), typeof(AllTypesMessage)),
            (MessageSerializer.Serialize(chain), typeof(ChainMessage)),
            (MessageSerializer.Serialize(envelope), typeof(GenericEnvelope<FlatMessage>)),
            (MessageSerializer.Serialize(noId), (System.Type?)null),        // NonId: object dispatch rejection path
            (MessageSerializer.Serialize(login), (System.Type?)typeof(LoginEvent)),
            (MessageSerializer.Serialize(deep), (System.Type?)typeof(ChainMessage)),
            (MessageSerializer.Serialize(fallback), (System.Type?)typeof(MessageProtocol.NetStandardFixtures.FallbackCollections)),
        };
    }

    [Fact]
    public void mutated_frames_are_either_cleanly_rejected_or_round_trip_idempotently()
    {
        const int seed = 20260908;
        // Campaign knob: MSGPROT_FUZZ_SCALE=N runs a deeper local campaign (e.g. 15) — CI keeps the default 1 (speed first).
        int scale = int.TryParse(Environment.GetEnvironmentVariable("MSGPROT_FUZZ_SCALE"), out var parsed) && parsed > 0
            ? parsed
            : 1;
        int mutationsPerFrame = 2_000 * scale;
        var random = new Random(seed);

        int rejected = 0, accepted = 0;
        string? firstFailure = null;

        for (int i = 0; i < mutationsPerFrame && firstFailure is null; i++)
        {
            foreach (var (original, typedEntry) in BuildSeedFrames())
            {
                var mutant = Mutate(original, random);
                if (mutant is null) continue;

                // ① object dispatch entry — including header routing.
                firstFailure = ParseAndClassify(mutant, i, "dispatch", ref rejected, ref accepted);
                if (firstFailure is not null) break;

                // ② generic entry — mismatched headers are caught by the KI-5 verification, body mutations by the generated readers.
                if (typedEntry is not null)
                {
                    firstFailure = ParseTypedAndClassify(mutant, typedEntry, i, ref rejected);
                    if (firstFailure is not null) break;
                }
            }
        }

        Assert.True(firstFailure is null, firstFailure);
        // Confirms the fuzzer actually exercised both paths (guards against a dead fuzzer) — minimum observation floors.
        Assert.True(rejected > 300, $"too few rejection observations: {rejected}");
        Assert.True(accepted > 50, $"too few acceptance observations: {accepted}");
    }

    static string? ParseAndClassify(byte[] mutant, int iteration, string entry, ref int rejected, ref int accepted)
    {
        object parsed;
        try
        {
            parsed = MessageSerializer.Deserialize(mutant);
        }
        catch (Exception rejection)
        {
            if (CleanRejections.Contains(rejection.GetType()))
            {
                rejected++;
                return null;
            }
            return $"iter {iteration} [{entry}]: unexpected exception type {rejection.GetType().FullName}: {rejection.Message}";
        }

        accepted++;
        try
        {
            byte[] bytes2 = MessageSerializer.Serialize(parsed);
            object parsed2 = MessageSerializer.Deserialize(bytes2);
            byte[] bytes3 = MessageSerializer.Serialize(parsed2);
            return bytes3.SequenceEqual(bytes2)
                ? null
                : $"iter {iteration} [{entry}]: non-idempotent round trip — silent corruption suspected";
        }
        catch (Exception roundTrip)
        {
            return $"iter {iteration} [{entry}]: successful read failed re-round-trip ({roundTrip.GetType().FullName}: {roundTrip.Message})";
        }
    }

    static string? ParseTypedAndClassify(byte[] mutant, System.Type typedEntry, int iteration, ref int rejected)
    {
        try
        {
            // The generic entry is invoked via reflection on a closed generic method — verifies the same
            // exception contract as the fuzzer body. Deserialize(byte[]) has generic and object overloads, so
            // a name+argument lookup would be ambiguous — pick only the generic definition and close it.
            var method = typeof(MessageSerializer)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == nameof(MessageSerializer.Deserialize)
                    && m.IsGenericMethodDefinition
                    && m.GetParameters() is { Length: 1 } parameters
                    && parameters[0].ParameterType == typeof(byte[]))
                .MakeGenericMethod(typedEntry);
            method.Invoke(null, new object[] { mutant });
            return null;
        }
        catch (TargetInvocationException invocation)
        {
            var inner = invocation.InnerException!;
            if (CleanRejections.Contains(inner.GetType()))
            {
                rejected++;
                return null;
            }
            return $"iter {iteration} [typed {typedEntry.Name}]: unexpected exception type {inner.GetType().FullName}: {inner.Message}";
        }
        catch (Exception direct)
        {
            // Failures of Invoke itself (argument contract) allow only clean types.
            if (CleanRejections.Contains(direct.GetType()))
            {
                rejected++;
                return null;
            }
            return $"iter {iteration} [typed {typedEntry.Name}]: unexpected exception type {direct.GetType().FullName}: {direct.Message}";
        }
    }

    static byte[]? Mutate(byte[] original, Random random)
    {
        var mutant = (byte[])original.Clone();
        switch (random.Next(5))
        {
            case 0: // bit flip
                mutant[random.Next(mutant.Length)] ^= (byte)(1 << random.Next(8));
                break;
            case 1: // truncation
                if (mutant.Length <= 1) return null;
                Array.Resize(ref mutant, random.Next(mutant.Length));
                break;
            case 2: // extreme-value substitution
                mutant[random.Next(mutant.Length)] = (byte)random.Next(256);
                break;
            case 3: // multi-bit — real corruption does not stop at one byte; includes simultaneous hits near length prefixes
                for (int k = 0; k < 2 + random.Next(3); k++)
                {
                    mutant[random.Next(mutant.Length)] ^= (byte)(1 << random.Next(8));
                }
                break;
            case 4: // garbage suffix — inverse of truncation: trailing junk must remain unconsumed
                if (mutant.Length > 256) return null;
                var extended = new byte[mutant.Length + 1 + random.Next(8)];
                mutant.CopyTo(extended, 0);
                for (int k = mutant.Length; k < extended.Length; k++) extended[k] = (byte)random.Next(256);
                mutant = extended;
                break;
        }
        return mutant.SequenceEqual(original) ? null : mutant;
    }
}
