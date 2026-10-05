using Ikd.Cli;

namespace Ikd.Tests;

public static class ReplTests
{
    [Test]
    public static void DepthCountsBraces()
    {
        Assert.Equal(1, Repl.DeltaDepth("class A {"));
        Assert.Equal(-1, Repl.DeltaDepth("}"));
        Assert.Equal(0, Repl.DeltaDepth("println(\"{\")"));
        Assert.Equal(0, Repl.DeltaDepth("fn f() { return 1; }"));
    }
}
