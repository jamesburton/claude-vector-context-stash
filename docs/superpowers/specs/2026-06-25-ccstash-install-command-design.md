# CCStash `install` command — design

**Status:** approved design (brainstorming), pending spec review
**Date:** 2026-06-25

## Goal

Replace the bare `init` verb with an `install` command that sets up CCStash's agent
configuration interactively (a Spectre.Console TUI by default) or non-interactively (flags),
for one or more agents, at project or user scope — with an extensible adapter model so new agents
are added without reworking the command. First adapters: **Claude Code** and **CodeSharp**.

## Scope & decomposition

The full request spans two repos and is split into two sub-projects:

- **SP1 (this spec): the `install` command + adapter framework + Claude Code adapter + CodeSharp
  *project-scope* wiring.** Self-contained and shippable from the CCStash repo. The CodeSharp
  capture hook is installed referencing a `{TranscriptPath}` placeholder; it no-ops until SP2.
- **SP2 (separate spec): the CodeSharp capture bridge.** A CodeSharp PR (emit a JSONL transcript on
  `SessionEnd`, add `{TranscriptPath}`/`{ProjectDir}` command-hook placeholders), the CodeSharp
  *user-scope* config model (based on Claude Code's), and a CCStash parser for that transcript.

SP1 delivers the whole installer and full Claude Code + CodeSharp-retrieval value. SP2 completes
CodeSharp auto-capture.

## Global constraints (verbatim values)

- Generated invocations use `dotnet dnx CCStash -- <verb>` (matches the verified live wiring; no
  `--configfile`, so the published nuget.org package is used).
- Target framework `net10.0`; the TUI dependency (Spectre.Console) is added to the `CCStash` CLI
  project only, not the libraries.
- All config writes are an **idempotent merge** — never clobber unrelated entries; re-running is a
  no-op.
- Every adapter must support `Remove` (uninstall) that deletes only CCStash-authored entries.

## Command surface

`ccstash install` (invoked `dnx CCStash -- install`):

- **No actionable args + interactive console → TUI** (Spectre.Console): multi-select agents
  (detected ones pre-checked), choose scope (project/user), review a dry-run plan, confirm, apply.
  If the ONNX model is missing, offer to download it.
- **Non-interactive flags** (any present ⇒ skip the TUI):
  - `--agent <claude|codesharp|all>` (repeatable / comma-separated)
  - `--scope <project|user>`
  - `--project <path>` (defaults to cwd / `CLAUDE_PROJECT_DIR`)
  - `--yes` (apply without the confirm prompt)
  - `--dry-run` (print the plan, write nothing)
- **`ccstash uninstall`** with the same `--agent/--scope/--project` selectors → each adapter's
  `Remove`.
- **`init` becomes a thin alias**: `install --agent claude --scope project --yes` (back-compat).

When no TTY is available and no flags are given, `install` prints usage and exits non-zero rather
than blocking on a TUI.

## Architecture

### `IAgentAdapter` (CCStash.Core, new `Install` namespace)

```csharp
public enum InstallScope { Project, User }

public sealed record InstallContext(InstallScope Scope, string ProjectDir);

// One concrete change the adapter will make, for dry-run display and idempotency reporting.
public sealed record InstallAction(string Target, string Description, bool AlreadyPresent);

public sealed record InstallPlan(string Agent, IReadOnlyList<InstallAction> Actions);

public interface IAgentAdapter
{
    string Id { get; }                       // "claude", "codesharp"
    string DisplayName { get; }              // "Claude Code", "CodeSharp"
    bool SupportsScope(InstallScope scope);  // codesharp: Project only in SP1
    bool Detect(InstallContext ctx);         // is this agent present for this scope/project?
    InstallPlan Plan(InstallContext ctx);    // compute actions without writing
    void Apply(InstallPlan plan, InstallContext ctx);
    void Remove(InstallContext ctx);         // delete only CCStash-authored entries
}
```

Adapters are pure config writers over the filesystem; `Plan` computes actions and `AlreadyPresent`
flags by reading current config, `Apply` performs the idempotent merge, `Remove` reverses it. The
TUI and the flag path both build an `InstallContext`, call `Plan` (for the dry-run/confirm screen),
then `Apply`. This keeps file I/O isolated behind the adapter and makes each adapter unit-testable
against a temp directory.

### Config-writing helpers (shared)

A small `JsonConfigEditor` (System.Text.Json.Nodes) provides load-or-empty, deep-merge-without-
clobber, and remove-by-path used by both adapters — extracting the merge logic currently inlined in
`InitVerb`. This is the DRY home for "ensure this hook entry exists" / "ensure this MCP server
entry exists" / "remove these".

### ClaudeCodeAdapter

Refactor of today's `InitVerb`. Writes:
- **Hooks** → `.claude/settings.json` (project) or `~/.claude/settings.json` (user):
  - `PreCompact` → `dotnet dnx CCStash -- stash`
  - `SessionStart` (matcher `compact`) → `dotnet dnx CCStash -- pointer`
- **MCP** → `.mcp.json` (project) or `~/.claude.json` `mcpServers` (user):
  - `ccstash` → `dotnet dnx CCStash -- mcp` (+ `--project <abs>` for project scope so the server
    resolves the same DB as the hooks).

`Detect` = a `.claude` dir / `~/.claude.json` exists. `Remove` strips only entries whose command
contains `CCStash`.

### CodeSharpAdapter (SP1: project scope only)

- **MCP** → merge `CodeSharp:Mcp:Servers:ccstash` into the project `appsettings.json`:
  `{ "Transport": "stdio", "Command": "dotnet", "Args": ["dnx","CCStash","--","mcp","--project","<abs>"] }`.
- **Hooks** → write `.codesharp/skills/ccstash.md` with `hooks:` frontmatter:
  - `event: SessionStart`, `handler: { type: command, command: "dotnet dnx CCStash -- pointer --project {ProjectDir}" }`
  - `event: SessionEnd`,   `handler: { type: command, command: "dotnet dnx CCStash -- stash --transcript {TranscriptPath} --project {ProjectDir}" }`

  The `SessionEnd` stash references `{TranscriptPath}` (a placeholder CodeSharp does not yet provide
  — added in SP2); until then the hook runs but finds no transcript and exits 0 (stash is already
  hook-safe / always-exit-0). `SupportsScope(User)` returns false in SP1.

`Detect` = a `.codesharp` dir or an `appsettings.json` containing a `CodeSharp:` section. `Remove`
deletes `.codesharp/skills/ccstash.md` and the `CodeSharp:Mcp:Servers:ccstash` entry.

> Note: `stash` gains an optional `--transcript <path>` arg (overrides the stdin `transcript_path`)
> and already accepts `--project`. These are additive and used by the CodeSharp hook commands.

## Data flow

1. `install` parses flags → `InstallContext` per (agent, scope), or the TUI collects them.
2. For each selected adapter: `Plan(ctx)` → actions (with `AlreadyPresent`).
3. TUI/flags show the combined plan; on confirm (or `--yes`), `Apply` each plan.
4. Output a summary per agent (what was written / already present) and any follow-ups (restart the
   agent to load MCP; model-download hint).

## Error handling

- Adapters are best-effort per action and never throw past `install`: a failed write for one agent
  is reported and does not abort the others.
- Malformed existing config (unparseable JSON) is reported and skipped for that target rather than
  overwritten (mirrors `CCStashConfig.Load`'s tolerant behavior).
- `--dry-run` and the TUI confirm screen both call only `Plan` (no writes).
- No-TTY + no-flags → usage + non-zero exit (never hang).

## Testing

- `JsonConfigEditor` unit tests: merge-without-clobber, idempotent re-apply, remove-by-path.
- `ClaudeCodeAdapter` tests: Plan/Apply/Remove against a temp project + temp `~`; assert hooks and
  MCP entries created, re-apply is a no-op, Remove leaves unrelated entries intact.
- `CodeSharpAdapter` tests: Plan/Apply/Remove against a temp project; assert `appsettings.json`
  `CodeSharp:Mcp:Servers:ccstash` and `.codesharp/skills/ccstash.md` content (valid YAML frontmatter
  with the two hooks), idempotency, and clean Remove.
- A flag-path smoke test: `install --agent all --scope project --project <temp> --yes` produces the
  expected files; `uninstall` removes them.
- TUI logic is kept thin (selection → context → Plan/Apply); the testable logic lives in adapters.

## Out of scope (SP1)

- CodeSharp user-scope config, the CodeSharp transcript-emit PR, and the CCStash CodeSharp
  transcript parser (all SP2).
- Adapters for Codex / Gemini CLI / Copilot (future adapters via the same interface).
