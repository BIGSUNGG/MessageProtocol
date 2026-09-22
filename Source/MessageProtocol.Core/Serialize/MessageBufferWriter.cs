using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MessageProtocol.Serialize
{
    /// <summary>
    /// Forward-only pooled byte buffer writer. Rents from ArrayPool and grows automatically when needed.
    /// Writes little-endian fixed-width primitives plus length-prefixed strings and byte segments.
    /// </summary>
    public ref struct MessageBufferWriter
    {
        /// <summary>
        /// Default nesting depth limit for serialization. Intentionally identical to the reader's default —
        /// prevents the asymmetry where a graph you can write cannot be read back by a peer with default settings (Known-Issues KI-25).
        /// </summary>
        public const int DefaultMaxNestingDepth = MessageBufferReader.DefaultMaxNestingDepth;

        byte[] _buffer;
        int _position;
        int _depth;
        int _maxNestingDepth;

        MessageBufferWriter(byte[] buffer, int maxNestingDepth)
        {
            _buffer = buffer;
            _position = 0;
            _depth = 0;
            _maxNestingDepth = maxNestingDepth;
        }

        public static MessageBufferWriter Create(int initialCapacity = 256)
        {
            return Create(initialCapacity, DefaultMaxNestingDepth);
        }

        /// <param name="initialCapacity">Initial rented capacity (0 or less starts with an empty buffer and grows on the first write).</param>
        /// <param name="maxNestingDepth">
        /// Nesting depth limit for serialization. An escape hatch for callers with legitimately deep object graphs;
        /// to read the result back, the receiving side must raise the same limit via <see cref="MessageBufferReader(ReadOnlySpan{byte}, int)"/>. Values of 0 or less are rejected.
        /// </param>
        public static MessageBufferWriter Create(int initialCapacity, int maxNestingDepth)
        {
            if (maxNestingDepth <= 0) ThrowInvalidMaxNestingDepth(maxNestingDepth);
            var buffer = initialCapacity <= 0
                ? Array.Empty<byte>()
                : ArrayPool<byte>.Shared.Rent(initialCapacity);
            return new MessageBufferWriter(buffer, maxNestingDepth);
        }

        public int Length => _position;
        public int Capacity => _buffer.Length;
        public Span<byte> WrittenSpan => _buffer.AsSpan(0, _position);
        public ReadOnlySpan<byte> WrittenReadOnlySpan => _buffer.AsSpan(0, _position);

        /// <summary>Maximum nesting depth this writer allows.</summary>
        public int MaxNestingDepth => _maxNestingDepth;

        /// <summary>Current nesting depth — managed by <see cref="EnterNestedObject"/> and <see cref="LeaveNestedObject"/>.</summary>
        public int NestingDepth => _depth;

        /// <summary>
        /// Marks the start of a nested object. Throws <see cref="InvalidOperationException"/> once the limit is reached —
        /// either the object graph is genuinely too deep (long linked list, deep tree) or a cycle leaked in through a
        /// runtime-dispatched member (the dispatch path does not track back-references). Without this guard, both cases
        /// exhaust the stack through recursion and kill the process with an **uncatchable stack overflow** (Known-Issues KI-25).
        /// Generated code and <c>SerializeToWriter</c> call this at every recursion point.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnterNestedObject()
        {
            if (_depth >= _maxNestingDepth) ThrowNestingTooDeep(_maxNestingDepth);
            _depth++;
        }

        /// <summary>
        /// Marks the end of a nested object. An unmatched call (e.g. an exception mid-write) only inflates the depth,
        /// so the guard fails in the safe direction. Clamped at 0 so depth never goes negative (a negative depth would disable the limit).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void LeaveNestedObject()
        {
            if (_depth > 0) _depth--;
        }

        /// <summary>Reserves room for <paramref name="size"/> bytes and returns the segment, advancing the write position.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> GetSpan(int size)
        {
            // Negative sizes used to be blocked only incidentally by the Span constructor's ArgumentOutOfRange — now an explicit API contract (KI-37).
            if (size < 0) ThrowNegativeSpanSize(size);
            EnsureCapacity(size);
            var span = _buffer.AsSpan(_position, size);
            _position += size;
            return span;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int count)
        {
            // A negative Advance rewinds the position, so later writes would overwrite already-written payload (Known-Issues KI-21).
            if (count < 0) ThrowNegativeCount(count);
            if ((uint)(_position + count) > (uint)_buffer.Length)
            {
                ThrowAdvanceBeyondCapacity();
            }
            _position += count;
        }

        /// <summary>Guarantees room for <paramref name="additional"/> more bytes. Generated code sums fixed-size segments and calls this once.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int additional)
        {
            // long comparison — `_position + additional` in int arithmetic overflows to negative for GB-scale requests,
            // making the growth guard pass falsely; the subsequent `AsSpan`/`CopyTo` then throws an exception that hides the cause (Known-Issues KI-7).
            if ((long)_position + additional > _buffer.Length)
            {
                Grow(additional);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow(int additional)
        {
            long required = (long)_position + additional;
            if (required < 0 || required > MaxBufferLength)
            {
                ThrowGrowBeyondMaxBuffer(required);
            }

            int newCapacity = ComputeGrowCapacity(_buffer.Length, required);
            var newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);
            if (_position > 0)
            {
                Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _position);
            }
            if (_buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }
            _buffer = newBuffer;
        }

        /// <summary>First growth capacity for an empty buffer (same as <see cref="MessageWireFormat.DefaultStreamCapacity"/>).</summary>
        const int DefaultGrowCapacity = 256;

        /// <summary>
        /// Computes the growth capacity — **long arithmetic** + array upper-bound clamp (Known-Issues KI-7).
        /// The old formula `Math.Max(_buffer.Length * 2, required)` overflowed to negative in `Length * 2` once the buffer
        /// passed 1GB, so `Math.Max` always picked `required`. Every growth then rented an **exact-size, zero-headroom**
        /// array plus a full copy, making growth cost quadratic (and arrays that large are not pooled anyway). The payload
        /// limit <see cref="MaxBufferLength"/> (~2.1GB) is within this library's supported range, so doubling must hold even there.
        /// </summary>
        /// <param name="currentCapacity">Length of the currently rented array (0 = empty buffer).</param>
        /// <param name="required">Total capacity needed (position + additional) — the caller guarantees ≤ <see cref="MaxBufferLength"/>.</param>
        internal static int ComputeGrowCapacity(int currentCapacity, long required)
        {
            long doubled = currentCapacity <= 0 ? DefaultGrowCapacity : (long)currentCapacity * 2;
            long capacity = doubled > required ? doubled : required;
            return capacity > MaxBufferLength ? (int)MaxBufferLength : (int)capacity;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteSByte(sbyte value) => WriteByte((byte)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt16(short value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(_position), value);
            _position += 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt16(ushort value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
            _position += 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt32(int value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), value);
            _position += 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt32(uint value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
            _position += 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt64(long value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_position), value);
            _position += 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt64(ulong value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
            _position += 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteSingle(float value)
        {
            EnsureCapacity(4);
            BinaryPrimitivesCompat.WriteSingleLittleEndian(_buffer.AsSpan(_position), value);
            _position += 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteDouble(double value)
        {
            EnsureCapacity(8);
            BinaryPrimitivesCompat.WriteDoubleLittleEndian(_buffer.AsSpan(_position), value);
            _position += 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteChar(char value) => WriteUInt16(value);

        /// <summary>Writes 16 bytes in GetBits order (lo, mid, hi, flags). No intermediate allocation.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteDecimal(decimal value)
        {
            EnsureCapacity(16);
            Span<decimal> temp = stackalloc decimal[1];
            temp[0] = value;
            ReadOnlySpan<int> raw = MemoryMarshal.Cast<decimal, int>(temp);
            var span = _buffer.AsSpan(_position);
            BinaryPrimitives.WriteInt32LittleEndian(span, raw[2]);          // lo
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4), raw[3]); // mid
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), raw[1]); // hi
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12), raw[0]);// flags
            _position += 16;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            EnsureCapacity(value.Length);
            value.CopyTo(_buffer.AsSpan(_position));
            _position += value.Length;
        }

        /// <summary>null = int32(-1), empty string = int32(0), otherwise int32(utf8 byte length) + utf8 bytes.</summary>
        public void WriteString(string? value)
        {
            if (value is null)
            {
                WriteInt32(-1);
                return;
            }
            if (value.Length == 0)
            {
                WriteInt32(0);
                return;
            }

            // Computes the required capacity in long — `4 + GetMaxByteCount(int)` overflows to negative for huge strings,
            // skipping the EnsureCapacity growth; GetBytes then fails with an internal ArgumentException (KI-22).
            long required = GetStringBufferRequirement(value.Length);
            if (_position + required > MaxBufferLength)
            {
                ThrowStringTooLarge(value.Length);
            }
            // The guard above ensures required ≤ MaxBufferLength - _position < int.MaxValue, so the narrowing cast and later int arithmetic are safe.
            EnsureCapacity((int)required);
            int written = StrictUtf8.GetBytes(value, 0, value.Length, _buffer, _position + 4);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), written);
            _position += 4 + written;
        }

        // UTF-8 upper-bound formula (max 3 bytes per char + 3-byte preamble). Same as `Encoding.GetMaxByteCount(int)`, but
        // that method returns a negative int once `charCount * 3 + 3` overflows at ~715 million characters (Known-Issues KI-22).
        const long Utf8MaxBytesPerChar = 3;
        const long Utf8PreambleBytes = 3;
        const int LengthPrefixBytes = 4;

        /// <summary>Maximum byte[] buffer length (.NET array limit) — larger payloads cannot fit in a single buffer.</summary>
        const long MaxBufferLength = 0X7FEFFFFFL;

        /// <summary>Returns, as a long, the buffer bytes required by a string payload (4-byte length prefix + UTF-8 upper bound).</summary>
        internal static long GetStringBufferRequirement(int charCount)
        {
            return LengthPrefixBytes + (Utf8MaxBytesPerChar * charCount) + Utf8PreambleBytes;
        }

        // Silently replacing lone surrogates with replacement bytes would make the receiver see a different string than the sender,
        // so a strict fallback surfaces encoding failures as-is (wire integrity policy — Known-Issues KI-20).
        static readonly Encoding StrictUtf8 = Encoding.GetEncoding(65001, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        /// <summary>
        /// Rewrites an int32 at the given offset (rare uses such as external framing).
        /// The offset must lie inside the **already written** range (`0 .. Length - 4`) — anything else touches unwritten bytes
        /// of the rented array, which later returns to the pool and becomes a write visible to another renter (Known-Issues KI-7).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PatchInt32(int offset, int value)
        {
            // `_position - 4` can be negative, so use two comparisons instead of the uint trick
            // (wrapping in uint turns the negative into a huge positive when Length < 4, letting every offset pass).
            if (offset < 0 || offset > _position - 4)
            {
                ThrowPatchOutOfRange(offset);
            }

            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(offset), value);
        }

        /// <summary>Transfers buffer ownership to a <see cref="PooledBuffer"/> and empties the writer.</summary>
        public PooledBuffer ToPooledBuffer()
        {
            var owner = PooledBuffer.FromRented(_buffer, _position);
            _buffer = Array.Empty<byte>();
            _position = 0;
            _depth = 0;
            return owner;
        }

        /// <summary>Copies the written content into a new byte[] and returns it (compatibility path).</summary>
        public byte[] ToArray()
        {
            if (_position == 0) return Array.Empty<byte>();
            var result = new byte[_position];
            Buffer.BlockCopy(_buffer, 0, result, 0, _position);
            return result;
        }

        public void Dispose()
        {
            if (_buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = Array.Empty<byte>();
                _position = 0;
            }
        }

        static void ThrowAdvanceBeyondCapacity()
        {
            throw new InvalidOperationException("Advance would move position beyond buffer capacity.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNegativeCount(int count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must not be negative.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNegativeSpanSize(int size)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Span size must not be negative.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowStringTooLarge(int charCount)
        {
            throw new ArgumentException(
                $"String of {charCount} characters needs more than the maximum buffer size ({MaxBufferLength} bytes) and cannot be serialized.",
                "value");
        }

        // Writer-side limit violations are caller data/state problems, so unlike the reader (illegal wire content = InvalidDataException)
        // they are reported as InvalidOperationException — same policy as `ThrowAdvanceBeyondCapacity`.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNestingTooDeep(int maxNestingDepth)
        {
            throw new InvalidOperationException(
                $"Nested object depth exceeds the maximum of {maxNestingDepth}. " +
                $"The object graph is too deep to serialize (long chain, deep tree, or a cycle through a " +
                $"runtime-dispatched member such as a type parameter or an abstract message type). " +
                $"Use 'MessageBufferWriter.Create(initialCapacity, maxNestingDepth)' if this graph is legitimately deeper, " +
                $"and raise the receiving reader's limit to match.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowInvalidMaxNestingDepth(int maxNestingDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxNestingDepth), maxNestingDepth, "Max nesting depth must be positive.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowGrowBeyondMaxBuffer(long required)
        {
            throw new InvalidOperationException(
                $"Message payload requires {required} bytes, which exceeds the maximum buffer size " +
                $"({MaxBufferLength} bytes) — a single byte[] cannot hold it.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void ThrowPatchOutOfRange(int offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), offset,
                $"Offset must point at a 4-byte range inside the written payload (0 .. {_position - 4}); Length is {_position}.");
        }
    }
}
