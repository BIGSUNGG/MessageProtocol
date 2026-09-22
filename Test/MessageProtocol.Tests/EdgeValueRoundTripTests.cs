using MessageProtocol;
using MessageProtocol.Serialize;
using MessageProtocol.Tests.Fixtures;
using Xunit;

namespace MessageProtocol.Tests;

/// <summary>
/// Precision checks for edge-value round trips — catches silent-corruption classes that value equality (==)
/// lets slip through:
///  -0.0 and +0.0 compare equal with == — loss of a signed zero is observable **only via bit comparison**.
///  NaNs carry payloads — the quiet/signaling distinction and payload bits must survive the wire.
///  (The wire is raw bit copy; these tests pin that fact as a contract. Any conversion/normalization sneaking
///  in breaks them immediately.)
/// </summary>
public class EdgeValueRoundTripTests
{
    static float F(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));
    static double D(ulong bits) => BitConverter.Int64BitsToDouble(unchecked((long)bits));

    public static TheoryData<uint> FloatPatterns => new()
    {
        0x00000000u,             // +0.0
        0x80000000u,             // -0.0 (signed zero — indistinguishable from +0.0 via ==)
        0x3F800000u,             // 1.0
        0x7F800000u,             // +Infinity
        0xFF800000u,             // -Infinity
        0x7FC00000u,             // quiet NaN (default payload)
        0xFFC00001u,             // quiet NaN (negative, payload 1)
        0x7F800001u,             // signaling NaN
        0x00000001u,             // smallest denormal
        0x007FFFFFu,             // largest denormal
        0x7F7FFFFFu,             // float.MaxValue
        0xFF7FFFFFu,             // -float.MaxValue
        0x00800000u,             // smallest normal
    };

    public static TheoryData<ulong> DoublePatterns => new()
    {
        0x0000000000000000ul,    // +0.0
        0x8000000000000000ul,    // -0.0
        0x3FF0000000000000ul,    // 1.0
        0x7FF0000000000000ul,    // +Infinity
        0xFFF0000000000000ul,    // -Infinity
        0x7FF8000000000000ul,    // quiet NaN
        0xFFF8000000000042ul,    // quiet NaN (negative, payload)
        0x7FF0000000000001ul,    // signaling NaN
        0x0000000000000001ul,    // smallest denormal
        0x000FFFFFFFFFFFFFul,    // largest denormal
        0x7FEFFFFFFFFFFFFFul,    // double.MaxValue
        0xFFEFFFFFFFFFFFFFul,    // -double.MaxValue
    };

    [Theory]
    [MemberData(nameof(FloatPatterns))]
    public void float_special_bit_patterns_are_preserved_down_to_the_bit(uint bits)
    {
        var message = new AllTypesMessage { Single = F(bits) };

        var roundTrip = MessageSerializer.Deserialize<AllTypesMessage>(MessageSerializer.Serialize(message));

        Assert.Equal(bits, unchecked((uint)BitConverter.SingleToInt32Bits(roundTrip.Single)));
    }

    [Theory]
    [MemberData(nameof(DoublePatterns))]
    public void double_special_bit_patterns_are_preserved_down_to_the_bit(ulong bits)
    {
        var message = new AllTypesMessage { Double = D(bits) };

        var roundTrip = MessageSerializer.Deserialize<AllTypesMessage>(MessageSerializer.Serialize(message));

        Assert.Equal(bits, unchecked((ulong)BitConverter.DoubleToInt64Bits(roundTrip.Double)));
    }

    public static TheoryData<decimal> DecimalPatterns => new()
    {
        0m,                                        // zero
        decimal.MaxValue,                          // 79,228,162,514,264,337,593,543,950,335
        decimal.MinValue,
        decimal.One,
        decimal.MinusOne,
        0.0000000000000000000000000001m,           // smallest positive at scale 28 (allowed maximum)
        -0.0000000000000000000000000001m,
        792281625142643375935439503.35m,           // largest significand×scale combination
        1.0000000000000000000000000000m,           // trailing-zero scale preserved (same value, possibly different bits)
    };

    [Theory]
    [MemberData(nameof(DecimalPatterns))]
    public void decimal_edge_values_are_preserved_including_scale(decimal value)
    {
        var message = new AllTypesMessage { Decimal = value };

        var roundTrip = MessageSerializer.Deserialize<AllTypesMessage>(MessageSerializer.Serialize(message));

        // decimal.Equals compares scale too (1.0 vs 1.00 are distinct) — pin the GetBits round trip as well.
        Assert.Equal(decimal.GetBits(value), decimal.GetBits(roundTrip.Decimal));
    }

    public static TheoryData<char, string?> CharStringPatterns => new()
    {
        // The string values are intentional non-ASCII/boundary payloads: they exercise UTF-8 encoding,
        // NUL bytes, surrogate pairs, and noncharacters on the wire — keep them as-is.
        { '\0', "nul 포함 \0 문자열" },            // NUL inside a string
        { '한', "한글 및 surrogate pair: 𝄞 🎮" },   // BMP + outside BMP (surrogate pair)
        { char.MaxValue, "max" },                  // U+FFFF (noncharacter — encodable in UTF-8)
        { '\uD7FF', "마지막 BMP-before-surrogates" }, // just below the surrogate block
    };

    [Theory]
    [MemberData(nameof(CharStringPatterns))]
    public void char_and_string_edge_values_round_trip(char value, string text)
    {
        var message = new AllTypesMessage { Char = value, Text = text };

        var roundTrip = MessageSerializer.Deserialize<AllTypesMessage>(MessageSerializer.Serialize(message));

        Assert.Equal(value, roundTrip.Char);
        Assert.Equal(text, roundTrip.Text);
    }

    [Fact]
    public void negative_zero_is_preserved_distinct_from_positive_zero()
    {
        // Equal under ==, so ordinary round-trip tests cannot catch this class — pinned explicitly.
        var message = new AllTypesMessage { Single = -0.0f, Double = -0.0 };

        var roundTrip = MessageSerializer.Deserialize<AllTypesMessage>(MessageSerializer.Serialize(message));

        Assert.Equal(0x80000000u, unchecked((uint)BitConverter.SingleToInt32Bits(roundTrip.Single)));
        Assert.Equal(0x8000000000000000ul, unchecked((ulong)BitConverter.DoubleToInt64Bits(roundTrip.Double)));
    }
}
