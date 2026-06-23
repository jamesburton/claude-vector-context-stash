# CCStash — Vector Context Stash for Claude Code

**Status:** Design approved (2026-06-24)
**Author:** James Burton (with Claude Code)
**Spec type:** Design / architecture

---

## 1. Summary

CCStash is a .NET tool, published to NuGet and run zero-install via `dnx`, that gives
Claude Code a **compaction-aware vector context stash**. On **pre-compaction** it distills the
about-to-be-summarized transcript into an embedded vector store; after compaction it injects a
**minimal pointer** and exposes a **`retrieve_context` MCP tool** so Claude pulls back specific
detail on demand.

The goal — taken directly from the project README — is to *"keep context small but pull recent
information quickly when needed again."*

### The spine (core principle)

> Post-compaction injection is a **minimal pointer, never a blob**. The compaction summary already
> carries the gist; the stash exists for **targeted detail recovery**, driven by the model calling a
> tool when it hits a gap.

This is the non-negotiable design constraint: anything that re-injects large context after
compaction defeats the purpose and is out of scope.

---

## 2. Motivation & build-vs-reuse decision

### Why build rather than reuse

Research surveyed the existing Claude Code / coding-agent memory ecosystem (the official MCP memory
server, claude-mem, mem0's official CC plugin, agentmemory, ClawMem, graphiti, basic-memory,
mcp-memory-service, qdrant-mcp, letta, and others).

- The **mature, popular options** (mem0's CC plugin, claude-mem) are *general long-term memory*. mem0
  has a `PreCompact` hook but targets a cloud/self-hosted store and is not compaction-*specific*;
  claude-mem captures at session-end rather than around compaction.
- The **closest conceptual match** (ClawMem) does implement the `PreCompact` → `SessionStart:compact`
  loop, but uses a flat markdown state-file for that path (not vector retrieval) and is **not
  Windows-native** (WSL2 required).

The genuine gap is a **vector-backed, compaction-specific** stash that runs **natively on Windows**
and that we own and can shape exactly. That — not "nothing exists" — is the case for building.

The honest "want something working today" alternative is the **mem0 official CC plugin** or
**claude-mem**; this spec deliberately chooses ownership, exact fit, and native Windows distribution
over adopting a general-purpose tool.

### Platform facts verified firsthand

Both facts the architecture rests on were confirmed directly, not assumed:

1. **`PreCompact`** hands the hook a `transcript_path` to the session JSONL on disk. The transcript
   was inspected directly: JSONL, one object per line, with `user` / `assistant` envelopes whose
   nested content blocks are typed `text` / `thinking` / `tool_use` / `tool_result`. It is parseable.
2. **`SessionStart`** fires with `source: "compact"` after compaction and can inject text via
   `hookSpecificOutput.additionalContext` (capped ~10k chars). The current session's own transcript
   contains a `hook_additional_context` entry, confirming the mechanism works.

`PreCompact` also distinguishes `compaction_triggered_by: "manual" | "auto"`. The design intentionally
uses `SessionStart:compact` for re-injection (well-documented) rather than the less-certain
`PostCompact` event.

---

## 3. Distribution & naming

CCStash is distributed as a **NuGet .NET tool**, run zero-install and auto-updating via the .NET 10
`dnx` command:

```
dnx CCStash -- stash          # latest, prompt-if-missing
dnx -y CCStash -- stash       # latest, auto-confirm download (used by hooks — non-interactive)
dnx CCStash@1.0.0 -- mcp      # version-pinned
```

- **Tool / NuGet package id:** `CCStash` (`<PackAsTool>true</PackAsTool>`, `<ToolCommandName>ccstash</ToolCommandName>`).
- **Libraries** (rebased to the `CCStash.*` namespace):
  - `CCStash.Core` — transcript parsing, distillation, chunking, `IVectorStore`, `IEmbedder`, config.
  - `CCStash.Stores.Sqlite` — sqlite-vec store (default).
  - `CCStash.Stores.Qdrant` — Qdrant store (pluggable; **deferred**, post first slice).
  - `CCStash.Embeddings.Onnx` — local ONNX embeddings (default).
  - `CCStash.Mcp` — MCP stdio server.
  - `CCStash` — the CLI tool project (the `dnx`-runnable package; references the libraries).

**Distribution tradeoff (explicit):** `dnx` requires the **.NET 10 SDK** to be installed on the
machine. This replaces the earlier self-contained-exe idea. In exchange we get zero-install,
auto-updating, version-pinnable execution with no manual build step. First-run latency (NuGet
restore) is a known consideration for hooks under a timeout — see §8.

---

## 4. Architecture

### 4.1 Components

A single tool with multiple verbs (the `CCStash` CLI, `System.CommandLine`):

| Verb | Invoked by | Responsibility |
|------|-----------|----------------|
| `stash` | `PreCompact` hook | Parse `transcript_path` (from stdin hook JSON), distill new turns, embed, upsert into the store. Always exits 0. |
| `pointer` | `SessionStart` hook (matcher `source=compact`) | Emit the minimal pointer as `additionalContext`. Emits nothing if no stash. |
| `mcp` | `.mcp.json` registration | Run the MCP stdio server exposing `retrieve_context` and `list_stashes`. |
| `init` | user, once | Write hook + `.mcp.json` config; initialize DB path; optionally pre-warm the package. |
| `status` | user | Show stash counts, last stash time, DB size, embedding model. |
| `search` | user (debug) | Manual semantic search against the stash from the terminal. |

### 4.2 The loop (data flow)

1. Session runs; context grows.
2. **Compaction triggers** (auto or `/compact`) → `PreCompact` hook → `dnx -y CCStash -- stash`.
   Stdin carries the hook JSON (`transcript_path`, `session_id`, `compaction_triggered_by`, `cwd`).
   CCStash parses the JSONL, distills **only new turns since the last stash** (incremental
   high-water mark per session), embeds, and upserts into the store keyed by `project + session`.
   Exits 0 regardless of outcome.
3. Claude Code compacts (summary replaces history).
4. **`SessionStart` (`source=compact`)** → `dnx -y CCStash -- pointer` → emits a short
   `additionalContext`, e.g.:
   > 🗄️ Detailed pre-compaction context for this session is stashed (id `<id>`, N chunks). Call the
   > `retrieve_context` tool to pull specifics — earlier decisions, file contents, error details —
   > when needed.

   A few hundred characters, never a blob.
5. Later, Claude hits a gap → calls **`retrieve_context(query, limit)`** → semantic search → returns
   top-k distilled chunks → Claude continues with detail; live context stays lean.
6. Next compaction → incremental stash again. Loop.

### 4.3 Distillation pipeline (the quality lever)

- **Keep:** user prompts, assistant text, tool **invocations** (name + key args).
- **Truncate** large `tool_result` payloads (file dumps, command output) to head/tail + a size note.
  *Raw tool output is never embedded* — it is the bulk of the bytes and the lowest signal. This is the
  primary quality and cost lever.
- `thinking` blocks: included but down-weighted; configurable.
- **Chunk** per logical turn, token-budgeted (~500–800 tokens). Metadata per chunk: `session`,
  `project`, `timestamp`, `turn_index`, `role`, `type`, `source_ref`.
- **Incremental:** a per-session high-water mark (e.g. last processed line/turn index) ensures
  repeated compactions only embed new turns.

### 4.4 Storage & the context identifier

- **Default store:** one sqlite file per project at `~/.claude/ccstash/<project-hash>.db`.
  A `vec0` virtual table holds embeddings; a `chunks` table holds text + metadata; metadata filtering
  is plain SQL. The embedding model + dimension are recorded in the DB to prevent dimension mixing.
- **sqlite-vec** is loaded as a SQLite extension through `Microsoft.Data.Sqlite`
  (`SQLitePCLRaw.bundle_e_sqlite3` + `connection.LoadExtension("vec0")`).
- **Context identifier** = `project:session`. **Retrieval scope defaults to the current session**,
  with a config flag to widen to **project-wide** (cross-session recall).
- At the first-slice scale (thousands to ~100k chunks for one developer) sqlite-vec brute-force KNN is
  ample; ANN is unnecessary.

### 4.5 Embeddings

- **Default:** local embeddings via **ONNX Runtime**, model `all-MiniLM-L6-v2` (384-dim), bundled.
  No API key, runs offline — appropriate for a privacy-conscious dev tool.
- **Optional:** an API embedder (e.g. Voyage / OpenAI) behind the same `IEmbedder` interface, via
  config.

### 4.6 Vector store abstraction

`IVectorStore` isolates the backend:

- `CCStash.Stores.Sqlite` — default, embedded, single file, no daemon.
- `CCStash.Stores.Qdrant` — **deferred**; reuses existing Qdrant infrastructure when desired
  (collection per project, payload metadata, HNSW). Swappable purely via config.

### 4.7 Configuration

`~/.claude/ccstash/config.json` (with optional per-project override):

- store backend + connection
- embedding provider + model
- distillation rules (max `tool_result` chars, include-thinking)
- retrieval scope (session | project)
- pointer verbosity

The README's *"force `/clear`"* idea is **deferred / out of v1 scope** (YAGNI). It may return later as
a config toggle. No hook on `/clear` is implemented in v1.

---

## 5. Safety (non-negotiable)

Hooks must never break a Claude Code session:

- `stash` runs under a timeout, catches all exceptions, and **always exits 0**; failures are logged to
  a file under the data dir.
- `stash` **never exits 2** and never blocks compaction.
- `pointer` emits nothing when there is no stash.
- `retrieve_context` degrades gracefully to "no results".

---

## 6. Testing strategy

- **Unit:** transcript parser (against captured real transcript fixtures), distillation rules,
  chunker, store (sqlite-vec temp file + in-memory fake `IVectorStore`), embedder (mock + one
  real-model smoke test).
- **Integration:** full `PreCompact` → `stash` → `retrieve_context` roundtrip on a fixture transcript;
  MCP `retrieve_context` contract test.
- **E2E (the demo):** install into a live Claude Code session, force `/compact`, confirm the pointer
  appears and `retrieve_context` returns relevant chunks.
- **Conventions:** xUnit; StyleCop SA rules; XML doc comments on public APIs.

---

## 7. First vertical slice (the demo target)

Build the end-to-end loop on a **real session**, minimally:

`stash` (parse + distill + local ONNX embed + sqlite-vec write) → `pointer` → `retrieve_context` MCP
tool → working roundtrip.

**In:** sqlite-vec only, local ONNX embeddings only, current-session scope, per-turn chunking,
truncated `tool_results`, `init` wiring for hooks + `.mcp.json`.

**Deferred:** Qdrant store, API embeddings, hybrid/FTS5 search, project-wide scope, `/clear` handling,
richer config.

---

## 8. Risks / integration details to validate early

1. **sqlite-vec extension loading** under `Microsoft.Data.Sqlite` on Windows (requires the
   `e_sqlite3` bundle with `load_extension` enabled, then `LoadExtension("vec0")`). Validate in a spike.
2. **ONNX embedding tokenizer** in .NET (BERT wordpiece — via `Microsoft.ML.Tokenizers` or
   `FastBertTokenizer`). If fiddly, the documented fallback is a local-API embedder (e.g. Ollama
   `nomic-embed-text`).
3. **`dnx` first-run latency** (NuGet restore) versus hook timeouts. Mitigations: pre-warm the package
   during `init`; allow version-pinning (`CCStash@x.y.z`); keep the `stash` timeout generous and
   failure-safe (exit 0).
4. **Transcript JSONL schema drift** across Claude Code versions — isolate all schema knowledge in the
   parser and test against captured fixtures.

---

## 9. Out of scope (v1)

- Qdrant backend, hybrid (BM25/FTS5) search, cross-session/project-wide retrieval.
- `/clear` hooking or forced clear/compaction behavior.
- API-based embeddings.
- Any post-compaction injection larger than the minimal pointer.
