using System;
using System.Buffers;

namespace MessageProtocol.Serialize
{
    /// <summary>
    /// Serialization result buffer owning a byte[] rented from ArrayPool.
    /// Return it to the pool with <see cref="Dispose"/> when done. Double Dispose is safe.
    /// </summary>
    /// <remarks>
    /// Ownership lives in a reference-type <see cref="Owner"/> holder — this type is a struct, so every assignment or pass
    /// makes a copy; if copies did not share the holder, one copy's Dispose would be invisible to the others and the same
    /// array would return to the pool twice (the next renter would see someone else's data — Known-Issues KI-37). The shared
    /// holder lets all copies observe a single returned state, and whichever copy Disposes first leaves the rest with empty views.
    /// </remarks>
    public struct PooledBuffer : IDisposable
    {
        /// <summary>The actual owner of the rented array. Every struct copy shares this instance.</summary>
        sealed class Owner
        {
            public byte[]? Buffer;
            public int Length;
            public bool FromPool;

            public void ReturnToPoolOnce()
            {
                if (!FromPool || Buffer is null) return;
                ArrayPool<byte>.Shared.Return(Buffer);
                Buffer = null;
                Length = 0;
                FromPool = false;
            }
        }

        Owner? _owner;

        PooledBuffer(Owner owner)
        {
            _owner = owner;
        }

        [Obsolete("Unused across DS_MessageProtocol, its tests, Sandbox, and the DS_RPC sibling stack (audited 2026-09-08); candidate for removal in the next major version.", error: false)]
        public static PooledBuffer Empty => default;

        public static PooledBuffer FromRented(byte[] rented, int length)
        {
            if (rented == null) throw new ArgumentNullException(nameof(rented));
            if ((uint)length > (uint)rented.Length) throw new ArgumentOutOfRangeException(nameof(length));
            // A zero-length array may be the Array.Empty singleton — not a pool-return candidate (early-returning zero-length
            // arrays to the shared pool is undocumented internal behavior and can throw in custom pools).
            return new PooledBuffer(new Owner { Buffer = rented, Length = length, FromPool = rented.Length > 0 });
        }

        public int Length => _owner?.Length ?? 0;

        public ReadOnlySpan<byte> Span => _owner?.Buffer is { } buffer
            ? buffer.AsSpan(0, _owner.Length)
            : ReadOnlySpan<byte>.Empty;

        [Obsolete("Unused across DS_MessageProtocol, its tests, Sandbox, and the DS_RPC sibling stack (audited 2026-09-08); use Span instead. Candidate for removal in the next major version.", error: false)]
        public ReadOnlyMemory<byte> Memory => _owner?.Buffer is { } buffer
            ? buffer.AsMemory(0, _owner.Length)
            : ReadOnlyMemory<byte>.Empty;

        /// <summary>View without pool return. The array may be reused, so watch its lifetime.</summary>
        [Obsolete("Unused across DS_MessageProtocol, its tests, Sandbox, and the DS_RPC sibling stack (audited 2026-09-08); use Span or ToArray instead. Candidate for removal in the next major version.", error: false)]
        public ArraySegment<byte> UnsafeArraySegment => _owner?.Buffer is { } buffer
            ? new ArraySegment<byte>(buffer, 0, _owner.Length)
            : default;

        public byte[] ToArray()
        {
            if (_owner?.Buffer is not { } source || _owner.Length == 0) return Array.Empty<byte>();
            var result = new byte[_owner.Length];
            Buffer.BlockCopy(source, 0, result, 0, _owner.Length);
            return result;
        }

        public void Dispose()
        {
            // Even a copy sees the same Owner — the array returns to the pool exactly once, and every copy's view is empty afterwards.
            _owner?.ReturnToPoolOnce();
        }
    }
}
