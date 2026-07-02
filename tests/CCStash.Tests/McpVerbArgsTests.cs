using CCStash.Verbs;

namespace CCStash.Tests;

public class McpVerbArgsTests
{
    [Fact]
    public void WantsHttp_false_when_flag_absent()
    {
        Assert.False(McpVerb.WantsHttp([]));
        Assert.False(McpVerb.WantsHttp(["--project", "/tmp/foo"]));
    }

    [Fact]
    public void WantsHttp_true_when_flag_present()
    {
        Assert.True(McpVerb.WantsHttp(["--http"]));
        Assert.True(McpVerb.WantsHttp(["--project", "/tmp/foo", "--http", "--port", "9000"]));
    }

    [Fact]
    public void ResolvePort_defaults_when_flag_absent()
    {
        Assert.Equal(McpVerb.DefaultHttpPort, McpVerb.ResolvePort([]));
        Assert.Equal(McpVerb.DefaultHttpPort, McpVerb.ResolvePort(["--http"]));
    }

    [Fact]
    public void ResolvePort_uses_explicit_value()
    {
        Assert.Equal(9000, McpVerb.ResolvePort(["--http", "--port", "9000"]));
    }

    [Fact]
    public void ResolvePort_falls_back_to_default_on_invalid_value()
    {
        Assert.Equal(McpVerb.DefaultHttpPort, McpVerb.ResolvePort(["--port", "not-a-number"]));
    }
}
