using MessageProtocol;
using MessageProtocol.NetStandardFixtures;
using MessageProtocol.Serialize;
using MessageProtocol.Tests.Fixtures;
using Xunit;

namespace MessageProtocol.Tests.Fixtures
{
    // Fixtures for automatic [Message] kind inference. The FullName-hash ID pin values are hard-coded in the
    // test assertions — if the algorithm (FNV-1a → 24-bit mask) or the FullName format changes, the pins in
    // this file break first.

    /// <summary>No ancestors and no derivatives → inferred as Standalone. FullName "MessageProtocol.Tests.Fixtures.AutoStandalone" hashes to 0x1F6FBD.</summary>
    [Message]
    public partial class AutoStandalone
    {
        public int Value { get; set; }
        public string? Text { get; set; }
    }

    /// <summary>[Message] derivatives (A/B) exist in the same compilation → inferred as GroupRoot. Hash 0xA8E839.</summary>
    [Message]
    public partial class AutoGroupRoot
    {
        public int RootValue { get; set; }
        public string? RootText { get; set; }
    }

    [Message]
    public partial class AutoGroupElementA : AutoGroupRoot
    {
        public int ElementValue { get; set; }
    }

    [Message]
    public partial class AutoGroupElementB : AutoGroupRoot
    {
        public string? Note { get; set; }
    }

    /// <summary>[Message] generic declaration — MessageId is the hash of the declaration FullName ("…AutoGenericEnvelope`1") = 0x344C18;
    /// closed-construction registration follows the existing [GenericMessage] manual ClassId mechanism.</summary>
    [Message]
    [GenericMessage(typeof(AutoGenericEnvelope<FlatMessage>), ClassId = 1)]
    public partial class AutoGenericEnvelope<T>
    {
        public T? Payload { get; set; }
        public int Stamp { get; set; }
    }

    /// <summary>[Message] base inherited from a reference assembly (NetStandardFixtures) → inferred as GroupElement. Hash 0xE93598.
    /// Generated and registered even though internal (emitted with the declaration's accessibility) — it only
    /// needs to verify, so it is also excluded from xUnit discovery.</summary>
    [Message]
    internal partial class CrossProjectElement : CrossProjectRoot
    {
        public int DerivedValue { get; set; }
    }
}

namespace MessageProtocol.Tests
{
    public class MessageAttributeTests
    {
    [Fact]
    public void message_inference_standalone_round_trips_with_hash_id()
    {
        // intentional non-ASCII payload: exercises UTF-8 round-trip
        var msg = new AutoStandalone { Value = -77, Text = "자동" };

        var rt = MessageSerializer.Deserialize<AutoStandalone>(MessageSerializer.Serialize(msg));

        Assert.Equal(-77, rt.Value);
        Assert.Equal("자동", rt.Text); // payload stays Korean on purpose (see above)
    }

    [Fact]
    public void message_inference_group_round_trips_each_element_via_object_dispatch()
    {
        var a = new AutoGroupElementA { RootValue = 1, RootText = "r", ElementValue = 9 };
        var b = new AutoGroupElementB { RootValue = 2, RootText = "t", Note = "note" };

        var decodedA = Assert.IsType<AutoGroupElementA>(MessageSerializer.Deserialize(MessageSerializer.Serialize((object)a)));
        var decodedB = Assert.IsType<AutoGroupElementB>(MessageSerializer.Deserialize(MessageSerializer.Serialize((object)b)));

        Assert.Equal((1, "r", 9), (decodedA.RootValue, decodedA.RootText, decodedA.ElementValue));
        Assert.Equal((2, "t", "note"), (decodedB.RootValue, decodedB.RootText, decodedB.Note));
    }

    [Fact]
    public void message_hash_id_matches_fullname_fnv1a_24bit_pin_values()
    {
        // Algorithm pin — each value is FNV-1a 32 (OffsetBasis 2166136261, Prime 16777619) over the UTF-8
        // bytes, masked with 0x00FF_FFFF. Compared against **literals** rather than a helper call to prevent
        // accidental reimplementation drift.
        Assert.Equal(0x1F6FBDu, MessageIdHash.FromFullName("MessageProtocol.Tests.Fixtures.AutoStandalone"));

        // Wire MessageId = header byte (flags<<4 | category) << 24 | hash. category 0.
        Assert.Equal(0x201F6FBDu, AutoStandalone.MessageId);           // Standalone flag (0x2)
        Assert.Equal(0x40A8E839u, AutoGroupRoot.MessageId);            // GroupRoot flag (0x4)
        Assert.Equal(0x807AE8F6u, AutoGroupElementA.MessageId);        // GroupElement flag (0x8)
        Assert.Equal(0x807AE763u, AutoGroupElementB.MessageId);
    }

    [Fact]
    public void message_generic_declaration_round_trips_constructions_with_hash_messageid()
    {
        object msg = new AutoGenericEnvelope<FlatMessage> { Payload = new FlatMessage { Value = 3 }, Stamp = 5 };

        var decoded = Assert.IsType<AutoGenericEnvelope<FlatMessage>>(
            MessageSerializer.Deserialize(MessageSerializer.Serialize(msg)));

        Assert.Equal(3, decoded.Payload!.Value);
        Assert.Equal(5, decoded.Stamp);
    }

    [Fact]
    public void message_cross_assembly_inherited_element_round_trips_including_reference_base_members()
    {
        // CrossProjectRoot is pinned as Standalone in NetStandardFixtures (netstandard2.1); the derivative in
        // this compilation is inferred and registered as GroupElement from the single [Message] attribute
        // (wire members are merged across the reference base chain).
        object msg = new CrossProjectElement { BaseValue = 11, BaseText = "base", DerivedValue = 22 };

        var decoded = Assert.IsType<CrossProjectElement>(MessageSerializer.Deserialize(MessageSerializer.Serialize(msg)));

        Assert.Equal((11, "base", 22), (decoded.BaseValue, decoded.BaseText, decoded.DerivedValue));
    }
    }
}
