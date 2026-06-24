using CCStash.Core.Distillation;

namespace CCStash.Core.Chunking;

/// <summary>Splits distilled turns into size-bounded chunks.</summary>
public interface IChunker
{
    /// <summary>Chunk turns to at most <see cref="ChunkOptions.MaxChars"/> characters each.</summary>
    IReadOnlyList<Chunk> Chunk(IReadOnlyList<DistilledTurn> turns, ChunkOptions options);
}
