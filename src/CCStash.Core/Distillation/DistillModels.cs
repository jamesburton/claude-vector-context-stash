namespace CCStash.Core.Distillation;

/// <summary>Controls how raw turns are distilled into compact text.</summary>
public sealed record DistillOptions(int MaxToolResultChars = 800, bool IncludeThinking = true);

/// <summary>A turn reduced to a single compact text representation.</summary>
public sealed record DistilledTurn(int Index, string Role, DateTimeOffset? Timestamp, string Text);
