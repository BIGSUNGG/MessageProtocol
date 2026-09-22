using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MessageProtocol.Serialize
{
    /// <summary>
    /// Static entry point for message registration, serialization, and deserialization.
    /// Generated code registers delegates and MessageIds directly from a <c>[ModuleInitializer]</c>.
    /// </summary>
    public static partial class MessageSerializer
    {
        static readonly ConcurrentDictionary<Type, byte> _registeredTypes = new();
        static readonly ConcurrentDictionary<uint, Type> _registeredMessageIds = new();

        /// <summary>Class IDs per closed generic construction. Shared by registrations from the declaration and from partial declarations elsewhere.</summary>
        static readonly ConcurrentDictionary<Type, uint> _genericClassIds = new();

        /// <summary>Looks up the class ID of a closed generic construction. Returns 0 for unregistered constructions.</summary>
        public static uint GetGenericClassId<T>() where T : IMessageSerializable<T>
        {
            return _genericClassIds.TryGetValue(typeof(T), out uint classId) ? classId : 0;
        }

        /// <summary>
        /// Fast path for registering ID messages. Fills <see cref="SerializerCache{T}"/> without reflection.
        /// </summary>
        public static void RegisterHasIdMessage<T>(
            TypedSerializeRefAction<T> serialize,
            TypedDeserializeRefFunc<T> deserialize,
            uint messageId,
            Func<T, byte[]>? serializeBytes = null,
            Func<byte[], T>? deserializeBytes = null)
            where T : IHasIdMessageSerializable<T>
        {
            if (serialize is null) throw new ArgumentNullException(nameof(serialize));
            if (deserialize is null) throw new ArgumentNullException(nameof(deserialize));

            serializeBytes ??= CreateSerializeBytesWrapper(serialize);
            deserializeBytes ??= CreateDeserializeBytesWrapper(deserialize);

            // Claim-first (KI-38): atomically claim the type **before** the prefill. With the old verify→prefill→claim order,
            // concurrent registrations of the same type with different delegates both passed validation, each prefill overwrote
            // the cache, and only the TryAdd loser failed with "already registered" — the loser's delegates (or an A/B mix)
            // remained in the authoritative SerializerCache<T>, and since dispatch invokers go through the cache, the
            // **rejected registration's serializer silently ran** (a TOCTOU recurrence of the KI-11 no-pollution class).
            // Claiming first makes the loser throw before prefill and keeps only the winner's values in the cache. The claim is rolled back on failure.
            if (!_registeredTypes.TryAdd(typeof(T), 0))
            {
                throw new InvalidOperationException($"Message type '{typeof(T).FullName}' is already registered.");
            }

            try
            {
                // Validate before the prefill — if the prefill ran first, the rejected registration's MessageId/HasId would
                // persist in SerializerCache<T> forever, and a later valid registration's prefill skips the recovery block
                // (Serialize is null), leaving the bad values in place (Known-Issues KI-11 residue, resolved 2026-09-07).
                ValidateRegistration(typeof(T), messageId, hasId: true, typeClaimed: true);

                PrefillSerializerCache(serialize, deserialize, serializeBytes, deserializeBytes, messageId, hasId: true);

                RegisterCore(typeof(T), messageId, hasId: true,
                    writer: static (object m, ref MessageBufferWriter w) => Serialize((T)m, ref w),
                    reader: static (ref MessageBufferReader r) => (object)Deserialize<T>(ref r),
                    typeClaimed: true);
            }
            catch
            {
                _registeredTypes.TryRemove(typeof(T), out _);
                throw;
            }
        }

        /// <summary>Reflection path for registering ID messages. Used for manually implemented types or when no delegates are supplied.</summary>
        public static void RegisterHasIdMessage<T>() where T : IHasIdMessageSerializable<T>
        {
            // Validate at registration time — better to fail here than to hit a null delegate later during object dispatch.
            if (SerializerCache<T>.Serialize is null) ThrowMissingSerialize<T>();

            if (!SerializerCache<T>.HasId)
            {
                throw new InvalidOperationException(
                    $"Type '{typeof(T).FullName}' is registered as a HasId message but exposes no 'public static uint MessageId' property.");
            }

            uint messageId = SerializerCache<T>.MessageId;
            ValidateRegistration(typeof(T), messageId, hasId: true);
            RegisterCore(typeof(T), messageId, hasId: true,
                writer: static (object m, ref MessageBufferWriter w) => Serialize((T)m, ref w),
                reader: SerializerCache<T>.Deserialize is null
                    ? null
                    : static (ref MessageBufferReader r) => (object)Deserialize<T>(ref r));
        }

        /// <summary>Fast path for registering NonId messages.</summary>
        public static void RegisterNonIdMessage<T>(
            TypedSerializeRefAction<T> serialize,
            TypedDeserializeRefFunc<T>? deserialize = null,
            Func<T, byte[]>? serializeBytes = null,
            Func<byte[], T>? deserializeBytes = null)
            where T : IMessageSerializable<T>
        {
            if (serialize is null) throw new ArgumentNullException(nameof(serialize));

            serializeBytes ??= CreateSerializeBytesWrapper(serialize);
            if (deserialize != null)
            {
                deserializeBytes ??= CreateDeserializeBytesWrapper(deserialize);
            }

            // As in the HasId path, validation runs before the prefill — prevents cache pollution when a duplicate registration is rejected (KI-11 residue).
            // Claim-first applies here too (KI-38): claim atomically before validation and prefill so the loser fails before prefill.
            if (!_registeredTypes.TryAdd(typeof(T), 0))
            {
                throw new InvalidOperationException($"Message type '{typeof(T).FullName}' is already registered.");
            }

            try
            {
                ValidateRegistration(typeof(T), 0u, hasId: false, typeClaimed: true);

                PrefillSerializerCache(serialize, deserialize, serializeBytes, deserializeBytes, messageId: 0u, hasId: false);

                RegisterCore(typeof(T), 0u, hasId: false,
                    writer: static (object m, ref MessageBufferWriter w) => Serialize((T)m, ref w),
                    reader: null,
                    typeClaimed: true);
            }
            catch
            {
                _registeredTypes.TryRemove(typeof(T), out _);
                throw;
            }
        }

        /// <summary>
        /// Registers a closed generic construction. Publishes the writer and reader under the (MessageId, ClassId) key so
        /// object dispatch works on both the sending and receiving sides.
        /// </summary>
        public static void RegisterGenericConstruction<T>(uint classId) where T : IHasIdMessageSerializable<T>
        {
            if (classId == 0 || classId > MessageWireFormat.MessageIdValueMask)
            {
                throw new ArgumentOutOfRangeException(nameof(classId),
                    $"ClassId must be between 1 and {MessageWireFormat.MessageIdValueMask} (2^24 - 1).");
            }

            if (!SerializerCache<T>.HasId)
            {
                throw new InvalidOperationException(
                    $"Type '{typeof(T).FullName}' is registered as a generic construction but exposes no 'public static uint MessageId' property.");
            }

            if (SerializerCache<T>.Serialize is null) ThrowMissingSerialize<T>();

            var deserialize = SerializerCache<T>.Deserialize
                ?? throw new InvalidOperationException(
                    $"Type '{typeof(T).FullName}' has no 'public static {typeof(T).Name} Deserialize(ref MessageBufferReader)' method; generic constructions require it.");

            uint messageId = SerializerCache<T>.MessageId;

            if (!_registeredTypes.TryAdd(typeof(T), 0))
            {
                throw new InvalidOperationException($"Message type '{typeof(T).FullName}' is already registered.");
            }

            bool classIdRecorded = false;
            bool writerRegistered = false;
            bool readerRegistered = false;
            try
            {
                // Publication order: classId → writer → reader. The generated write path reads GetGenericClassId<T>;
                // if the writer invoker were published first, Serialize entering via object dispatch would read classId 0
                // and throw a "not registered" exception (a race between manual eager registration and serialization — audit ledger MEDIUM, resolved 2026-09-07).
                // Publishing classId first guarantees it is visible the moment the writer is.
                _genericClassIds[typeof(T)] = classId;
                classIdRecorded = true;

                RegisterWriterInvoker(typeof(T), static (object m, ref MessageBufferWriter w) => Serialize((T)m, ref w));
                writerRegistered = true;

                RegisterGenericReaderInvoker(messageId, classId, typeof(T),
                    (ref MessageBufferReader r) => (object)deserialize(ref r)!);
                readerRegistered = true;
            }
            catch
            {
                if (readerRegistered) TryRemoveGenericReaderInvoker(messageId, classId);
                if (writerRegistered) TryRemoveWriterInvoker(typeof(T));
                if (classIdRecorded) _genericClassIds.TryRemove(typeof(T), out _);
                _registeredTypes.TryRemove(typeof(T), out _);
                throw;
            }
        }

        /// <summary>Reflection path for registering NonId messages.</summary>
        public static void RegisterNonIdMessage<T>() where T : IMessageSerializable<T>
        {
            if (SerializerCache<T>.Serialize is null) ThrowMissingSerialize<T>();

            RegisterCore(typeof(T), 0u, hasId: false,
                writer: static (object m, ref MessageBufferWriter w) => Serialize((T)m, ref w),
                reader: null);
        }

        /// <summary>
        /// Reflection-based registration. The type must implement <see cref="IMessageSerializable{T}"/> or
        /// <see cref="IHasIdMessageSerializable{T}"/>.
        /// </summary>
        public static void RegisterType(Type type)
        {
            if (type is null) throw new ArgumentNullException(nameof(type));
            if (type.IsGenericTypeDefinition)
            {
                throw new ArgumentException($"Open generic type '{type.FullName}' cannot be registered.", nameof(type));
            }

            var iHasId = type.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IHasIdMessageSerializable<>));

            var iMessage = type.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMessageSerializable<>));

            if (iHasId == null && iMessage == null)
            {
                throw new InvalidOperationException(
                    $"Type '{type.FullName}' does not implement 'IMessageSerializable<{type.Name}>'. " +
                    $"This usually means the source generator (MessageProtocol.CodeGenerator) did not generate the required partial class implementation.");
            }

            string methodName = iHasId != null ? nameof(RegisterHasIdMessage) : nameof(RegisterNonIdMessage);
            // Pick only the parameterless overload (to distinguish it from the delegate-taking overloads).
            var generic = typeof(MessageSerializer)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == methodName
                            && m.IsGenericMethodDefinition
                            && m.GetParameters().Length == 0)
                .MakeGenericMethod(type);
            try
            {
                generic.Invoke(null, null);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }

        static Func<T, byte[]> CreateSerializeBytesWrapper<T>(TypedSerializeRefAction<T> serialize)
        {
            return message =>
            {
                var writer = MessageBufferWriter.Create();
                try
                {
                    serialize(message, ref writer);
                    return writer.ToArray();
                }
                finally
                {
                    writer.Dispose();
                }
            };
        }

        static Func<byte[], T> CreateDeserializeBytesWrapper<T>(TypedDeserializeRefFunc<T> deserialize)
        {
            return data =>
            {
                var reader = new MessageBufferReader(data);
                return deserialize(ref reader);
            };
        }

        /// <summary>
        /// Plants the delegates into the prefill holder, then runs the <see cref="SerializerCache{T}"/> cctor to skip reflection.
        /// Because the holder is a separate type, the cache's cctor is not triggered while it is being set up. If the cctor already ran
        /// (early access before registration), the CLR will not run it again, so the cache fields are filled directly to recover.
        /// </summary>
        static void PrefillSerializerCache<T>(
            TypedSerializeRefAction<T> serialize,
            TypedDeserializeRefFunc<T>? deserialize,
            Func<T, byte[]> serializeBytes,
            Func<byte[], T>? deserializeBytes,
            uint messageId,
            bool hasId)
        {
            SerializerCachePrefill<T>.Serialize = serialize;
            SerializerCachePrefill<T>.Deserialize = deserialize;
            SerializerCachePrefill<T>.SerializeBytes = serializeBytes;
            SerializerCachePrefill<T>.DeserializeBytes = deserializeBytes;
            SerializerCachePrefill<T>.MessageId = messageId;
            SerializerCachePrefill<T>.HasId = hasId;
            // The volatile write is a release store — publishes the fields above so they become visible to other threads before IsSet=true (KI-11).
            SerializerCachePrefill<T>.IsSet = true;

            RuntimeHelpers.RunClassConstructor(typeof(SerializerCache<T>).TypeHandle);

            // If the cache was already initialized before registration (early access), the cctor never saw the Prefill and the CLR will not re-run it —
            // the cache fields are not readonly, so fill them directly here to recover. Without this step the type would be unusable forever (KI-11).
            if (SerializerCache<T>.Serialize is null)
            {
                // The fields are volatile, so each assignment is a release store — the previous `Volatile.Write(Serialize)` batch publication
                // was only valid for readers that read Serialize first, and did not pair with hot paths that read only Deserialize (KI-39).
                SerializerCache<T>.Deserialize = deserialize;
                SerializerCache<T>.SerializeBytes = serializeBytes;
                SerializerCache<T>.DeserializeBytes = deserializeBytes;
                SerializerCache<T>.MessageId = messageId;
                SerializerCache<T>.HasId = hasId;
                SerializerCache<T>.Serialize = serialize;
            }
        }

        /// <summary>
        /// Validates rejection conditions before the registration is published — no side effects (touches neither dispatch nor cache).
        /// If the prefill ran before this validation, the rejected registration's MessageId/HasId would persist in
        /// <see cref="SerializerCache{T}"/> forever (Known-Issues KI-11 residue). RegisterCore's atomic claims (TryAdd/GetOrAdd) remain in place
        /// and handle concurrent registration races between validation and publication.
        /// </summary>
        static void ValidateRegistration(Type type, uint messageId, bool hasId, bool typeClaimed = false)
        {
            // On the claim-first registration path (KI-38) this registration attempt already claimed the type, so skip the duplicate check.
            if (!typeClaimed && _registeredTypes.ContainsKey(type))
            {
                throw new InvalidOperationException($"Message type '{type.FullName}' is already registered.");
            }

            if (!hasId)
            {
                return;
            }

            byte headerByte = (byte)(messageId >> 24);
            if (MessageWireFormat.IsGenericMessage(headerByte))
            {
                throw new InvalidOperationException(
                    $"Message type '{type.FullName}' uses the generic header flag; register generic constructions with '{nameof(RegisterGenericConstruction)}' instead.");
            }

            if (MessageWireFormat.HasEmbeddedMessageId(headerByte))
            {
                if (_registeredMessageIds.TryGetValue(messageId, out var existing) && !ReferenceEquals(existing, type))
                {
                    throw new InvalidOperationException(
                        $"Message type with ID {messageId} is already registered by '{existing.FullName}'.");
                }

                return;
            }

            // A hasId registration whose header carries the NonId bit — previously the id/reader registration was silently skipped, leaving only object
            // serialization working, and Deserialize(object) later failed with a cause-less KeyNotFoundException. Now reported at registration time.
            throw new InvalidOperationException(
                $"Message type '{type.FullName}' is registered as a HasId message but its MessageId 0x{messageId:X8} carries the NonId flag, so the wire header would embed no message id. " +
                $"Register NonId messages with '{nameof(RegisterNonIdMessage)}' instead, or compose the id with Standalone/Group flags.");
        }

        static void RegisterCore(Type type, uint messageId, bool hasId, BufferWriterAction writer, BufferReaderFunc? reader, bool typeClaimed = false)
        {
            // The caller (delegate path, KI-38) may already have claimed the type before the prefill — skip here because a second claim would fail.
            if (!typeClaimed && !_registeredTypes.TryAdd(type, 0))
            {
                throw new InvalidOperationException($"Message type '{type.FullName}' is already registered.");
            }

            bool writerRegistered = false;
            bool messageIdRegistered = false;
            bool readerRegistered = false;
            try
            {
                RegisterWriterInvoker(type, writer);
                writerRegistered = true;

                if (hasId)
                {
                    byte headerByte = (byte)(messageId >> 24);
                    if (MessageWireFormat.IsGenericMessage(headerByte))
                    {
                        throw new InvalidOperationException(
                            $"Message type '{type.FullName}' uses the generic header flag; register generic constructions with '{nameof(RegisterGenericConstruction)}' instead.");
                    }

                    if (MessageWireFormat.HasEmbeddedMessageId(headerByte))
                    {
                        var existing = _registeredMessageIds.GetOrAdd(messageId, type);
                        if (!ReferenceEquals(existing, type))
                        {
                            throw new InvalidOperationException(
                                $"Message type with ID {messageId} is already registered by '{existing.FullName}'.");
                        }
                        messageIdRegistered = true;

                        if (reader != null)
                        {
                            RegisterReaderInvoker(messageId, reader);
                            readerRegistered = true;
                        }
                    }
                }
            }
            catch
            {
                _registeredTypes.TryRemove(type, out _);
                if (writerRegistered) TryRemoveWriterInvoker(type);
                if (readerRegistered) TryRemoveReaderInvoker(messageId);
                if (messageIdRegistered) _registeredMessageIds.TryRemove(messageId, out _);
                throw;
            }
        }
    }
}
