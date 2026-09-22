using System;
using System.Reflection;

namespace MessageProtocol.Serialize
{
    public static partial class MessageSerializer
    {
        /// <summary>Ref-based serialize delegate called by the generic hot path.</summary>
        public delegate void TypedSerializeRefAction<T>(T message, ref MessageBufferWriter writer);

        /// <summary>Ref-based deserialize delegate called by the generic hot path.</summary>
        public delegate T TypedDeserializeRefFunc<T>(ref MessageBufferReader reader);

        /// <summary>
        /// Holder for planting delegates before the <see cref="SerializerCache{T}"/> cctor runs.
        /// Being a separate type, it does not trigger the cache's cctor during Prefill.
        /// </summary>
        static class SerializerCachePrefill<T>
        {
            public static TypedSerializeRefAction<T>? Serialize;
            public static TypedDeserializeRefFunc<T>? Deserialize;
            public static Func<T, byte[]>? SerializeBytes;
            public static Func<byte[], T>? DeserializeBytes;
            public static uint MessageId;
            public static bool HasId;

            // volatile = release store. The cctor reads only this flag before the other fields, so publication is tied here to
            // stop a concurrent cctor from seeing only IsSet=true and freezing a **torn state** (delegates still null) into the cache
            // (hard to observe on x86, but Unity ARM can reorder store-store — Known-Issues KI-11).
            public static volatile bool IsSet;
        }

        /// <summary>
        /// Per-type-argument static cache. Filled without reflection when prefilled at registration,
        /// or by one-time reflection on first access otherwise.
        /// <para>
        /// Fields are not <c>readonly</c> and the cctor **never throws** — for the same reason. If the cctor threw, the CLR would
        /// cache that failure per type permanently, unrecoverable by any later successful delegate registration (`TypeInitializationException`);
        /// if the fields were readonly, early access before registration would run the cctor first and the Prefill would be ignored forever (Known-Issues KI-11).
        /// Unresolved members stay null and are reported with clear messages at their use sites.
        /// </para>
        /// </summary>
        internal static class SerializerCache<T>
        {
            // volatile (KI-39): for types whose cctor ran first via early access before registration, the **recovery block**
            // (PrefillSerializerCache) rewrites these fields from outside the cctor. That recovery writes publish `Serialize`
            // with release semantics last, but hot paths that read only `Deserialize` (Deserialize<T>) do not pair with that release,
            // so on ARM (Unity) they could read a **stale null** even after registration completed, causing false ThrowMissingDeserialize or MessageId=0.
            // Making every field volatile gives each write release and each read acquire semantics, so per-location synchronization pairs hold.
            // Cost: ARM64 acquire load ≈ 1 cycle (LDAR), free on x86 — negligible in the hot path.
            public static volatile TypedSerializeRefAction<T>? Serialize;
            public static volatile TypedDeserializeRefFunc<T>? Deserialize;
            public static volatile Func<T, byte[]>? SerializeBytes;
            public static volatile Func<byte[], T>? DeserializeBytes;
            public static volatile uint MessageId;
            public static volatile bool HasId;

            static SerializerCache()
            {
                if (SerializerCachePrefill<T>.IsSet)
                {
                    Serialize = SerializerCachePrefill<T>.Serialize;
                    Deserialize = SerializerCachePrefill<T>.Deserialize;
                    SerializeBytes = SerializerCachePrefill<T>.SerializeBytes;
                    DeserializeBytes = SerializerCachePrefill<T>.DeserializeBytes;
                    MessageId = SerializerCachePrefill<T>.MessageId;
                    HasId = SerializerCachePrefill<T>.HasId;
                    return;
                }

                Type type = typeof(T);

                // Leave unfound members null — throwing here would make the CLR permanently cache the per-type
                // initialization failure, becoming a TypeInitializationException that no later delegate registration can recover from (KI-11).
                Serialize = TryCreateDelegate<TypedSerializeRefAction<T>>(TryResolveSerializeRefMethod(type));
                SerializeBytes = TryCreateDelegate<Func<T, byte[]>>(TryResolveSerializeBytesMethod(type));
                Deserialize = TryCreateDelegate<TypedDeserializeRefFunc<T>>(TryResolveDeserializeRefMethod(type));
                DeserializeBytes = TryCreateDelegate<Func<byte[], T>>(TryResolveDeserializeBytesMethod(type));

                if (TryResolveMessageIdGetter(type, out uint id))
                {
                    MessageId = id;
                    HasId = true;
                }
            }
        }

        /// <summary>Creates a delegate from a static member found via reflection. Returns null when the member is absent (the cctor-never-throws rule).</summary>
        static TDelegate? TryCreateDelegate<TDelegate>(MethodInfo? method) where TDelegate : class
        {
            return method is null ? null : (TDelegate)(object)method.CreateDelegate(typeof(TDelegate));
        }

        static readonly Type ByRefBufferWriterType = typeof(MessageBufferWriter).MakeByRefType();
        static readonly Type ByRefBufferReaderType = typeof(MessageBufferReader).MakeByRefType();

        /// <summary>Finds `static void Serialize(T, ref MessageBufferWriter)`. Returns null when absent — if the cctor threw, the CLR would cache the failure permanently (KI-11).</summary>
        static MethodInfo? TryResolveSerializeRefMethod(Type type)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "Serialize") continue;
                if (method.ReturnType != typeof(void)) continue;
                if (method.IsGenericMethodDefinition) continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 2) continue;
                if (parameters[0].ParameterType != type) continue;
                if (parameters[1].ParameterType != ByRefBufferWriterType) continue;
                return method;
            }
            return null;
        }

        static MethodInfo? TryResolveDeserializeRefMethod(Type type)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "Deserialize") continue;
                if (method.ReturnType != type) continue;
                if (method.IsGenericMethodDefinition) continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1) continue;
                if (parameters[0].ParameterType != ByRefBufferReaderType) continue;
                return method;
            }
            return null;
        }

        /// <summary>Finds `static byte[] Serialize(T)`. Returns null when absent (same rationale as <see cref="TryResolveSerializeRefMethod"/>).</summary>
        static MethodInfo? TryResolveSerializeBytesMethod(Type type)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "Serialize") continue;
                if (method.ReturnType != typeof(byte[])) continue;
                if (method.IsGenericMethodDefinition) continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1) continue;
                if (parameters[0].ParameterType != type) continue;
                return method;
            }
            return null;
        }

        static MethodInfo? TryResolveDeserializeBytesMethod(Type type)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "Deserialize") continue;
                if (method.ReturnType != type) continue;
                if (method.IsGenericMethodDefinition) continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1) continue;
                if (parameters[0].ParameterType != typeof(byte[])) continue;
                return method;
            }
            return null;
        }

        static bool TryResolveMessageIdGetter(Type type, out uint messageId)
        {
            var property = type.GetProperty(
                "MessageId",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (property != null && property.PropertyType == typeof(uint) && property.CanRead)
            {
                messageId = (uint)property.GetValue(null)!;
                return true;
            }

            messageId = 0;
            return false;
        }
    }
}
