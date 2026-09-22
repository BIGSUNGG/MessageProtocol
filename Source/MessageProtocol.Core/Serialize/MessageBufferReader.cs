using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MessageProtocol.Serialize
{
    /// <summary>
    /// Forward-only ReadOnlySpan-based buffer reader. Reads past the end throw <see cref="EndOfStreamException"/>.
    /// </summary>
    public ref struct MessageBufferReader
    {
        /// <summary>
        /// Default nesting depth limit for deserialization. Stops an untrusted peer from packing deep nesting into a small
        /// frame to exhaust the recursion stack (stack overflow — uncatchable, kills the process immediately) (Known-Issues KI-14).
        /// Can be raised per reader via <see cref="MessageBufferReader(ReadOnlySpan{byte}, int)"/>.
        /// </summary>
        public const int DefaultMaxNestingDepth = 64;

        ReadOnlySpan<byte> _buffer;
        int _position;
        int _depth;
        int _maxNestingDepth;

        public MessageBufferReader(ReadOnlySpan<byte> buffer)
            : this(buffer, DefaultMaxNestingDepth)
        {
        }

        /// <param name="buffer">The payload buffer to read.</param>
        /// <param name="maxNestingDepth">
        /// Nesting depth limit for deserialization. An escape hatch for callers with legitimately deep object graphs;
        /// the value must stay below the thread stack size (one stack frame per level). Values of 0 or less are rejected.
        /// </param>
        public MessageBufferReader(ReadOnlySpan<byte> buffer, int maxNestingDepth)
        {
            if (maxNestingDepth <= 0) ThrowInvalidMaxNestingDepth(maxNestingDepth);
            _buffer = buffer;
            _position = 0;
            _depth = 0;
            _maxNestingDepth = maxNestingDepth;
        }

        public int Position => _position;
        public int Remaining => _buffer.Length - _position;
        public ReadOnlySpan<byte> UnreadSpan => _buffer.Slice(_position);

        /// <summary>Maximum nesting depth this reader allows.</summary>
        public int MaxNestingDepth => _maxNestingDepth;

        /// <summary>Current nesting depth — managed by <see cref="EnterNestedObject"/> and <see cref="LeaveNestedObject"/>.</summary>
        public int NestingDepth => _depth;

        /// <summary>
        /// Marks the start of a nested object read. Throws <see cref="InvalidDataException"/> once the limit is reached
        /// (illegal wire content — distinct from the <see cref="EndOfStreamException"/> boundary violation). Generated code and
        /// <c>DeserializeFromReader</c> call this at every recursion point.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnterNestedObject()
        {
            if (_depth >= _maxNestingDepth) ThrowNestingTooDeep(_maxNestingDepth);
            _depth++;
        }

        /// <summary>
        /// Marks the end of a nested object read. An unmatched call (e.g. an exception mid-read) only inflates the depth,
        /// so the guard fails in the safe direction — a reader that threw is positioned mid-object and must not be reused.
        /// Clamped at 0 so depth never goes negative (a negative depth would disable the limit).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void LeaveNestedObject()
        {
            if (_depth > 0) _depth--;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte()
        {
            if ((uint)_position >= (uint)_buffer.Length) ThrowEndOfBuffer();
            return _buffer[_position++];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public sbyte ReadSByte() => (sbyte)ReadByte();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadBoolean() => ReadByte() != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public short ReadInt16()
        {
            EnsureRemaining(2);
            short value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_position));
            _position += 2;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort ReadUInt16()
        {
            EnsureRemaining(2);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position));
            _position += 2;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadInt32()
        {
            EnsureRemaining(4);
            int value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position));
            _position += 4;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadUInt32()
        {
            EnsureRemaining(4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position));
            _position += 4;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadInt64()
        {
            EnsureRemaining(8);
            long value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position));
            _position += 8;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadUInt64()
        {
            EnsureRemaining(8);
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position));
            _position += 8;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ReadSingle()
        {
            EnsureRemaining(4);
            float value = BinaryPrimitivesCompat.ReadSingleLittleEndian(_buffer.Slice(_position));
            _position += 4;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double ReadDouble()
        {
            EnsureRemaining(8);
            double value = BinaryPrimitivesCompat.ReadDoubleLittleEndian(_buffer.Slice(_position));
            _position += 8;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public char ReadChar() => (char)ReadUInt16();

        /// <summary>Restores the 16 bytes written by WriteDecimal in GetBits order (lo, mid, hi, flags).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public decimal ReadDecimal()
        {
            EnsureRemaining(16);
            var span = _buffer.Slice(_position);
            int lo = BinaryPrimitives.ReadInt32LittleEndian(span);
            int mid = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4));
            int hi = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8));
            int flags = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(12));
            // flags contract: bit 31 sign, bits 16–23 scale (0–28), the rest reserved (0). Violations are rejected — a bad scale causes a stack buffer overflow in DecCalc arithmetic (process crash).
            uint f = (uint)flags;
            if ((f & 0x7F00FFFFu) != 0 || ((f >> 16) & 0xFFu) > 28u)
            {
                throw new InvalidDataException("Invalid decimal wire bits.");
            }
            _position += 16;

            Span<decimal> temp = stackalloc decimal[1];
            Span<int> raw = MemoryMarshal.Cast<decimal, int>(temp);
            raw[0] = flags;
            raw[1] = hi;
            raw[2] = lo;
            raw[3] = mid;
            return temp[0];
        }

        /// <summary>Returns a view of the next <paramref name="length"/> bytes and advances the read position.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> ReadBytes(int length)
        {
            EnsureRemaining(length);
            var span = _buffer.Slice(_position, length);
            _position += length;
            return span;
        }

        /// <summary>Reads an int32 length-prefixed string. -1 means null, 0 means empty. Invalid UTF-8 is rejected as corrupt wire data.</summary>
        public string? ReadString()
        {
            int length = ReadInt32();
            if (length == -1) return null;
            // The only null contract is -1. Other negative values (-2 … int.MinValue) are corrupt length prefixes and must not be
            // transmuted into null and silently passed (Known-Issues KI-6).
            if (length < -1)
            {
                throw new InvalidDataException("String length prefix is negative but not -1.");
            }
            if (length == 0) return string.Empty;
            try
            {
                return StrictUtf8.GetString(ReadBytes(length));
            }
            catch (DecoderFallbackException exception)
            {
                // Reports illegal wire content, distinct from a boundary violation (EndOfStreamException) — same policy as ReadDecimal KI-15.
                throw new InvalidDataException("String payload is not valid UTF-8.", exception);
            }
        }

        // Silently swapping invalid bytes for U+FFFD would let corrupt packets decode without a trace, so a strict fallback rejects them (Known-Issues KI-20).
        static readonly Encoding StrictUtf8 = Encoding.GetEncoding(65001, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Skip(int count)
        {
            // A negative Skip rewinds the position, allowing already-consumed bytes to be read again — a forward-only contract violation (Known-Issues KI-21).
            if (count < 0) ThrowNegativeCount(count);
            EnsureRemaining(count);
            _position += count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnsureRemaining(int count)
        {
            if ((uint)(_position + count) > (uint)_buffer.Length)
            {
                ThrowEndOfBuffer();
            }
        }

        static void ThrowEndOfBuffer()
        {
            throw new EndOfStreamException("Attempted to read past the end of the buffer.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNegativeCount(int count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must not be negative.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNestingTooDeep(int maxNestingDepth)
        {
            throw new InvalidDataException(
                $"Nested object depth exceeds the maximum of {maxNestingDepth}. " +
                $"The payload is corrupt or hostile; construct the reader with " +
                $"'new MessageBufferReader(buffer, maxNestingDepth)' if this graph is legitimately deeper.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowInvalidMaxNestingDepth(int maxNestingDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxNestingDepth), maxNestingDepth, "Max nesting depth must be positive.");
        }
    }
}
