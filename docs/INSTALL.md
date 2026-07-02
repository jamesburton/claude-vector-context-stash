# Installing CCStash

CCStash is a .NET tool run zero-install via `dnx`. It requires the **.NET 10 SDK** (which provides
`dnx`).

CCStash's hooks and MCP server invoke it as `dnx CCStash` — `dnx` resolves the tool from a NuGet
feed. So CCStash must be reachable on a feed `dnx` is configured to use.

### Option A — published feed (the intended path)

Publish once, then `dnx CCStash` works anywhere:

```bash
dotnet pack src/CCStash -c Release -o ./artifacts
dotnet nuget push artifacts/CCStash.*.nupkg --source <your-feed> --api-key <key>
dnx CCStash -- status      # resolves from the feed, runs the tool
```

### Option B — local source, before publishing

`dnx` accepts a NuGet config selecting a local folder source:

```bash
dotnet pack src/CCStash -c Release -o ./artifacts
# dnx-nuget.config points at ./artifacts + nuget.org
dotnet dnx --configfile dnx-nuget.config CCStash -- status
```

> Verified working: `dotnet dnx --configfile dnx-nuget.config CCStash -- status` resolves the packed
> tool from `./artifacts` and runs it. To make the hooks use a local source pre-publish, add that
> `--configfile` argument to the commands `init` writes (or point your machine's NuGet config at the
> feed).

## 2. Add a local embedding model (recommended)

CCStash uses a local ONNX model — no API key, fully offline. Download `all-MiniLM-L6-v2`:

```bash
mkdir -p ~/.claude/ccstash/models/all-MiniLM-L6-v2
cd ~/.claude/ccstash/models/all-MiniLM-L6-v2
curl -L -o model.onnx     https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx
curl -L -o tokenizer.json https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/tokenizer.json
```

Without a model, CCStash falls back to a non-semantic embedder — the loop still runs, but retrieval
ranking is keyword-ish rather than semantic.

## 3. Wire it into your project

```bash
cd /path/to/your/project
dnx -y CCStash -- install --agent claude --scope project --yes
```

Or interactively (multi-select agents, choose scope, review a dry-run plan, confirm):

```bash
dnx -y CCStash -- install
```

This writes (merging, not clobbering):

- `.claude/settings.json` — `PreCompact` → `ccstash stash`, `SessionStart[compact]` → `ccstash pointer`
- `.mcp.json` — a `ccstash` MCP server (`ccstash mcp`)
- `~/.claude/ccstash/config.json` — default settings

Pass `--scope user` to install into `~/.claude/settings.json` / `~/.claude.json` instead (shared
across all projects). `ccstash uninstall` with the same `--agent`/`--scope`/`--project` flags removes
only the entries CCStash wrote. `ccstash init` remains as a fixed shorthand for
`install --agent claude --scope project --yes`.

Restart Claude Code so it loads the hooks and the MCP server.

## End-to-end check

1. Have a normal conversation, then run `/compact`.
2. `PreCompact` runs `ccstash stash` → check `~/.claude/ccstash/ccstash.log` shows `stash ok`.
3. After compaction, a `SessionStart` pointer appears as a system reminder mentioning `retrieve_context`.
4. Ask Claude something answerable only from earlier detail; confirm it calls `retrieve_context`.
5. `dnx CCStash -- status` shows the stored chunk count.

## Configuration (`~/.claude/ccstash/config.json`)

| Key | Default | Meaning |
|---|---|---|
| `Store` | `sqlite` | Vector store backend. |
| `EmbeddingProvider` | `onnx` | `onnx` (local) — falls back to a fake embedder if no model is present. |
| `EmbeddingModel` | `all-MiniLM-L6-v2` | Model directory name under `~/.claude/ccstash/models`. |
| `MaxToolResultChars` | `800` | Truncation budget for bulky tool output before embedding. |
| `IncludeThinking` | `true` | Whether to stash assistant thinking blocks. |
| `RetrievalScope` | `session` | `session` (current/latest) or `project` (all sessions). |
| `RetrievalLimit` | `6` | Default number of chunks returned. |

## Notes / known follow-ups

- The packed tool is large (~150 MB) because ONNX Runtime bundles native libraries for every
  platform. A RID-trimmed build will shrink `dnx` first-run download time.
- `dnx` first-run restores the package; for hooks, pre-warm once or pin a version (`CCStash@x.y.z`).
- The native `sqlite-vec` ANN extension and Qdrant/hybrid-search are planned behind `IVectorStore`.
