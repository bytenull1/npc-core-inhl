// ReSharper disable CheckNamespace
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Compiler marker for `init` accessors (records use them). netstandard2.1 does not ship
    /// it, and the runtime never looks at it.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}
