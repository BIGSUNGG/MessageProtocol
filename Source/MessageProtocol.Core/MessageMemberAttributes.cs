using System;

namespace MessageProtocol
{
    /// <summary>Excludes the member from serialization.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public class MessageIgnoreAttribute : Attribute
    {
    }

    /// <summary>Includes a non-public member in serialization.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public class MessageIncludeAttribute : Attribute
    {
    }
}
