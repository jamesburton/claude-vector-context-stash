# CCStash — Vector Context Stash for Claude Code

CCStash gives Claude Code a **compaction-aware vector memory**. Just before the conversation is
compacted, it distills the transcript into an embedded vector store. After compaction it injects a
**tiny pointer** (not a context dump) and exposes a **`retrieve_context` MCP tool** so Claude can pull
back specific earlier detail on demand.

> The goal (from the original idea): *keep the live context small, but pull recent information back
> quickly when it's needed again.*

## The spine

Post-compaction injection is a **minimal pointer, never a blob**. The compaction summary already
carries the gist; the stash exists for **targeted detail recovery**, driven by the model calling a
tool when it hits a gap.

## How it works

```
 conversation grows
        │
        ▼
 ┌──────────────┐   PreCompact hook        ┌───────────────────────────┐
 │  /compact or │ ───────────────────────► │ ccstash stash             │
 │  auto-compact│   (transcript_path)      │  parse → distill → chunk  │
 └──────────────┘                          │  → embed → sqlite store   │
        │                                  └───────────────────────────┘
        ▼  (history summarized)
 ┌──────────────┐   SessionStart[compact]  ┌───────────────────────────┐
 │  compacted   │ ───────────────────────► │ ccstash pointer           │
 │  session     │                          │  "🗄️ N chunks stashed —    │
 └──────────────┘                          │   call retrieve_context"  │
        │                                  └───────────────────────────┘
        ▼  later, Claude needs detail
 ┌──────────────┐   MCP tool call          ┌───────────────────────────┐
 │ retrieve_    │ ───────────────────────► │ semantic search → top-k   │
 │ context(q)   │ ◄─────────────────────── │ distilled chunks          │
 └──────────────┘                          └───────────────────────────┘
```

## Components

| Project | Responsibility |
|---|---|
| `CCStash.Core` | Transcript parser, distiller (truncates bulky tool output), chunker, `IVectorStore`/`IEmbedder` interfaces, stash + retrieval services, config. |
| `CCStash.Stores.Sqlite` | Single-file SQLite store (no daemon). |
| `CCStash.Embeddings.Onnx` | Local `all-MiniLM-L6-v2` embeddings via ONNX Runtime — no API key. |
| `CCStash.Mcp` | `retrieve_context` / `list_stashes` MCP tools. |
| `CCStash` | The CLI tool (`dnx`-runnable): `install`, `uninstall`, `stash`, `pointer`, `mcp`, `init`, `status`, `search`. |

## Quick start

Requires the **.NET 10 SDK** (provides `dnx`).

```bash
# from your project directory
dnx -y CCStash -- install   # wire hooks (.claude/settings.json) + MCP server (.mcp.json)
# place a local model for semantic embeddings (optional but recommended):
#   ~/.claude/ccstash/models/all-MiniLM-L6-v2/{model.onnx,tokenizer.json}
# restart Claude Code
```

Then just work. On compaction CCStash stashes automatically; when Claude needs older detail it calls
`retrieve_context`. Inspect the stash any time:

```bash
dnx CCStash -- status                 # chunk count, model, latest session
dnx CCStash -- search "that database decision"
```

See [docs/INSTALL.md](docs/INSTALL.md) for details and the end-to-end check.

### MCP transport

`ccstash mcp` serves `retrieve_context`/`list_stashes` over **stdio** by default (what `install`
wires up). Pass `--http` to serve over Streamable HTTP instead, optionally with `--port <n>`
(default `6733`):

```bash
dnx CCStash -- mcp --http --port 6733   # listens on http://localhost:6733/mcp
```

## Design & plan

- Design spec: [docs/superpowers/specs/2026-06-24-ccstash-vector-context-stash-design.md](docs/superpowers/specs/2026-06-24-ccstash-vector-context-stash-design.md)
- Implementation plan: [docs/superpowers/plans/2026-06-24-ccstash-vertical-slice.md](docs/superpowers/plans/2026-06-24-ccstash-vertical-slice.md)
- `install`/`uninstall` command design spec: [docs/superpowers/specs/2026-06-25-ccstash-install-command-design.md](docs/superpowers/specs/2026-06-25-ccstash-install-command-design.md)
- `install`/`uninstall` implementation plan (SP1): [docs/superpowers/plans/2026-07-02-ccstash-install-command-sp1.md](docs/superpowers/plans/2026-07-02-ccstash-install-command-sp1.md)

## Status

Working end-to-end: stash (incremental) → pointer → semantic `retrieve_context`, across both the
**SQLite** (default, single-file, with **hybrid vector + FTS5/BM25 search** fused via Reciprocal Rank
Fusion) and **Qdrant** backends, selectable by config. Local ONNX embeddings are the default, with
`FakeEmbedder` as an offline fallback so hooks never fail. The native `sqlite-vec` ANN extension and
Qdrant native hybrid (sparse vectors) remain planned optimizations behind the existing `IVectorStore`
interface.
