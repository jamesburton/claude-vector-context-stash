using CCStash.Core.Embeddings;

namespace CCStash.Core.Tests;

public class FakeEmbedderTests
{
    [Fact]
    public async Task Embed_is_deterministic_and_normalized()
    {
        var e = new FakeEmbedder(8);
        var a = await e.EmbedAsync("hello");
        var b = await e.EmbedAsync("hello");

        Assert.Equal(8, e.Dimension);
        Assert.Equal(a, b);
        Assert.Equal(1f, MathF.Sqrt(a.Sum(x => x * x)), 3);
    }
}
