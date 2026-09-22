using MessageProtocol.Serialize;
using Xunit;

namespace MessageProtocol.Tests;

/// <summary>
/// KI-7 regression: the writer's growth arithmetic and the `PatchInt32` boundary.
/// Pre-fix experiment (consumer process outside this repo): ① on a 1.5GB buffer `_buffer.Length * 2`
/// overflowed to **-1,294,967,296**, so `Math.Max` always picked the exact requirement → every growth rented
/// with zero headroom and copied everything (quadratic growth cost, and no pooling at that size). The payload
/// ceiling `0X7FEFFFFF` (~2.1GB) is within this library's supported range, so this was a real regression.
/// ② `EnsureCapacity(int.MaxValue)` attempted a 2GB allocation and threw `OutOfMemoryException` ("Array
/// dimensions exceeded supported range") instead of a clear rejection. ③ `PatchInt32(60)` was accepted even
/// with `Length = 4`, writing into **unwritten bytes** of the rented array — which later returns to the pool.
/// </summary>
public class WriterGrowthTests
{
    /// <summary>Same value as `MessageBufferWriter.MaxBufferLength` (private const) — the .NET array ceiling.</summary>
    const int MaxBufferLength = 0x7FEFFFFF;

    [Theory]
    [InlineData(0, 10, 256)]        // first growth of an empty buffer = default capacity
    [InlineData(256, 300, 512)]     // doubling wins when larger than required
    [InlineData(256, 1000, 1000)]   // doubling insufficient → required
    [InlineData(1024, 1500, 2048)]  // doubling larger than required → keep headroom
    [InlineData(1024, 2048, 2048)]  // doubling equals required → that value (already 2× headroom)
    public void grow_capacity_is_max_of_doubling_and_required(int currentCapacity, long required, int expected)
    {
        Assert.Equal(expected, MessageBufferWriter.ComputeGrowCapacity(currentCapacity, required));
    }

    [Fact]
    public void grow_capacity_stays_non_negative_and_keeps_headroom_beyond_1gb()
    {
        // Pre-fix formula: Math.Max(1_500_000_000 * 2, required) = Math.Max(-1_294_967_296, required) = required (0 headroom).
        int capacity = MessageBufferWriter.ComputeGrowCapacity(1_500_000_000, 1_500_000_100L);

        Assert.Equal(MaxBufferLength, capacity);   // headroom preserved up to the array ceiling
        Assert.True(capacity > 1_500_000_100L, "exact-fit growth means quadratic re-rent + full copy");
    }

    [Fact]
    public void grow_capacity_stays_within_ceiling_and_never_below_requirement()
    {
        int[] capacities = { 0, 1, 256, 65_536, 1_000_000, 1_073_741_824, 1_500_000_000, MaxBufferLength };
        long[] requirements = { 1, 1_000, 1_073_741_824, MaxBufferLength };

        foreach (int currentCapacity in capacities)
        {
            foreach (long required in requirements)
            {
                int capacity = MessageBufferWriter.ComputeGrowCapacity(currentCapacity, required);

                Assert.InRange(capacity, (int)Math.Min(required, MaxBufferLength), MaxBufferLength);
            }
        }
    }

    [Fact]
    public void capacity_demand_over_ceiling_throws_clearly_instead_of_attempting_allocation()
    {
        var writer = MessageBufferWriter.Create();

        // Pre-fix: attempted Rent(2,147,483,647) → OutOfMemoryException("Array dimensions exceeded supported range").
        var exception = Assert.IsType<InvalidOperationException>(CatchEnsureCapacity(ref writer, int.MaxValue));

        Assert.Contains("maximum buffer size", exception.Message);
        writer.Dispose();
    }

    [Fact]
    public void normal_growth_still_works_and_leaves_headroom_capacity()
    {
        var writer = MessageBufferWriter.Create(256);
        writer.WriteInt32(7);

        writer.EnsureCapacity(1000);
        Assert.True(writer.Capacity >= 1004);

        // Writes within already-reserved capacity must not grow (this is how generated code reserves fixed
        // sizes in bulk).
        int capacityBefore = writer.Capacity;
        writer.WriteInt64(9);

        Assert.Equal(capacityBefore, writer.Capacity);
        Assert.Equal(12, writer.Length);
        writer.Dispose();
    }

    [Fact]
    public void patch_int32_works_within_the_written_region()
    {
        var writer = MessageBufferWriter.Create(64);
        writer.WriteInt32(1);
        writer.WriteInt32(2);
        Assert.Equal(8, writer.Length);

        writer.PatchInt32(0, 111);
        writer.PatchInt32(4, 222);   // last 4 bytes = boundary offset

        Assert.Equal(new byte[] { 111, 0, 0, 0, 222, 0, 0, 0 }, writer.ToArray());
        writer.Dispose();
    }

    [Theory]
    [InlineData(-1)]   // negative offset
    [InlineData(1)]    // cannot fully contain 4 bytes (Length 4)
    [InlineData(4)]    // outside the written region = unwritten pooled bytes
    [InlineData(60)]   // offset accepted by the pre-fix code
    public void patch_int32_rejects_offsets_outside_the_written_region(int offset)
    {
        var writer = MessageBufferWriter.Create(64);
        writer.WriteInt32(1);        // Length = 4 → the only allowed offset is 0

        Assert.IsType<ArgumentOutOfRangeException>(CatchPatchInt32(ref writer, offset, 12345));
        Assert.Equal(4, writer.Length);   // state unchanged after rejection
        writer.Dispose();
    }

    [Fact]
    public void patch_int32_is_rejected_even_on_an_empty_writer()
    {
        var writer = MessageBufferWriter.Create(64);   // Length = 0

        Assert.IsType<ArgumentOutOfRangeException>(CatchPatchInt32(ref writer, 0, 1));
        writer.Dispose();
    }

    // `MessageBufferWriter` is a ref struct and cannot be captured by lambdas (Assert.Throws unavailable) —
    // exceptions are observed via helpers that take it by ref and use try/catch.

    static Exception? CatchEnsureCapacity(ref MessageBufferWriter writer, int additional)
    {
        try
        {
            writer.EnsureCapacity(additional);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    static Exception? CatchPatchInt32(ref MessageBufferWriter writer, int offset, int value)
    {
        try
        {
            writer.PatchInt32(offset, value);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
