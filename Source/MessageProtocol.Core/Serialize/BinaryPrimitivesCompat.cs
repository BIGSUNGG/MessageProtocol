using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MessageProtocol.Serialize
{
    /// <summary>
    /// Provides little-endian float/double reads and writes missing from netstandard2.1.
    /// </summary>
    static class BinaryPrimitivesCompat
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float ReadSingleLittleEndian(ReadOnlySpan<byte> source)
        {
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static double ReadDoubleLittleEndian(ReadOnlySpan<byte> source)
        {
            return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void WriteSingleLittleEndian(Span<byte> destination, float value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void WriteDoubleLittleEndian(Span<byte> destination, double value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination, BitConverter.DoubleToInt64Bits(value));
        }
    }
}
