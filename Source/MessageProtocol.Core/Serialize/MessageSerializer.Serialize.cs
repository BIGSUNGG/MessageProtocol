using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace MessageProtocol.Serialize
{
    public static partial class MessageSerializer
    {
        /// <summary>Dispatch table finding the per-type serialize delegate for the object path.</summary>
        static readonly ConcurrentDictionary<Type, BufferWriterAction> _writerDispatch = new();

        /// <summary>object-path serialize delegate.</summary>
        public delegate void BufferWriterAction(object message, ref MessageBufferWriter writer);

        /// <summary>
        /// Generic hot path. Serializes with a single call through the static cache delegate (no dictionary lookup, no boxing).
        /// Based on the declared type; use <see cref="Serialize(object)"/> when derived-type polymorphism is needed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T>(T message, ref MessageBufferWriter writer) where T : IMessageSerializable<T>
        {
            if (message is null) throw new ArgumentNullException(nameof(message));
            var serialize = SerializerCache<T>.Serialize;
            if (serialize is null) ThrowMissingSerialize<T>();
            serialize!(message, ref writer);
        }

        /// <summary>Generic path: returns a byte[] for compatibility.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(T message) where T : IMessageSerializable<T>
        {
            if (message is null) throw new ArgumentNullException(nameof(message));
            var serializeBytes = SerializerCache<T>.SerializeBytes;
            if (serializeBytes is null) ThrowMissingSerialize<T>();
            return serializeBytes!(message);
        }

        /// <summary>Generic path: returns an ArrayPool-backed <see cref="PooledBuffer"/>. The caller must Dispose it.</summary>
        public static PooledBuffer SerializePooled<T>(T message) where T : IMessageSerializable<T>
        {
            if (message is null) throw new ArgumentNullException(nameof(message));
            var serialize = SerializerCache<T>.Serialize;
            if (serialize is null) ThrowMissingSerialize<T>();
            var writer = MessageBufferWriter.Create();
            try
            {
                serialize!(message, ref writer);
                return writer.ToPooledBuffer();
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Reports a missing serialize delegate in the cache. Reported here instead of throwing from the <see cref="SerializerCache{T}"/> cctor,
        /// so the CLR does not permanently cache the initialization failure per type and a later delegate registration can recover (Known-Issues KI-11).
        /// </summary>
        static void ThrowMissingSerialize<T>()
        {
            throw new InvalidOperationException(
                $"Type '{typeof(T).FullName}' has no serialize method. " +
                $"Ensure the type is generated via MessageProtocol.CodeGenerator or defines " +
                $"'public static void Serialize({typeof(T).Name}, ref MessageBufferWriter)' and " +
                $"'public static byte[] Serialize({typeof(T).Name})', and that it is registered " +
                $"(MessageSerializer.RegisterNonIdMessage / RegisterHasIdMessage) before first use.");
        }

        /// <summary>object dispatch path: serializes by runtime type (polymorphism). Returns a byte[] for compatibility.</summary>
        public static byte[] Serialize(object message)
        {
            if (message is null) throw new ArgumentNullException(nameof(message));
            var writer = MessageBufferWriter.Create();
            try
            {
                var invoker = GetWriterInvoker(message.GetType());
                invoker(message, ref writer);
                return writer.ToArray();
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>object dispatch path: returns a <see cref="PooledBuffer"/>.</summary>
        public static PooledBuffer SerializePooled(object message)
        {
            if (message is null) throw new ArgumentNullException(nameof(message));
            var writer = MessageBufferWriter.Create();
            try
            {
                var invoker = GetWriterInvoker(message.GetType());
                invoker(message, ref writer);
                return writer.ToPooledBuffer();
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        /// <summary>object dispatch: writes directly into the given writer (for nested messages).</summary>
        /// <remarks>
        /// Counts as one level of nesting (<see cref="MessageBufferWriter.EnterNestedObject"/>) — recursion from type-parameter and
        /// abstract-message members and from manual implementations ties into the writer's depth counter, stopping cyclic graphs on
        /// the dispatch path (which does not track back-references) from exhausting the recursion stack (uncatchable stack overflow) (Known-Issues KI-25).
        /// </remarks>
        public static void SerializeToWriter(object message, ref MessageBufferWriter writer)
        {
            if (message is null) throw new ArgumentNullException(nameof(message));
            var invoker = GetWriterInvoker(message.GetType());

            // This is a public entry point, so the caller may keep writing to the same writer after an exception — pair the calls in a finally.
            writer.EnterNestedObject();
            try
            {
                invoker(message, ref writer);
            }
            finally
            {
                writer.LeaveNestedObject();
            }
        }

        static readonly object _lazyRegisterLock = new();

        static BufferWriterAction GetWriterInvoker(Type messageType)
        {
            if (_writerDispatch.TryGetValue(messageType, out var invoker))
            {
                return invoker;
            }

            // Lazy registration for manually implemented types whose ModuleInitializer has not run.
            lock (_lazyRegisterLock)
            {
                if (_writerDispatch.TryGetValue(messageType, out invoker))
                {
                    return invoker;
                }

                try
                {
                    RegisterType(messageType);
                }
                catch (InvalidOperationException) when (_writerDispatch.ContainsKey(messageType))
                {
                    // Already registered by a race with another thread.
                }
            }

            if (_writerDispatch.TryGetValue(messageType, out invoker))
            {
                return invoker;
            }

            throw new InvalidOperationException(
                $"Type '{messageType.FullName}' is not registered for serialization. " +
                $"Ensure the type is generated via MessageProtocol.CodeGenerator and referenced so its ModuleInitializer runs, " +
                $"or call MessageSerializer.RegisterType(typeof({messageType.Name})) manually.");
        }

        internal static void RegisterWriterInvoker(Type type, BufferWriterAction invoker)
        {
            if (!_writerDispatch.TryAdd(type, invoker))
            {
                throw new InvalidOperationException($"Type '{type.FullName}' already registered for serialization.");
            }
        }

        internal static bool TryRemoveWriterInvoker(Type type)
        {
            return _writerDispatch.TryRemove(type, out _);
        }
    }
}
