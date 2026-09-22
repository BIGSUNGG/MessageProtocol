using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MessageProtocol.Serialize
{
    public static partial class MessageSerializer
    {
        /// <summary>Reader delegate dispatched by MessageId.</summary>
        public delegate object BufferReaderFunc(ref MessageBufferReader reader);

        static readonly ConcurrentDictionary<uint, BufferReaderFunc> _readerDispatch = new();

        /// <summary>Generic construction dispatch: (MessageId, ClassId) → reader. The key is a ulong combining both values at 24 bits each.</summary>
        static readonly ConcurrentDictionary<ulong, BufferReaderFunc> _genericReaderDispatch = new();

        /// <summary>Registered owner type of a (MessageId, ClassId) pair. Used to detect conflicts between constructions.</summary>
        static readonly ConcurrentDictionary<ulong, Type> _registeredGenericIds = new();

        internal static ulong GenericDispatchKey(uint messageId, uint classId)
        {
            return ((ulong)messageId << 24) | (classId & MessageWireFormat.MessageIdValueMask);
        }

        /// <summary>Generic hot-path deserialization (no dictionary lookup, no boxing).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ref MessageBufferReader reader) where T : IMessageSerializable<T>
        {
            var deserialize = SerializerCache<T>.Deserialize;
            if (deserialize is null) ThrowMissingDeserialize<T>();
            return deserialize!(ref reader);
        }

        /// <summary>Generic path: deserializes from a ReadOnlySpan.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ReadOnlySpan<byte> data) where T : IMessageSerializable<T>
        {
            if (data.Length == 0) throw new ArgumentException("Message data is empty.", nameof(data));
            var deserialize = SerializerCache<T>.Deserialize;
            if (deserialize is null) ThrowMissingDeserialize<T>();
            var reader = new MessageBufferReader(data);
            return deserialize!(ref reader);
        }

        /// <summary>
        /// Generic path (full-consumption check): succeeds only when the whole frame is consumed exactly. Any remaining bytes
        /// throw <see cref="System.IO.InvalidDataException"/> — a frame written by a peer with a different member layout for this type
        /// (schema drift — a violation of the ADR-0006 layout freeze, e.g. a removed field) fails loudly instead of silently losing data.
        /// The basic <see cref="Deserialize{T}(ReadOnlySpan{byte})"/> allows trailing bytes.
        /// </summary>
        public static T DeserializeExact<T>(ReadOnlySpan<byte> data) where T : IMessageSerializable<T>
        {
            if (data.Length == 0) throw new ArgumentException("Message data is empty.", nameof(data));
            var deserialize = SerializerCache<T>.Deserialize;
            if (deserialize is null) ThrowMissingDeserialize<T>();
            var reader = new MessageBufferReader(data);
            var result = deserialize!(ref reader);
            if (reader.Position != data.Length)
            {
                ThrowTrailingBytes(reader.Position, data.Length);
            }
            return result;
        }

        /// <summary>Generic path: deserializes from a ReadOnlyMemory.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ReadOnlyMemory<byte> data) where T : IMessageSerializable<T>
        {
            return Deserialize<T>(data.Span);
        }

        /// <summary>Generic path: byte[] compatibility path.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(byte[] data) where T : IMessageSerializable<T>
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            if (data.Length == 0) throw new ArgumentException("Message data is empty.", nameof(data));
            var deserializeBytes = SerializerCache<T>.DeserializeBytes;
            if (deserializeBytes is null) ThrowMissingDeserialize<T>();
            return deserializeBytes!(data);
        }

        static void ThrowMissingDeserialize<T>()
        {
            throw new InvalidOperationException(
                $"Type '{typeof(T).FullName}' has no deserialize method. " +
                $"Ensure the type is generated via MessageProtocol.CodeGenerator or defines " +
                $"'public static {typeof(T).Name} Deserialize(ref MessageBufferReader)' and " +
                $"'public static {typeof(T).Name} Deserialize(byte[])'.");
        }

        /// <summary>object dispatch deserialization: routes to the type registered under the header MessageId (Standalone/Group only).</summary>
        public static object Deserialize(byte[] data)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            return Deserialize(new ReadOnlySpan<byte>(data));
        }

        /// <summary>object dispatch deserialization: ReadOnlyMemory input.</summary>
        public static object Deserialize(ReadOnlyMemory<byte> data) => Deserialize(data.Span);

        /// <summary>object dispatch deserialization: ReadOnlySpan input. Generic messages route to their construction by (MessageId, ClassId).</summary>
        public static object Deserialize(ReadOnlySpan<byte> data)
        {
            return DeserializeCore(data, out _);
        }

        /// <summary>
        /// object dispatch deserialization (full-consumption check): succeeds only when the whole frame is consumed exactly.
        /// Any remaining bytes throw <see cref="System.IO.InvalidDataException"/> — a frame written by a peer with a different member layout
        /// (schema drift — a violation of the ADR-0006 layout freeze) fails loudly instead of silently losing data.
        /// The basic <see cref="Deserialize(ReadOnlySpan{byte})"/> allows trailing bytes, e.g. transport-layer framing padding.
        /// </summary>
        public static object DeserializeExact(ReadOnlySpan<byte> data)
        {
            var result = DeserializeCore(data, out int consumed);
            if (consumed != data.Length)
            {
                ThrowTrailingBytes(consumed, data.Length);
            }
            return result;
        }

        /// <summary>Shared routing body — returns the number of bytes consumed (for the full-consumption check).</summary>
        static object DeserializeCore(ReadOnlySpan<byte> data, out int consumed)
        {
            if (data.Length == 0) throw new ArgumentException("Message data is empty.", nameof(data));

            byte header = data[0];
            var flags = MessageWireFormat.GetFlags(header);
            bool generic = MessageWireFormat.IsGenericMessage(header);
            if (!generic && (flags & MessageFlag.IdMessage) == 0)
            {
                // Illegal wire content (a flag bit) is an InvalidDataException — no cast ever happens, so
                // InvalidCastException both mischaracterized the failure and fell outside the fuzzer's clean-rejection
                // list at the trust boundary (found by the 2026-09-08 fuzzer, KI-41 chain).
                throw new System.IO.InvalidDataException("Message is not a standalone or group message; the header flag bits are invalid.");
            }

            uint messageId = ReadMessageIdFromHeader(data);

            if (generic)
            {
                if (data.Length < MessageWireFormat.GenericIdHeaderSize)
                {
                    throw new ArgumentException($"Message data is too short to read the {MessageWireFormat.GenericIdHeaderSize}-byte generic header.");
                }

                uint classId = (uint)data[4] << 16 | (uint)data[5] << 8 | data[6];
                if (!_genericReaderDispatch.TryGetValue(GenericDispatchKey(messageId, classId), out var genericInvoker))
                {
                    throw new KeyNotFoundException($"Generic message type with ID {messageId} and ClassId {classId} is not registered.");
                }

                var genericReader = new MessageBufferReader(data);
                var genericValue = genericInvoker(ref genericReader);
                consumed = genericReader.Position;
                return genericValue;
            }

            if (!_readerDispatch.TryGetValue(messageId, out var invoker))
            {
                throw new KeyNotFoundException($"Message type with ID {messageId} is not registered.");
            }

            var reader = new MessageBufferReader(data);
            var value = invoker(ref reader);
            consumed = reader.Position;
            return value;
        }

        /// <summary>Full-consumption check failure — reported as illegal wire content (distinct from boundary/argument errors).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowTrailingBytes(int consumed, int total)
        {
            throw new System.IO.InvalidDataException(
                $"Deserialization consumed {consumed} of {total} bytes; {total - consumed} trailing byte(s) remain. " +
                $"The frame was written with a different member layout than this type (schema drift) or contains extra data. " +
                $"Per ADR-0006, published message layouts are frozen — introduce a new MessageId type for layout changes.");
        }

        /// <summary>Nested object dispatch: routes to the type registered under the header at the current reader position. Generic headers route by (MessageId, ClassId).</summary>
        /// <remarks>
        /// Counts as one level of nesting (<see cref="MessageBufferReader.EnterNestedObject"/>) — type-parameter members and
        /// external callers' recursion tie into the reader's depth counter, preventing infinite recursion (stack overflow)
        /// from a small adversarial frame (Known-Issues KI-14).
        /// </remarks>
        public static object DeserializeFromReader(ref MessageBufferReader reader)
        {
            var unread = reader.UnreadSpan;
            if (unread.Length == 0) throw new ArgumentException("Reader has no data to deserialize.");

            uint messageId = ReadMessageIdFromHeader(unread);
            BufferReaderFunc? invoker;

            if (MessageWireFormat.IsGenericMessage(unread[0]))
            {
                if (unread.Length < MessageWireFormat.GenericIdHeaderSize)
                {
                    throw new ArgumentException($"Reader data is too short to read the {MessageWireFormat.GenericIdHeaderSize}-byte generic header.");
                }

                uint classId = (uint)unread[4] << 16 | (uint)unread[5] << 8 | unread[6];
                if (!_genericReaderDispatch.TryGetValue(GenericDispatchKey(messageId, classId), out invoker))
                {
                    throw new KeyNotFoundException($"Generic message type with ID {messageId} and ClassId {classId} is not registered.");
                }
            }
            else if (!_readerDispatch.TryGetValue(messageId, out invoker))
            {
                throw new KeyNotFoundException($"Message type with ID {messageId} is not registered.");
            }

            // This is a public entry point, so a manual implementation may keep using the same reader after an exception — pair the calls in a finally.
            reader.EnterNestedObject();
            try
            {
                return invoker!(ref reader);
            }
            finally
            {
                reader.LeaveNestedObject();
            }
        }

        static uint ReadMessageIdFromHeader(ReadOnlySpan<byte> data)
        {
            byte header = data[0];
            uint messageId = (uint)header << 24;
            if (!MessageWireFormat.HasEmbeddedMessageId(header))
            {
                return messageId;
            }
            if (data.Length < MessageWireFormat.IdHeaderSize)
            {
                throw new ArgumentException($"Message data is too short to read the {MessageWireFormat.IdHeaderSize}-byte message id.");
            }
            messageId |= (uint)data[1] << 16;
            messageId |= (uint)data[2] << 8;
            messageId |= data[3];
            return messageId;
        }

        internal static void RegisterReaderInvoker(uint messageId, BufferReaderFunc invoker)
        {
            if (!_readerDispatch.TryAdd(messageId, invoker))
            {
                throw new InvalidOperationException($"Message id {messageId} already registered for deserialization.");
            }
        }

        internal static bool TryRemoveReaderInvoker(uint messageId)
        {
            return _readerDispatch.TryRemove(messageId, out _);
        }

        internal static void RegisterGenericReaderInvoker(uint messageId, uint classId, Type type, BufferReaderFunc invoker)
        {
            ulong key = GenericDispatchKey(messageId, classId);
            var existing = _registeredGenericIds.GetOrAdd(key, type);
            if (!ReferenceEquals(existing, type))
            {
                throw new InvalidOperationException(
                    $"Generic construction with MessageId {messageId} and ClassId {classId} is already registered by '{existing.FullName}'.");
            }

            if (!_genericReaderDispatch.TryAdd(key, invoker))
            {
                _registeredGenericIds.TryRemove(key, out _);
                throw new InvalidOperationException(
                    $"Generic construction with MessageId {messageId} and ClassId {classId} is already registered for deserialization.");
            }
        }

        internal static bool TryRemoveGenericReaderInvoker(uint messageId, uint classId)
        {
            ulong key = GenericDispatchKey(messageId, classId);
            // Registration publishes owner→dispatch, so removal (rollback) goes in reverse, dispatch→owner — with the same order
            // there would be a window where the owner disappears while dispatch is still alive, letting a re-registration of the same key
            // claim the owner and have the rollback erase the new registration's dispatch (KI-38 audit FINDING 3, reached only on failed registration paths).
            bool removed = _genericReaderDispatch.TryRemove(key, out _);
            _registeredGenericIds.TryRemove(key, out _);
            return removed;
        }
    }
}
