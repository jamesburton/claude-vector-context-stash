using CCStash.Core.Transcript;

namespace CCStash.Core.Distillation;

/// <summary>Reduces transcript turns to compact, embeddable text.</summary>
public interface IDistiller
{
    /// <summary>Distill turns, truncating bulky tool output per <paramref name="options"/>.</summary>
    IReadOnlyList<DistilledTurn> Distill(IReadOnlyList<TranscriptTurn> turns, DistillOptions options);
}
