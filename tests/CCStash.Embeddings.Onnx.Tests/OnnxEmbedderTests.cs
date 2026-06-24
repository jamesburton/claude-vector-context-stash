using CCStash.Embeddings.Onnx;

namespace CCStash.Embeddings.Onnx.Tests;

public class OnnxEmbedderTests
{
    private static string? ModelDir =>
        Environment.GetEnvironmentVariable("CCSTASH_MODEL_DIR")
        ?? DefaultDir();

    private static string? DefaultDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "ccstash", "models", "all-MiniLM-L6-v2");
        return File.Exists(Path.Combine(dir, "model.onnx")) ? dir : null;
    }

    // These are integration tests over a ~90 MB local model that lives outside the repo
    // (under ~/.claude/ccstash/models). xUnit 2.9 has no dynamic skip, so when the model is
    // absent they no-op cleanly rather than fail a fresh clone; the rest of the suite is strict.
    [Fact]
    public async Task Dimension_is_384_and_vectors_are_normalized()
    {
        if (ModelDir is null)
        {
            return;
        }

        using var e = await OnnxEmbedder.LoadAsync(ModelDir!);

        var v = await e.EmbedAsync("hello world");

        Assert.Equal(384, e.Dimension);
        Assert.Equal(384, v.Length);
        Assert.Equal(1f, MathF.Sqrt(v.Sum(x => x * x)), 2);
    }

    [Fact]
    public async Task Similar_sentences_score_higher_than_unrelated()
    {
        if (ModelDir is null)
        {
            return;
        }

        using var e = await OnnxEmbedder.LoadAsync(ModelDir!);

        var cat = await e.EmbedAsync("the cat sat on the mat");
        var kitten = await e.EmbedAsync("a kitten rested on a rug");
        var finance = await e.EmbedAsync("quarterly tax accounting report");

        Assert.True(Dot(cat, kitten) > Dot(cat, finance),
            $"expected kitten ({Dot(cat, kitten):F3}) > finance ({Dot(cat, finance):F3})");
    }

    [Fact]
    public async Task Is_deterministic()
    {
        if (ModelDir is null)
        {
            return;
        }

        using var e = await OnnxEmbedder.LoadAsync(ModelDir!);

        var a = await e.EmbedAsync("deterministic check");
        var b = await e.EmbedAsync("deterministic check");

        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Batched_embeddings_match_single_embeddings()
    {
        if (ModelDir is null)
        {
            return;
        }

        using var e = await OnnxEmbedder.LoadAsync(ModelDir!);

        var texts = new[] { "short", "a noticeably longer sentence about cats and mats", "another one" };
        var batch = await e.EmbedBatchAsync(texts);

        for (var i = 0; i < texts.Length; i++)
        {
            var single = await e.EmbedAsync(texts[i]);

            // Padding to the batch maximum is masked out of attention, so batched and single-row
            // results are the same vector up to floating-point noise (cosine ~1).
            Assert.Equal(1f, Dot(batch[i], single), 3);
        }
    }

    private static float Dot(float[] a, float[] b)
    {
        float d = 0;
        for (var i = 0; i < a.Length; i++)
        {
            d += a[i] * b[i];
        }

        return d;
    }
}
