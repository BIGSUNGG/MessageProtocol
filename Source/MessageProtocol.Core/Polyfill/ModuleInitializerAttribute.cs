#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>Polyfill for netstandard2.1 targets. On net6+ the runtime-provided type is used.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class ModuleInitializerAttribute : Attribute
    {
    }
}
#endif
