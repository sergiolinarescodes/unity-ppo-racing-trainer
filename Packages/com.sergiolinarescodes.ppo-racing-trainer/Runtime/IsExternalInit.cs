namespace System.Runtime.CompilerServices
{
    // Public so consumers compiling against this package can use C# 9 record
    // types + init-only setters without having to add their own shim. An
    // internal shim would only satisfy the package's own assembly.
    public class IsExternalInit
    {
    }
}
