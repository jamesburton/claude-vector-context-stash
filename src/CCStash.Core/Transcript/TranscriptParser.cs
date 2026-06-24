using System.Text.Json;

namespace CCStash.Core.Transcript;

/// <summary>Default <see cref="ITranscriptParser"/> for the Claude Code transcript format.</summary>
public sealed class TranscriptParser : ITranscriptParser
{
    /// <inheritdoc/>
    public IReadOnlyList<TranscriptTurn> Parse(string jsonlPath)
    {
        var turns = new List<TranscriptTurn>();
        var index = 0;

        foreach (var line in File.ReadLines(jsonlPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            TranscriptTurn? turn;
            try
            {
                turn = ParseLine(line, index);
            }
            catch (JsonException)
            {
                continue; // tolerate schema drift / partial lines
            }

            if (turn is not null)
            {
                turns.Add(turn);
                index++;
            }
        }

        return turns;
    }

    private static TranscriptTurn? ParseLine(string line, int index)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeEl))
        {
            return null;
        }

        var type = typeEl.GetString();
        if (type is not ("user" or "assistant"))
        {
            return null; // only conversation turns are stashable
        }

        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var role = message.TryGetProperty("role", out var roleEl) ? roleEl.GetString() ?? type : type;
        DateTimeOffset? ts = root.TryGetProperty("timestamp", out var tsEl) &&
                             DateTimeOffset.TryParse(tsEl.GetString(), out var parsed)
            ? parsed
            : null;

        var blocks = ParseContent(message);
        return blocks.Count == 0 ? null : new TranscriptTurn(index, role, ts, blocks);
    }

    private static List<ContentBlock> ParseContent(JsonElement message)
    {
        var blocks = new List<ContentBlock>();
        if (!message.TryGetProperty("content", out var content))
        {
            return blocks;
        }

        // Content may be a bare string or an array of typed blocks.
        if (content.ValueKind == JsonValueKind.String)
        {
            blocks.Add(new ContentBlock(BlockKind.Text, content.GetString() ?? string.Empty, null));
            return blocks;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return blocks;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var btype = item.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            switch (btype)
            {
                case "text":
                    blocks.Add(new ContentBlock(BlockKind.Text, Str(item, "text"), null));
                    break;
                case "thinking":
                    blocks.Add(new ContentBlock(BlockKind.Thinking, Str(item, "thinking"), null));
                    break;
                case "tool_use":
                    blocks.Add(new ContentBlock(
                        BlockKind.ToolUse,
                        item.TryGetProperty("input", out var inp) ? inp.GetRawText() : string.Empty,
                        item.TryGetProperty("name", out var n) ? n.GetString() : null));
                    break;
                case "tool_result":
                    blocks.Add(new ContentBlock(BlockKind.ToolResult, ResultText(item), null));
                    break;
            }
        }

        return blocks;
    }

    private static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string ResultText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var c))
        {
            return string.Empty;
        }

        if (c.ValueKind == JsonValueKind.String)
        {
            return c.GetString() ?? string.Empty;
        }

        if (c.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(c.EnumerateArray().Select(e => Str(e, "text")));
        }

        return c.GetRawText();
    }
}
