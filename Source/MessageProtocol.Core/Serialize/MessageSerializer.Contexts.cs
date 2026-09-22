using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;

namespace MessageProtocol.Serialize
{
    public static partial class MessageSerializer
    {
        /// <summary>
        /// Reference-type tags for the forward-only wire format. Values 0/1/2 are part of the wire spec and must not change.
        /// </summary>
        public enum ReferenceKind : byte
        {
            Null = 0,
            NewObject = 1,
            BackReference = 2,
        }

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new();

            bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);

            int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        /// <summary>
        /// Context tracking shared and cyclic references during a single serialization.
        /// The first object uses only the slot; the Dictionary is allocated from the second registration on.
        /// </summary>
        public struct SerializeContext
        {
            object? _firstObject;
            Dictionary<object, int>? _objectIds;
            int _nextObjectId;

            /// <summary>
            /// Finds the id of an already-registered object. Rejects null — a null reference must be written as
            /// <see cref="ReferenceKind.Null"/>; if it were used as a lookup target it would become indistinguishable from
            /// the empty-slot sentinel `_firstObject is null` (Known-Issues KI-30).
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryGetObjectId(object value, out int objectId)
            {
                if (value is null) ThrowNullReferenceValue(nameof(value));

                if (_objectIds is not null)
                {
                    return _objectIds.TryGetValue(value, out objectId);
                }

                if (_firstObject is not null && ReferenceEquals(_firstObject, value))
                {
                    objectId = 1;
                    return true;
                }

                objectId = 0;
                return false;
            }

            /// <summary>
            /// Registers a new object and returns its assigned id. Ids start at 1.
            /// Rejects null — registering null would leave the first slot empty so **the next object also gets id 1**, and
            /// back-references would point at the wrong object (silent object-graph corruption) — Known-Issues KI-30.
            /// </summary>
            public int RegisterObject(object value)
            {
                if (value is null) ThrowNullReferenceValue(nameof(value));

                if (_objectIds is not null)
                {
                    int id = _nextObjectId++;
                    _objectIds[value] = id;
                    return id;
                }

                if (_firstObject is null)
                {
                    _firstObject = value;
                    _nextObjectId = 2;
                    return 1;
                }

                // Initial capacity 8: skips the first resizes (1→3→7) for typical object graphs (2–8 tracked objects).
                // The dictionary is allocated only on first-slot promotion, so there is no cost for empty graphs (2026-09-08 hot-path audit FINDING 3).
                _objectIds = new Dictionary<object, int>(8, ReferenceComparer.Instance)
                {
                    [_firstObject] = 1,
                };
                _firstObject = null;
                int promotedId = _nextObjectId++;
                _objectIds[value] = promotedId;
                return promotedId;
            }
        }

        /// <summary>
        /// Table restoring id → object dereferences during a single deserialization.
        /// The first object uses only the slot; the Dictionary is allocated from the second registration on.
        /// </summary>
        public struct DeserializeContext
        {
            object? _firstObject;
            Dictionary<int, object>? _objects;
            int _nextObjectId;

            /// <summary>
            /// Registers a newly created object and returns its id (must match the serialization-time order).
            /// Rejects null — for the same reason as <see cref="SerializeContext.RegisterObject"/>, id 1 would be issued twice and
            /// <see cref="GetObject"/> would resolve back-references to the wrong instance (Known-Issues KI-30).
            /// </summary>
            public int RegisterNewObject(object value)
            {
                if (value is null) ThrowNullReferenceValue(nameof(value));

                if (_objects is not null)
                {
                    int id = _nextObjectId++;
                    _objects[id] = value;
                    return id;
                }

                if (_firstObject is null)
                {
                    _firstObject = value;
                    _nextObjectId = 2;
                    return 1;
                }

                _objects = new Dictionary<int, object>(8) // initial capacity 8 — defers resizes (same rationale as above).
                {
                    [1] = _firstObject,
                };
                _firstObject = null;
                int promotedId = _nextObjectId++;
                _objects[promotedId] = value;
                return promotedId;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public object GetObject(int objectId)
            {
                if (_objects is not null)
                {
                    if (!_objects.TryGetValue(objectId, out var value))
                    {
                        ThrowMissingObject(objectId);
                    }
                    return value!;
                }

                if (objectId == 1 && _firstObject is not null)
                {
                    return _firstObject;
                }

                ThrowMissingObject(objectId);
                return null!;
            }

            static void ThrowMissingObject(int id)
            {
                throw new InvalidDataException($"Back-reference to object id {id} could not be resolved.");
            }
        }

        /// <summary>
        /// Null rejection shared by the reference-tracking contexts — a single rationale for both.
        /// `_firstObject is null` is the "empty slot" sentinel, so registering or looking up null leaves the slot unoccupied,
        /// **id 1 is issued twice**, and back-references resolve to the wrong instance (Known-Issues KI-30).
        /// </summary>
        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNullReferenceValue(string paramName)
        {
            throw new ArgumentNullException(paramName,
                "A null reference is serialized as ReferenceKind.Null and must not be registered in or looked up from " +
                "the reference-tracking context: registering null leaves the first slot empty, so object id 1 is issued " +
                "twice and back-references resolve to the wrong instance.");
        }
    }
}
