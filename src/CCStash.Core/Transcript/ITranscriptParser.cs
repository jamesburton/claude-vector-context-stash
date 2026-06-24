namespace CCStash.Core.Transcript;

/// <summary>Parses a Claude Code session JSONL transcript into typed turns.</summary>
public interface ITranscriptParser
{
    /// <summary>Parse the JSONL at <paramref name="jsonlPath"/>; unparseable lines are skipped.</summary>
    IReadOnlyList<TranscriptTurn> Parse(string jsonlPath);
}
