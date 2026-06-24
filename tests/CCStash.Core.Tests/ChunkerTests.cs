using CCStash.Core.Chunking;
using CCStash.Core.Distillation;

namespace CCStash.Core.Tests;

public class ChunkerTests
{
    [Fact]
    public void Short_turn_becomes_one_chunk()
    {
        var turns = new[] { new DistilledTurn(0, "user", DateTimeOffset.UnixEpoch, "hello world") };
        var chunks = new Chunker().Chunk(turns, new ChunkOptions(MaxChars: 100));

        Assert.Single(chunks);
        Assert.Equal("turn", chunks[0].Type);
        Assert.Equal(0, chunks[0].TurnIndex);
    }

    [Fact]
    public void Long_turn_splits_into_multiple_parts_preserving_turn_index()
    {
        var turns = new[] { new DistilledTurn(3, "assistant", null, new string('a', 250)) };
        var chunks = new Chunker().Chunk(turns, new ChunkOptions(MaxChars: 100));

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.Equal(3, c.TurnIndex));
        Assert.All(chunks, c => Assert.Equal("turn-part", c.Type));
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 100));
    }
}
