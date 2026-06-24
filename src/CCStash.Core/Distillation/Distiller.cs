using System.Text;
using CCStash.Core.Transcript;

namespace CCStash.Core.Distillation;

/// <summary>Default distiller: keeps prompts, text, and tool invocations; truncates tool output.</summary>
public sealed class Distiller : IDistiller
{
    /// <inheritdoc/>
    public IReadOnlyList<DistilledTurn> Distill(IReadOnlyList<TranscriptTurn> turns, DistillOptions options)
    {
        var result = new List<DistilledTurn>();

        foreach (var turn in turns)
        {
            var sb = new StringBuilder();
            foreach (var block in turn.Blocks)
            {
                AppendBlock(sb, block, options);
            }

            var text = sb.ToString().Trim();
            if (text.Length > 0)
            {
                result.Add(new DistilledTurn(turn.Index, turn.Role, turn.Timestamp, text));
            }
        }

        return result;
    }

    private static void AppendBlock(StringBuilder sb, ContentBlock block, DistillOptions options)
    {
        switch (block.Kind)
        {
            case BlockKind.Text:
                sb.AppendLine(block.Text);
                break;
            case BlockKind.Thinking when options.IncludeThinking:
                sb.AppendLine($"(thinking) {block.Text}");
                break;
            case BlockKind.ToolUse:
                sb.AppendLine($"[tool: {block.ToolName}] {Truncate(block.Text, options.MaxToolResultChars)}");
                break;
            case BlockKind.ToolResult:
                sb.AppendLine($"[tool result] {Truncate(block.Text, options.MaxToolResultChars)}");
                break;
        }
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var head = max / 2;
        var tail = max - head;
        return $"{text[..head]} …[{text.Length - max} chars truncated]… {text[^tail..]}";
    }
}
