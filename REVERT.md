# Reverting the CCStash live wiring

This undoes the global Claude Code wiring applied for live testing (user-scoped
`PreCompact`/`SessionStart` hooks + the `ccstash` user MCP server). Nothing here
touches the repo — only your machine's Claude Code config under `~/.claude`.

## Backups taken when wiring was applied

- `~/.claude/settings.json.ccstash-bak-20260624-111836`
- `~/.claude.json.ccstash-bak-20260624-111836`

## Recommended: surgical removal

Prefer this over restoring the whole `.claude.json` backup — your running session
may have updated other state in that file since the backup was taken.

### 1. Remove the user MCP server

```bash
claude mcp remove ccstash --scope user
```

Verify:

```bash
python -c "import json;print('ccstash' in json.load(open(r'C:/Users/james/.claude.json')).get('mcpServers',{}))"
# -> False
```

### 2. Remove the two hooks from `~/.claude/settings.json`

Removes only the entries whose command references `CCStash`, leaving every other
hook intact:

```bash
python - <<'PY'
import json
p = r"C:/Users/james/.claude/settings.json"
d = json.load(open(p, encoding="utf-8"))
h = d.get("hooks", {})
for event in ("PreCompact", "SessionStart"):
    if event in h:
        h[event] = [e for e in h[event]
                    if not any("CCStash" in x.get("command", "")
                               for x in e.get("hooks", []))]
        if not h[event]:
            del h[event]
json.dump(d, open(p, "w", encoding="utf-8"), indent=2)
print("CCStash hooks removed; remaining hook events:", list(h.keys()))
PY
```

### 3. Restart Claude Code

stdio MCP servers don't hot-reload, so restart Claude Code to fully unload the
`ccstash` server. (`/mcp` only refreshes/retries connections.)

## Optional: delete stashed data

Per-project vector databases, config, log, and the downloaded embedding model
all live under one directory:

```bash
rm -rf ~/.claude/ccstash          # removes DBs, config.json, ccstash.log, and models/
# or keep the ~90 MB model and clear only the stashes:
rm -f ~/.claude/ccstash/*.db ~/.claude/ccstash/*.db-* ~/.claude/ccstash/ccstash.log
```

## Fallback: full restore from backup

If you'd rather restore wholesale (this reverts ALL changes to those files since
the backup, not just CCStash):

```bash
cp ~/.claude/settings.json.ccstash-bak-20260624-111836 ~/.claude/settings.json
cp ~/.claude.json.ccstash-bak-20260624-111836           ~/.claude.json
```

Then restart Claude Code.

## Notes

- The hooks and MCP entry invoke `dotnet dnx --configfile <repo>/dnx-nuget.config
  CCStash`, which resolves the locally-packed `artifacts/CCStash.0.1.1.nupkg`.
  Removing the wiring above is sufficient; the local NuGet artifacts and the
  `dnx` cache can be left in place or cleared separately.
- To re-apply later, see `docs/INSTALL.md`.
