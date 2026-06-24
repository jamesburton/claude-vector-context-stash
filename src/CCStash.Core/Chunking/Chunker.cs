using CCStash.Core.Distillation;

namespace CCStash.Core.Chunking;

/// <summary>Default per-turn chunker; oversized turns split into ordered parts.</summary>
public sealed class Chunker : IChunker
{
    /// <inheritdoc/>
    public IReadOnlyList<Chunk> Chunk(IReadOnlyList<DistilledTurn> turns, ChunkOptions options)
    {
        var chunks = new List<Chunk>();

        foreach (var turn in turns)
        {
            if (turn.Text.Length <= options.MaxChars)
            {
                chunks.Add(new Chunk(turn.Index, turn.Role, turn.Timestamp, "turn", turn.Text));
                continue;
            }

            for (var offset = 0; offset < turn.Text.Length; offset += options.MaxChars)
            {
                var len = Math.Min(options.MaxChars, turn.Text.Length - offset);
                chunks.Add(new Chunk(turn.Index, turn.Role, turn.Timestamp, "turn-part", turn.Text.Substring(offset, len)));
            }
        }

        return chunks;
    }
}
