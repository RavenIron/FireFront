// net472 predates the interpolated-string-handler attributes; defining them
// ourselves is the supported way to use the C# 10 feature on old target
// frameworks — the compiler only cares that a type with this exact name
// exists, not which assembly declares it.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    internal sealed class InterpolatedStringHandlerAttribute : Attribute
    {
    }
}
