# CCStash `install`/`uninstall` Command (SP1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the bare `init` verb with `ccstash install`/`ccstash uninstall` — an adapter-based
config installer (Claude Code + CodeSharp, project/user scope) driven by flags or a Spectre.Console
TUI, per `docs/superpowers/specs/2026-06-25-ccstash-install-command-design.md`.

**Architecture:** A new `CCStash.Core.Install` namespace holds pure, filesystem-only, unit-testable
pieces: `IAgentAdapter` (+ `InstallContext`/`InstallAction`/`InstallPlan` records), a shared
`JsonConfigEditor` (load/merge/save/remove primitives extracted from today's `InitVerb`), and two
adapters (`ClaudeCodeAdapter`, `CodeSharpAdapter`). A new `InstallVerb` in the CLI project owns flag
parsing, the confirm/dry-run gate, and a thin Spectre.Console TUI; it only calls
`Plan`/`Apply`/`Remove` on the adapters it selects. `init` becomes a fixed-flag call into the same
verb.

**Tech Stack:** .NET 10, `System.Text.Json.Nodes` (JSON merge), xUnit 2.9.3 (tests), Spectre.Console
(new — CLI project only, TUI rendering/prompts).

## Global Constraints

- Target framework `net10.0`, `Nullable enable`, `ImplicitUsings enable` (from `Directory.Build.props`;
  `GenerateDocumentationFile=true` repo-wide — every public/internal type/member needs an XML doc
  comment, matching existing code).
- Spectre.Console is added as a `PackageReference` to `src/CCStash/CCStash.csproj` **only** — not to
  `CCStash.Core` or any other library project.
- All config writes are an **idempotent merge** — never clobber unrelated entries; re-running is a
  no-op that reports `AlreadyPresent: true` for every action.
- Every adapter supports `Remove` (uninstall) that deletes only CCStash-authored entries.
- **`dnx -y CCStash -- <verb>` (unchanged) for the `ClaudeCodeAdapter`.** The design spec's prose says
  "Generated invocations use `dotnet dnx CCStash -- <verb>`", but the *shipped, currently-installed*
  Claude Code hook command is `dnx -y CCStash -- stash` / `dnx -y CCStash -- pointer` (see
  `InitVerb.cs` today, and `docs/INSTALL.md`). Idempotency is matched by **exact command-string
  equality** (`EnsureHookEntry` below) — if `ClaudeCodeAdapter` used a different string, re-running
  `install` on a project that already ran `init` would append a **second, duplicate** hook entry
  instead of recognizing the existing one, and both would fire on every compaction. `ClaudeCodeAdapter`
  therefore keeps the exact `dnx -y CCStash -- <verb>` strings verbatim. This is a deliberate deviation
  from the spec's prose, not an oversight.
- **`dotnet dnx CCStash -- <verb> ...` for the brand-new `CodeSharpAdapter`** (no existing installs to
  stay compatible with) — exact command strings and the `{ "Transport": "stdio", "Command": "dotnet",
  "Args": [...] }` MCP shape are copied verbatim from the spec's `CodeSharpAdapter` section.
- `stash` gains an optional `--transcript <path>` arg (overrides stdin's `transcript_path`); it already
  effectively needs `--project` support added for the CodeSharp hook use case.
- `init` becomes a thin alias: `install --agent claude --scope project --yes`.

---

## File structure

**New — `src/CCStash.Core/Install/`** (pure BCL, no new package references; mirrors `CCStash.Core`
having zero `PackageReference`s today):
- `InstallTypes.cs` — `InstallScope`, `InstallContext`, `InstallAction`, `InstallPlan`.
- `IAgentAdapter.cs` — the adapter interface.
- `JsonConfigEditor.cs` — shared load/merge/save/remove primitives.
- `ClaudeCodeAdapter.cs` — refactor of `InitVerb`'s logic, project + user scope.
- `CodeSharpAdapter.cs` — project-scope-only adapter (SP1).

**Modified:**
- `src/CCStash.Core/Config/CCStashPaths.cs` — add `ClaudeUserSettingsPath`, `ClaudeUserJsonPath`, and a
  `CCSTASH_HOME_OVERRIDE` test seam.
- `src/CCStash/Verbs/StashVerb.cs` — accept `--transcript`/`--project` CLI overrides.
- `src/CCStash/Program.cs` — dispatch `install`/`uninstall`, alias `init`, update usage string.
- `src/CCStash/CCStash.csproj` — add Spectre.Console.
- `CCStash.slnx` — add new `tests/CCStash.Tests` project.
- `docs/INSTALL.md`, `README.md` — document the new command.

**New — `src/CCStash/Verbs/`:**
- `InstallVerb.cs` — flag parsing, confirm/dry-run gate, TUI, dispatch to adapters.

**New — `tests/CCStash.Core.Tests/Install/`** (existing project, adapters live in `CCStash.Core`):
- `JsonConfigEditorTests.cs`
- `ClaudeCodeAdapterTests.cs`
- `CodeSharpAdapterTests.cs`

**New — `tests/CCStash.Tests/`** (new project; verbs live in the `CCStash` exe project, which has no
test project today):
- `CCStash.Tests.csproj` (references `src/CCStash/CCStash.csproj`)
- `InstallVerbTests.cs` — flag-path smoke tests (`install`/`uninstall`/`init` end-to-end against temp
  dirs).
- `StashVerbArgsTests.cs` — `--transcript`/`--project` override tests.

**Removed:** `src/CCStash/Verbs/InitVerb.cs` (logic now lives in `ClaudeCodeAdapter` + `InstallVerb`).

---

### Task 1: `CCStashPaths` user-scope path helpers + test seam

**Files:**
- Modify: `src/CCStash.Core/Config/CCStashPaths.cs`
- Test: `tests/CCStash.Core.Tests/CCStashPathsTests.cs`

**Interfaces:**
- Produces: `CCStashPaths.ClaudeUserSettingsPath` (`string`, `~/.claude/settings.json`),
  `CCStashPaths.ClaudeUserJsonPath` (`string`, `~/.claude.json`). Both re-evaluate on every access
  (not cached), honoring a `CCSTASH_HOME_OVERRIDE` environment variable when set — this is the seam
  `ClaudeCodeAdapter`'s user-scope tests use to avoid touching the real `~`.

- [ ] **Step 1: Write the failing tests**

```csharp
// Append to tests/CCStash.Core.Tests/CCStashPathsTests.cs, inside class CCStashPathsTests
[Fact]
public void ClaudeUserPaths_use_home_override_when_set()
{
    var fakeHome = Path.Combine(Path.GetTempPath(), $"ccstash-home-{Guid.NewGuid():N}");
    Environment.SetEnvironmentVariable("CCSTASH_HOME_OVERRIDE", fakeHome);
    try
    {
        Assert.Equal(Path.Combine(fakeHome, ".claude", "settings.json"), CCStashPaths.ClaudeUserSettingsPath);
        Assert.Equal(Path.Combine(fakeHome, ".claude.json"), CCStashPaths.ClaudeUserJsonPath);
    }
    finally
    {
        Environment.SetEnvironmentVariable("CCSTASH_HOME_OVERRIDE", null);
    }
}

[Fact]
public void ClaudeUserPaths_fall_back_to_real_user_profile_when_unset()
{
    Environment.SetEnvironmentVariable("CCSTASH_HOME_OVERRIDE", null);
    var expectedHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    Assert.Equal(Path.Combine(expectedHome, ".claude", "settings.json"), CCStashPaths.ClaudeUserSettingsPath);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CCStash.Core.Tests --filter "FullyQualifiedName~CCStashPathsTests"`
Expected: FAIL — `ClaudeUserSettingsPath`/`ClaudeUserJsonPath` do not exist (compile error).

- [ ] **Step 3: Implement**

```csharp
// src/CCStash.Core/Config/CCStashPaths.cs — add inside CCStashPaths, after DbPath
    /// <summary>
    /// Path to the user-scoped Claude Code settings file (<c>~/.claude/settings.json</c>), used by
    /// <c>install --scope user</c> for the Claude Code adapter.
    /// </summary>
    public static string ClaudeUserSettingsPath => Path.Combine(HomeDir, ".claude", "settings.json");

    /// <summary>
    /// Path to Claude Code's user-scoped MCP registry (<c>~/.claude.json</c>), used by
    /// <c>install --scope user</c> for the Claude Code adapter.
    /// </summary>
    public static string ClaudeUserJsonPath => Path.Combine(HomeDir, ".claude.json");

    /// <summary>
    /// Resolves the user's home directory, honoring <c>CCSTASH_HOME_OVERRIDE</c> when set. The
    /// override exists purely as a test seam so user-scope installer tests never touch the real
    /// <c>~</c>; production code paths never set this variable.
    /// </summary>
    private static string HomeDir =>
        Environment.GetEnvironmentVariable("CCSTASH_HOME_OVERRIDE")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
```

Note: `DataDir`/`ConfigPath`/`LogPath` are left untouched (they stay pinned to the real
`UserProfile`, matching today's behavior) — only the two new properties consult the override, and
they are expression-bodied (`=>`), not `{ get; }` field-backed, so they re-resolve on every access.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CCStash.Core.Tests --filter "FullyQualifiedName~CCStashPathsTests"`
Expected: PASS (4 tests: the 2 existing + 2 new).

- [ ] **Step 5: Commit**

```bash
git add src/CCStash.Core/Config/CCStashPaths.cs tests/CCStash.Core.Tests/CCStashPathsTests.cs
git commit -m "feat: add user-scope Claude Code paths with a home-dir test seam"
```

---

### Task 2: `JsonConfigEditor` — shared JSON merge primitives

**Files:**
- Create: `src/CCStash.Core/Install/JsonConfigEditor.cs`
- Test: `tests/CCStash.Core.Tests/Install/JsonConfigEditorTests.cs`

**Interfaces:**
- Produces (all in `namespace CCStash.Core.Install`, `public static class JsonConfigEditor`):
  - `JsonObject LoadOrEmpty(string path)`
  - `void Save(string path, JsonObject root)`
  - `JsonObject GetOrCreateObject(JsonObject container, string key)`
  - `bool EnsureArrayEntry(JsonObject container, string arrayKey, Func<JsonObject, bool> isMatch, Func<JsonObject> buildEntry)` — returns `true` if already present (no-op), `false` if it appended.
  - `bool EnsureChild(JsonObject container, string key, JsonNode value)` — returns `true` if `container[key]` already deep-equals `value` (no-op), `false` if it set/overwrote.
  - `int RemoveArrayEntries(JsonObject container, string arrayKey, Func<JsonObject, bool> isMatch)` — returns count removed.
  - `bool RemoveChild(JsonObject container, string key)`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CCStash.Core.Tests/Install/JsonConfigEditorTests.cs
using System.Text.Json.Nodes;
using CCStash.Core.Install;

namespace CCStash.Core.Tests.Install;

public class JsonConfigEditorTests
{
    [Fact]
    public void LoadOrEmpty_returns_empty_object_when_file_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        var root = JsonConfigEditor.LoadOrEmpty(path);

        Assert.Empty(root);
    }

    [Fact]
    public void LoadOrEmpty_returns_empty_object_when_file_malformed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"malformed-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var root = JsonConfigEditor.LoadOrEmpty(path);
            Assert.Empty(root);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_then_LoadOrEmpty_round_trips_and_creates_parent_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ccstash-jce-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "nested", "settings.json");
        var root = new JsonObject { ["a"] = 1 };

        JsonConfigEditor.Save(path, root);
        var loaded = JsonConfigEditor.LoadOrEmpty(path);

        Assert.Equal(1, (int)loaded["a"]!);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void EnsureArrayEntry_appends_when_absent_and_is_idempotent()
    {
        var container = new JsonObject();

        var firstAlreadyPresent = JsonConfigEditor.EnsureArrayEntry(
            container, "items",
            isMatch: e => (string?)e["id"] == "x",
            buildEntry: () => new JsonObject { ["id"] = "x" });
        var secondAlreadyPresent = JsonConfigEditor.EnsureArrayEntry(
            container, "items",
            isMatch: e => (string?)e["id"] == "x",
            buildEntry: () => new JsonObject { ["id"] = "x" });

        Assert.False(firstAlreadyPresent);
        Assert.True(secondAlreadyPresent);
        Assert.Single(container["items"]!.AsArray());
    }

    [Fact]
    public void EnsureChild_sets_when_absent_reports_already_present_when_equal()
    {
        var container = new JsonObject();

        var first = JsonConfigEditor.EnsureChild(container, "k", JsonValue.Create("v"));
        var second = JsonConfigEditor.EnsureChild(container, "k", JsonValue.Create("v"));
        var third = JsonConfigEditor.EnsureChild(container, "k", JsonValue.Create("v2"));

        Assert.False(first);
        Assert.True(second);
        Assert.False(third);
        Assert.Equal("v2", (string?)container["k"]);
    }

    [Fact]
    public void RemoveArrayEntries_removes_only_matching_entries()
    {
        var container = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["id"] = "x" },
                new JsonObject { ["id"] = "y" }),
        };

        var removed = JsonConfigEditor.RemoveArrayEntries(container, "items", e => (string?)e["id"] == "x");

        Assert.Equal(1, removed);
        Assert.Single(container["items"]!.AsArray());
        Assert.Equal("y", (string?)container["items"]![0]!["id"]);
    }

    [Fact]
    public void RemoveChild_removes_key_and_reports_whether_it_existed()
    {
        var container = new JsonObject { ["k"] = "v" };

        Assert.True(JsonConfigEditor.RemoveChild(container, "k"));
        Assert.False(JsonConfigEditor.RemoveChild(container, "k"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CCStash.Core.Tests --filter "FullyQualifiedName~JsonConfigEditorTests"`
Expected: FAIL — `CCStash.Core.Install` namespace/type does not exist (compile error).

- [ ] **Step 3: Implement**

```csharp
// src/CCStash.Core/Install/JsonConfigEditor.cs
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CCStash.Core.Install;

/// <summary>
/// Shared primitives for reading, idempotently merging, and writing JSON config files used by
/// <see cref="IAgentAdapter"/> implementations. Extracted from the logic that used to live inline in
/// the old <c>init</c> verb.
/// </summary>
public static class JsonConfigEditor
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Load <paramref name="path"/> as a JSON object, or an empty object if it is missing or unreadable.</summary>
    public static JsonObject LoadOrEmpty(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>Serialize <paramref name="root"/> to <paramref name="path"/>, creating parent directories as needed.</summary>
    public static void Save(string path, JsonObject root)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, root.ToJsonString(WriteOptions));
    }

    /// <summary>Get the object at <paramref name="key"/> under <paramref name="container"/>, creating it if absent.</summary>
    public static JsonObject GetOrCreateObject(JsonObject container, string key)
    {
        var obj = container[key] as JsonObject ?? new JsonObject();
        container[key] = obj;
        return obj;
    }

    /// <summary>
    /// Ensure the array at <paramref name="arrayKey"/> under <paramref name="container"/> contains an
    /// entry satisfying <paramref name="isMatch"/>; appends <paramref name="buildEntry"/>'s result if
    /// none does.
    /// </summary>
    /// <returns><see langword="true"/> if a matching entry already existed (no-op); otherwise <see langword="false"/>.</returns>
    public static bool EnsureArrayEntry(
        JsonObject container,
        string arrayKey,
        Func<JsonObject, bool> isMatch,
        Func<JsonObject> buildEntry)
    {
        var arr = container[arrayKey] as JsonArray ?? new JsonArray();
        container[arrayKey] = arr;

        if (arr.OfType<JsonObject>().Any(isMatch))
        {
            return true;
        }

        arr.Add(buildEntry());
        return false;
    }

    /// <summary>Ensure <paramref name="container"/>[<paramref name="key"/>] deep-equals <paramref name="value"/>, setting it if absent or different.</summary>
    /// <returns><see langword="true"/> if it already equaled <paramref name="value"/> (no-op); otherwise <see langword="false"/>.</returns>
    public static bool EnsureChild(JsonObject container, string key, JsonNode value)
    {
        var existing = container[key];
        if (existing is not null && JsonNode.DeepEquals(existing, value))
        {
            return true;
        }

        container[key] = value;
        return false;
    }

    /// <summary>Remove all entries in the array at <paramref name="arrayKey"/> satisfying <paramref name="isMatch"/>.</summary>
    /// <returns>The number of entries removed.</returns>
    public static int RemoveArrayEntries(JsonObject container, string arrayKey, Func<JsonObject, bool> isMatch)
    {
        if (container[arrayKey] is not JsonArray arr)
        {
            return 0;
        }

        var toRemove = arr.OfType<JsonObject>().Where(isMatch).ToList();
        foreach (var entry in toRemove)
        {
            arr.Remove(entry);
        }

        return toRemove.Count;
    }

    /// <summary>Remove <paramref name="key"/> from <paramref name="container"/> if present.</summary>
    /// <returns><see langword="true"/> if it was present and removed; otherwise <see langword="false"/>.</returns>
    public static bool RemoveChild(JsonObject container, string key) => container.Remove(key);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CCStash.Core.Tests --filter "FullyQualifiedName~JsonConfigEditorTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CCStash.Core/Install/JsonConfigEditor.cs tests/CCStash.Core.Tests/Install/JsonConfigEditorTests.cs
git commit -m "feat: extract JsonConfigEditor merge primitives for the installer"
```

---

### Task 3: `IAgentAdapter` + install types

**Files:**
- Create: `src/CCStash.Core/Install/InstallTypes.cs`
- Create: `src/CCStash.Core/Install/IAgentAdapter.cs`

No test file — these are pure data/interface declarations exercised by Task 4/5's adapter tests.

**Interfaces:**
- Produces: `InstallScope { Project, User }`, `InstallContext(InstallScope Scope, string ProjectDir)`,
  `InstallAction(string Target, string Description, bool AlreadyPresent)`,
  `InstallPlan(string Agent, IReadOnlyList<InstallAction> Actions)`, `IAgentAdapter` with members
  `Id`, `DisplayName`, `SupportsScope(InstallScope)`, `Detect(InstallContext)`,
  `Plan(InstallContext)`, `Apply(InstallPlan, InstallContext)`, `Remove(InstallContext)`.

- [ ] **Step 1: Implement `InstallTypes.cs`**

```csharp
// src/CCStash.Core/Install/InstallTypes.cs
namespace CCStash.Core.Install;

/// <summary>Where an agent's CCStash config should be installed.</summary>
public enum InstallScope
{
    /// <summary>Config lives inside the target project (e.g. <c>.claude/settings.json</c>).</summary>
    Project,

    /// <summary>Config lives under the user's home directory (e.g. <c>~/.claude/settings.json</c>).</summary>
    User,
}

/// <summary>The scope and project directory an <see cref="IAgentAdapter"/> operation runs against.</summary>
/// <param name="Scope">Project or user scope.</param>
/// <param name="ProjectDir">
/// The target project's root directory. Used verbatim for <see cref="InstallScope.Project"/> writes,
/// and to derive the absolute path baked into MCP server args either way.
/// </param>
public sealed record InstallContext(InstallScope Scope, string ProjectDir);

/// <summary>One concrete config change an adapter will make (or already made), for dry-run/plan display.</summary>
/// <param name="Target">A human-readable file/key path identifying what this action touches.</param>
/// <param name="Description">A human-readable summary of the change.</param>
/// <param name="AlreadyPresent">True if this action is already satisfied by existing config (a no-op on Apply).</param>
public sealed record InstallAction(string Target, string Description, bool AlreadyPresent);

/// <summary>The full set of actions an adapter will take for one <see cref="InstallContext"/>.</summary>
/// <param name="Agent">The owning adapter's <see cref="IAgentAdapter.Id"/>.</param>
/// <param name="Actions">The individual actions, in application order.</param>
public sealed record InstallPlan(string Agent, IReadOnlyList<InstallAction> Actions);
```

- [ ] **Step 2: Implement `IAgentAdapter.cs`**

```csharp
// src/CCStash.Core/Install/IAgentAdapter.cs
namespace CCStash.Core.Install;

/// <summary>
/// Wires (or unwires) CCStash's hooks/MCP server into one agent's config. Implementations are pure
/// filesystem config writers — no console I/O — so the CLI's TUI and flag paths can both drive them
/// identically via <see cref="Plan"/>/<see cref="Apply"/>.
/// </summary>
public interface IAgentAdapter
{
    /// <summary>Short, stable identifier used on the command line (e.g. <c>"claude"</c>).</summary>
    string Id { get; }

    /// <summary>Human-readable name for TUI/plan display (e.g. <c>"Claude Code"</c>).</summary>
    string DisplayName { get; }

    /// <summary>Whether this adapter can install at the given scope.</summary>
    bool SupportsScope(InstallScope scope);

    /// <summary>Whether this agent appears to already be present/configured for <paramref name="ctx"/>.</summary>
    bool Detect(InstallContext ctx);

    /// <summary>Compute the actions <see cref="Apply"/> would take, without writing anything.</summary>
    InstallPlan Plan(InstallContext ctx);

    /// <summary>Write the config changes described by (a freshly recomputed) <paramref name="plan"/>.</summary>
    void Apply(InstallPlan plan, InstallContext ctx);

    /// <summary>Remove only the CCStash-authored entries this adapter would have written for <paramref name="ctx"/>.</summary>
    void Remove(InstallContext ctx);
}
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build src/CCStash.Core`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/CCStash.Core/Install/InstallTypes.cs src/CCStash.Core/Install/IAgentAdapter.cs
git commit -m "feat: add IAgentAdapter interface and install plan/context types"
```

---

### Task 4: `ClaudeCodeAdapter`

**Files:**
- Create: `src/CCStash.Core/Install/ClaudeCodeAdapter.cs`
- Test: `tests/CCStash.Core.Tests/Install/ClaudeCodeAdapterTests.cs`

**Interfaces:**
- Consumes: `JsonConfigEditor.*` (Task 2), `IAgentAdapter`/`InstallContext`/`InstallPlan`/`InstallAction`
  (Task 3), `CCStashPaths.ClaudeUserSettingsPath`/`ClaudeUserJsonPath` (Task 1).
- Produces: `public sealed class ClaudeCodeAdapter : IAgentAdapter` with `Id == "claude"`, plus public
  constants `ClaudeCodeAdapter.StashCommand` (`"dnx -y CCStash -- stash"`) and
  `ClaudeCodeAdapter.PointerCommand` (`"dnx -y CCStash -- pointer"`) that Task 8's `InstallVerb` and
  its tests reference for assertions.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CCStash.Core.Tests/Install/ClaudeCodeAdapterTests.cs
using System.Text.Json.Nodes;
using CCStash.Core.Config;
using CCStash.Core.Install;

namespace CCStash.Core.Tests.Install;

public class ClaudeCodeAdapterTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _fakeHome;

    public ClaudeCodeAdapterTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"ccstash-cc-proj-{Guid.NewGuid():N}");
        _fakeHome = Path.Combine(Path.GetTempPath(), $"ccstash-cc-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectDir);
        Environment.SetEnvironmentVariable("CCSTASH_HOME_OVERRIDE", _fakeHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CCSTASH_HOME_OVERRIDE", null);
        if (Directory.Exists(_projectDir))
        {
            Directory.Delete(_projectDir, recursive: true);
        }

        if (Directory.Exists(_fakeHome))
        {
            Directory.Delete(_fakeHome, recursive: true);
        }
    }

    [Fact]
    public void SupportsScope_returns_true_for_both_scopes()
    {
        var adapter = new ClaudeCodeAdapter();

        Assert.True(adapter.SupportsScope(InstallScope.Project));
        Assert.True(adapter.SupportsScope(InstallScope.User));
    }

    [Fact]
    public void Plan_reports_not_present_before_apply_and_present_after()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);

        var before = adapter.Plan(ctx);
        Assert.All(before.Actions, a => Assert.False(a.AlreadyPresent));

        adapter.Apply(before, ctx);
        var after = adapter.Plan(ctx);
        Assert.All(after.Actions, a => Assert.True(a.AlreadyPresent));
    }

    [Fact]
    public void Apply_project_scope_writes_hooks_and_mcp_with_absolute_project_path()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);

        adapter.Apply(adapter.Plan(ctx), ctx);

        var settings = JsonConfigEditor.LoadOrEmpty(Path.Combine(_projectDir, ".claude", "settings.json"));
        var hooks = settings["hooks"]!.AsObject();
        Assert.Equal(ClaudeCodeAdapter.StashCommand, (string?)hooks["PreCompact"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal(ClaudeCodeAdapter.PointerCommand, (string?)hooks["SessionStart"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal("compact", (string?)hooks["SessionStart"]![0]!["matcher"]);

        var mcp = JsonConfigEditor.LoadOrEmpty(Path.Combine(_projectDir, ".mcp.json"));
        var server = mcp["mcpServers"]!["ccstash"]!;
        Assert.Equal("dnx", (string?)server["command"]);
        var args = server["args"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Equal(new[] { "-y", "CCStash", "--", "mcp", "--project", Path.GetFullPath(_projectDir) }, args);
    }

    [Fact]
    public void Apply_user_scope_writes_to_home_settings_and_claude_json_without_project_arg()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = new InstallContext(InstallScope.User, _projectDir);

        adapter.Apply(adapter.Plan(ctx), ctx);

        var settings = JsonConfigEditor.LoadOrEmpty(CCStashPaths.ClaudeUserSettingsPath);
        Assert.Equal(
            ClaudeCodeAdapter.StashCommand,
            (string?)settings["hooks"]!["PreCompact"]![0]!["hooks"]![0]!["command"]);

        var root = JsonConfigEditor.LoadOrEmpty(CCStashPaths.ClaudeUserJsonPath);
        var args = root["mcpServers"]!["ccstash"]!["args"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Equal(new[] { "-y", "CCStash", "--", "mcp" }, args);
    }

    [Fact]
    public void Apply_preserves_unrelated_existing_hooks_and_mcp_entries()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);
        var settingsPath = Path.Combine(_projectDir, ".claude", "settings.json");
        JsonConfigEditor.Save(settingsPath, new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreCompact"] = new JsonArray(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "some-other-tool" }),
                }),
            },
            ["unrelatedTopLevelKey"] = "keep-me",
        });

        adapter.Apply(adapter.Plan(ctx), ctx);

        var settings = JsonConfigEditor.LoadOrEmpty(settingsPath);
        Assert.Equal("keep-me", (string?)settings["unrelatedTopLevelKey"]);
        var preCompactCommands = settings["hooks"]!["PreCompact"]!.AsArray()
            .Select(e => (string?)e!["hooks"]![0]!["command"]).ToList();
        Assert.Contains("some-other-tool", preCompactCommands);
        Assert.Contains(ClaudeCodeAdapter.StashCommand, preCompactCommands);
    }

    [Fact]
    public void Remove_deletes_only_ccstash_entries()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);
        adapter.Apply(adapter.Plan(ctx), ctx);
        var settingsPath = Path.Combine(_projectDir, ".claude", "settings.json");
        var settings = JsonConfigEditor.LoadOrEmpty(settingsPath);
        JsonConfigEditor.GetOrCreateObject(settings, "hooks");
        JsonConfigEditor.EnsureArrayEntry(
            settings["hooks"]!.AsObject(), "PreCompact",
            e => (string?)e["hooks"]![0]!["command"] == "some-other-tool",
            () => new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "some-other-tool" }) });
        JsonConfigEditor.Save(settingsPath, settings);

        adapter.Remove(ctx);

        var after = JsonConfigEditor.LoadOrEmpty(settingsPath);
        var preCompactCommands = after["hooks"]!["PreCompact"]!.AsArray()
            .Select(e => (string?)e!["hooks"]![0]!["command"]).ToList();
        Assert.DoesNotContain(ClaudeCodeAdapter.StashCommand, preCompactCommands);
        Assert.Contains("some-other-tool", preCompactCommands);
        var mcp = JsonConfigEditor.LoadOrEmpty(Path.Combine(_projectDir, ".mcp.json"));
        Assert.Null(mcp["mcpServers"]?["ccstash"]);
    }

    [Fact]
    public void Detect_is_false_for_a_fresh_project_and_true_after_apply()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);

        Assert.False(adapter.Detect(ctx));
        adapter.Apply(adapter.Plan(ctx), ctx);
        Assert.True(adapter.Detect(ctx));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CCStash.Core.Tests --filter "FullyQualifiedName~ClaudeCodeAdapterTests"`
Expected: FAIL — `ClaudeCodeAdapter` does not exist (compile error).

- [ ] **Step 3: Implement**

```csharp
// src/CCStash.Core/Install/ClaudeCodeAdapter.cs
using System.Text.Json.Nodes;
using CCStash.Core.Config;

namespace CCStash.Core.Install;

/// <summary>
/// Wires CCStash into Claude Code: <c>PreCompact</c>/<c>SessionStart[compact]</c> hooks and an
/// <c>ccstash</c> MCP server entry. Project scope writes <c>.claude/settings.json</c> +
/// <c>.mcp.json</c>; user scope writes <c>~/.claude/settings.json</c> + <c>~/.claude.json</c>.
/// </summary>
public sealed class ClaudeCodeAdapter : IAgentAdapter
{
    /// <summary>The exact <c>PreCompact</c> hook command. Kept stable for idempotency across versions.</summary>
    public const string StashCommand = "dnx -y CCStash -- stash";

    /// <summary>The exact <c>SessionStart[compact]</c> hook command. Kept stable for idempotency across versions.</summary>
    public const string PointerCommand = "dnx -y CCStash -- pointer";

    /// <inheritdoc />
    public string Id => "claude";

    /// <inheritdoc />
    public string DisplayName => "Claude Code";

    /// <inheritdoc />
    public bool SupportsScope(InstallScope scope) => true;

    /// <inheritdoc />
    public bool Detect(InstallContext ctx) =>
        ctx.Scope == InstallScope.Project
            ? Directory.Exists(Path.Combine(ctx.ProjectDir, ".claude")) || File.Exists(Path.Combine(ctx.ProjectDir, ".mcp.json"))
            : File.Exists(CCStashPaths.ClaudeUserJsonPath) || File.Exists(CCStashPaths.ClaudeUserSettingsPath);

    /// <inheritdoc />
    public InstallPlan Plan(InstallContext ctx)
    {
        var settingsPath = SettingsPath(ctx);
        var settings = JsonConfigEditor.LoadOrEmpty(settingsPath);
        var hooks = JsonConfigEditor.GetOrCreateObject(settings, "hooks");

        var preCompactPresent = EnsureHookEntry(hooks, "PreCompact", matcher: null, StashCommand);
        var sessionStartPresent = EnsureHookEntry(hooks, "SessionStart", matcher: "compact", PointerCommand);

        var actions = new List<InstallAction>
        {
            new($"{settingsPath} hooks.PreCompact", $"Run `{StashCommand}` on PreCompact", preCompactPresent),
            new($"{settingsPath} hooks.SessionStart[compact]", $"Run `{PointerCommand}` on SessionStart (compact)", sessionStartPresent),
        };

        var mcpPath = McpPath(ctx);
        var mcpRoot = JsonConfigEditor.LoadOrEmpty(mcpPath);
        var servers = JsonConfigEditor.GetOrCreateObject(mcpRoot, "mcpServers");
        var mcpPresent = JsonConfigEditor.EnsureChild(servers, "ccstash", McpServerNode(ctx));
        actions.Add(new($"{mcpPath} mcpServers.ccstash", $"Register the ccstash MCP server ({ctx.Scope} scope)", mcpPresent));

        return new InstallPlan(Id, actions);
    }

    /// <inheritdoc />
    public void Apply(InstallPlan plan, InstallContext ctx)
    {
        var settingsPath = SettingsPath(ctx);
        var settings = JsonConfigEditor.LoadOrEmpty(settingsPath);
        var hooks = JsonConfigEditor.GetOrCreateObject(settings, "hooks");
        EnsureHookEntry(hooks, "PreCompact", matcher: null, StashCommand);
        EnsureHookEntry(hooks, "SessionStart", matcher: "compact", PointerCommand);
        JsonConfigEditor.Save(settingsPath, settings);

        var mcpPath = McpPath(ctx);
        var mcpRoot = JsonConfigEditor.LoadOrEmpty(mcpPath);
        var servers = JsonConfigEditor.GetOrCreateObject(mcpRoot, "mcpServers");
        JsonConfigEditor.EnsureChild(servers, "ccstash", McpServerNode(ctx));
        JsonConfigEditor.Save(mcpPath, mcpRoot);
    }

    /// <inheritdoc />
    public void Remove(InstallContext ctx)
    {
        var settingsPath = SettingsPath(ctx);
        if (File.Exists(settingsPath))
        {
            var settings = JsonConfigEditor.LoadOrEmpty(settingsPath);
            if (settings["hooks"] is JsonObject hooks)
            {
                JsonConfigEditor.RemoveArrayEntries(hooks, "PreCompact", e => HasCommand(e, StashCommand));
                JsonConfigEditor.RemoveArrayEntries(hooks, "SessionStart", e => HasCommand(e, PointerCommand));
            }

            JsonConfigEditor.Save(settingsPath, settings);
        }

        var mcpPath = McpPath(ctx);
        if (File.Exists(mcpPath))
        {
            var root = JsonConfigEditor.LoadOrEmpty(mcpPath);
            if (root["mcpServers"] is JsonObject servers)
            {
                JsonConfigEditor.RemoveChild(servers, "ccstash");
            }

            JsonConfigEditor.Save(mcpPath, root);
        }
    }

    private static bool EnsureHookEntry(JsonObject hooks, string eventName, string? matcher, string command) =>
        JsonConfigEditor.EnsureArrayEntry(
            hooks,
            eventName,
            entry => HasCommand(entry, command),
            () =>
            {
                var entry = new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }),
                };
                if (matcher is not null)
                {
                    entry["matcher"] = matcher;
                }

                return entry;
            });

    private static bool HasCommand(JsonObject hookEventEntry, string command) =>
        (hookEventEntry["hooks"] as JsonArray)?.OfType<JsonObject>().Any(h => (string?)h["command"] == command) == true;

    private static JsonObject McpServerNode(InstallContext ctx)
    {
        var args = new JsonArray("-y", "CCStash", "--", "mcp");
        if (ctx.Scope == InstallScope.Project)
        {
            args.Add("--project");
            args.Add(Path.GetFullPath(ctx.ProjectDir));
        }

        return new JsonObject { ["command"] = "dnx", ["args"] = args };
    }

    private static string SettingsPath(InstallContext ctx) =>
        ctx.Scope == InstallScope.Project
            ? Path.Combine(ctx.ProjectDir, ".claude", "settings.json")
            : CCStashPaths.ClaudeUserSettingsPath;

    private static string McpPath(InstallContext ctx) =>
        ctx.Scope == InstallScope.Project
            ? Path.Combine(ctx.ProjectDir, ".mcp.json")
            : CCStashPaths.ClaudeUserJsonPath;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CCStash.Core.Tests --filter "FullyQualifiedName~ClaudeCodeAdapterTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CCStash.Core/Install/ClaudeCodeAdapter.cs tests/CCStash.Core.Tests/Install/ClaudeCodeAdapterTests.cs
git commit -m "feat: add ClaudeCodeAdapter for project and user scope install"
```

---

### Task 5: `CodeSharpAdapter`

**Files:**
- Create: `src/CCStash.Core/Install/CodeSharpAdapter.cs`
- Test: `tests/CCStash.Core.Tests/Install/CodeSharpAdapterTests.cs`

**Interfaces:**
- Consumes: `JsonConfigEditor.*`, `IAgentAdapter`/`InstallContext`/`InstallPlan`/`InstallAction`.
- Produces: `public sealed class CodeSharpAdapter : IAgentAdapter` with `Id == "codesharp"`, public
  constant `CodeSharpAdapter.SkillFileName` (`"ccstash.md"`, under `.codesharp/skills/`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CCStash.Core.Tests/Install/CodeSharpAdapterTests.cs
using CCStash.Core.Install;

namespace CCStash.Core.Tests.Install;

public class CodeSharpAdapterTests : IDisposable
{
    private readonly string _projectDir;

    public CodeSharpAdapterTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"ccstash-cs-proj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDir))
        {
            Directory.Delete(_projectDir, recursive: true);
        }
    }

    [Fact]
    public void SupportsScope_is_project_only_in_SP1()
    {
        var adapter = new CodeSharpAdapter();

        Assert.True(adapter.SupportsScope(InstallScope.Project));
        Assert.False(adapter.SupportsScope(InstallScope.User));
    }

    [Fact]
    public void Apply_writes_appsettings_mcp_entry_and_skill_file()
    {
        var adapter = new CodeSharpAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);

        adapter.Apply(adapter.Plan(ctx), ctx);

        var appsettings = JsonConfigEditor.LoadOrEmpty(Path.Combine(_projectDir, "appsettings.json"));
        var server = appsettings["CodeSharp"]!["Mcp"]!["Servers"]!["ccstash"]!;
        Assert.Equal("stdio", (string?)server["Transport"]);
        Assert.Equal("dotnet", (string?)server["Command"]);
        var args = server["Args"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Equal(new[] { "dnx", "CCStash", "--", "mcp", "--project", Path.GetFullPath(_projectDir) }, args);

        var skillPath = Path.Combine(_projectDir, ".codesharp", "skills", CodeSharpAdapter.SkillFileName);
        Assert.True(File.Exists(skillPath));
        var content = File.ReadAllText(skillPath);
        Assert.Contains("event: SessionStart", content);
        Assert.Contains("dotnet dnx CCStash -- pointer --project {ProjectDir}", content);
        Assert.Contains("event: SessionEnd", content);
        Assert.Contains("dotnet dnx CCStash -- stash --transcript {TranscriptPath} --project {ProjectDir}", content);
    }

    [Fact]
    public void Plan_reports_already_present_after_apply()
    {
        var adapter = new CodeSharpAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);
        adapter.Apply(adapter.Plan(ctx), ctx);

        var plan = adapter.Plan(ctx);

        Assert.All(plan.Actions, a => Assert.True(a.AlreadyPresent));
    }

    [Fact]
    public void Remove_deletes_skill_file_and_mcp_entry_only()
    {
        var adapter = new CodeSharpAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);
        adapter.Apply(adapter.Plan(ctx), ctx);
        var appsettingsPath = Path.Combine(_projectDir, "appsettings.json");
        var appsettings = JsonConfigEditor.LoadOrEmpty(appsettingsPath);
        JsonConfigEditor.GetOrCreateObject(JsonConfigEditor.GetOrCreateObject(appsettings, "CodeSharp"), "Other")["Key"] = "keep-me";
        JsonConfigEditor.Save(appsettingsPath, appsettings);

        adapter.Remove(ctx);

        Assert.False(File.Exists(Path.Combine(_projectDir, ".codesharp", "skills", CodeSharpAdapter.SkillFileName)));
        var after = JsonConfigEditor.LoadOrEmpty(appsettingsPath);
        Assert.Null(after["CodeSharp"]?["Mcp"]?["Servers"]?["ccstash"]);
        Assert.Equal("keep-me", (string?)after["CodeSharp"]?["Other"]?["Key"]);
    }

    [Fact]
    public void Detect_true_when_codesharp_dir_or_appsettings_section_exists()
    {
        var adapter = new CodeSharpAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);
        Assert.False(adapter.Detect(ctx));

        Directory.CreateDirectory(Path.Combine(_projectDir, ".codesharp"));
        Assert.True(adapter.Detect(ctx));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CCStash.Core.Tests --filter "FullyQualifiedName~CodeSharpAdapterTests"`
Expected: FAIL — `CodeSharpAdapter` does not exist (compile error).

- [ ] **Step 3: Implement**

```csharp
// src/CCStash.Core/Install/CodeSharpAdapter.cs
using System.Text.Json.Nodes;

namespace CCStash.Core.Install;

/// <summary>
/// Wires CCStash into CodeSharp (project scope only in SP1): an <c>appsettings.json</c>
/// <c>CodeSharp:Mcp:Servers:ccstash</c> entry, and a <c>.codesharp/skills/ccstash.md</c> hook-skill
/// with <c>SessionStart</c>/<c>SessionEnd</c> command hooks. The <c>SessionEnd</c> hook references a
/// <c>{TranscriptPath}</c> placeholder CodeSharp does not yet provide (SP2); until then it runs and
/// finds no transcript, which is hook-safe (<c>stash</c> always exits 0).
/// </summary>
public sealed class CodeSharpAdapter : IAgentAdapter
{
    /// <summary>File name of the generated hook-skill under <c>.codesharp/skills/</c>.</summary>
    public const string SkillFileName = "ccstash.md";

    private const string PointerCommandTemplate = "dotnet dnx CCStash -- pointer --project {ProjectDir}";
    private const string StashCommandTemplate = "dotnet dnx CCStash -- stash --transcript {TranscriptPath} --project {ProjectDir}";

    /// <inheritdoc />
    public string Id => "codesharp";

    /// <inheritdoc />
    public string DisplayName => "CodeSharp";

    /// <inheritdoc />
    public bool SupportsScope(InstallScope scope) => scope == InstallScope.Project;

    /// <inheritdoc />
    public bool Detect(InstallContext ctx)
    {
        if (Directory.Exists(Path.Combine(ctx.ProjectDir, ".codesharp")))
        {
            return true;
        }

        var appsettings = JsonConfigEditor.LoadOrEmpty(AppSettingsPath(ctx));
        return appsettings["CodeSharp"] is not null;
    }

    /// <inheritdoc />
    public InstallPlan Plan(InstallContext ctx)
    {
        RequireProjectScope(ctx);

        var appsettingsPath = AppSettingsPath(ctx);
        var appsettings = JsonConfigEditor.LoadOrEmpty(appsettingsPath);
        var servers = McpServersObject(appsettings);
        var mcpPresent = JsonConfigEditor.EnsureChild(servers, "ccstash", McpServerNode(ctx));

        var skillPath = SkillPath(ctx);
        var skillPresent = File.Exists(skillPath) && File.ReadAllText(skillPath) == SkillFileContent;

        return new InstallPlan(Id, new List<InstallAction>
        {
            new($"{appsettingsPath} CodeSharp:Mcp:Servers:ccstash", "Register the ccstash MCP server", mcpPresent),
            new(skillPath, "Write the ccstash SessionStart/SessionEnd hook-skill", skillPresent),
        });
    }

    /// <inheritdoc />
    public void Apply(InstallPlan plan, InstallContext ctx)
    {
        RequireProjectScope(ctx);

        var appsettingsPath = AppSettingsPath(ctx);
        var appsettings = JsonConfigEditor.LoadOrEmpty(appsettingsPath);
        var servers = McpServersObject(appsettings);
        JsonConfigEditor.EnsureChild(servers, "ccstash", McpServerNode(ctx));
        JsonConfigEditor.Save(appsettingsPath, appsettings);

        var skillPath = SkillPath(ctx);
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        File.WriteAllText(skillPath, SkillFileContent);
    }

    /// <inheritdoc />
    public void Remove(InstallContext ctx)
    {
        RequireProjectScope(ctx);

        var appsettingsPath = AppSettingsPath(ctx);
        if (File.Exists(appsettingsPath))
        {
            var appsettings = JsonConfigEditor.LoadOrEmpty(appsettingsPath);
            if (appsettings["CodeSharp"]?["Mcp"]?["Servers"] is JsonObject servers)
            {
                JsonConfigEditor.RemoveChild(servers, "ccstash");
            }

            JsonConfigEditor.Save(appsettingsPath, appsettings);
        }

        var skillPath = SkillPath(ctx);
        if (File.Exists(skillPath))
        {
            File.Delete(skillPath);
        }
    }

    private static void RequireProjectScope(InstallContext ctx)
    {
        if (ctx.Scope != InstallScope.Project)
        {
            throw new NotSupportedException("CodeSharpAdapter supports project scope only in SP1.");
        }
    }

    private static JsonObject McpServersObject(JsonObject appsettings) =>
        JsonConfigEditor.GetOrCreateObject(
            JsonConfigEditor.GetOrCreateObject(
                JsonConfigEditor.GetOrCreateObject(appsettings, "CodeSharp"), "Mcp"), "Servers");

    private static JsonObject McpServerNode(InstallContext ctx) => new()
    {
        ["Transport"] = "stdio",
        ["Command"] = "dotnet",
        ["Args"] = new JsonArray("dnx", "CCStash", "--", "mcp", "--project", Path.GetFullPath(ctx.ProjectDir)),
    };

    private static string AppSettingsPath(InstallContext ctx) => Path.Combine(ctx.ProjectDir, "appsettings.json");

    private static string SkillPath(InstallContext ctx) => Path.Combine(ctx.ProjectDir, ".codesharp", "skills", SkillFileName);

    private static string SkillFileContent =>
        "---\n" +
        "hooks:\n" +
        "  - event: SessionStart\n" +
        "    handler:\n" +
        "      type: command\n" +
        $"      command: \"{PointerCommandTemplate}\"\n" +
        "  - event: SessionEnd\n" +
        "    handler:\n" +
        "      type: command\n" +
        $"      command: \"{StashCommandTemplate}\"\n" +
        "---\n\n" +
        "# CCStash\n\n" +
        "Wires CCStash's pointer/stash hooks into CodeSharp. Managed by `ccstash install` — do not " +
        "edit by hand; run `ccstash uninstall --agent codesharp` to remove.\n";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CCStash.Core.Tests --filter "FullyQualifiedName~CodeSharpAdapterTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CCStash.Core/Install/CodeSharpAdapter.cs tests/CCStash.Core.Tests/Install/CodeSharpAdapterTests.cs
git commit -m "feat: add CodeSharpAdapter (project-scope MCP entry + hook-skill)"
```

---

### Task 6: New `tests/CCStash.Tests` project (scaffolding)

**Files:**
- Create: `tests/CCStash.Tests/CCStash.Tests.csproj`
- Modify: `CCStash.slnx`

No verb code exists yet to test against `CCStash.Verbs`, so this task only scaffolds the project and
proves it builds/runs (zero tests); Tasks 8 and 9 add real test files into it.

**Interfaces:**
- Produces: a buildable, `dotnet test`-discoverable project referencing `src/CCStash/CCStash.csproj`.

- [ ] **Step 1: Create the project file**

```xml
<!-- tests/CCStash.Tests/CCStash.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\CCStash\CCStash.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add a placeholder test so the project is non-empty**

```csharp
// tests/CCStash.Tests/PlaceholderTests.cs
namespace CCStash.Tests;

public class PlaceholderTests
{
    [Fact]
    public void Project_scaffolding_builds_and_runs()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 3: Register the project in `CCStash.slnx`**

```xml
<!-- CCStash.slnx — add this line inside <Folder Name="/tests/"> -->
    <Project Path="tests/CCStash.Tests/CCStash.Tests.csproj" />
```

- [ ] **Step 4: Run to verify it builds and discovers the test**

Run: `dotnet test tests/CCStash.Tests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add tests/CCStash.Tests/CCStash.Tests.csproj tests/CCStash.Tests/PlaceholderTests.cs CCStash.slnx
git commit -m "test: scaffold a CCStash.Tests project for CLI verb tests"
```

---

### Task 7: `stash` gains `--transcript`/`--project` CLI overrides

**Files:**
- Modify: `src/CCStash/Verbs/StashVerb.cs`
- Modify: `src/CCStash/Program.cs`
- Test: `tests/CCStash.Tests/StashVerbArgsTests.cs`

**Interfaces:**
- Consumes: `HookInput` record (`src/CCStash.Core/Hooks/HookInput.cs`) — used via its `with` expression.
- Produces: `StashVerb.RunAsync(string[] args, TextReader stdin)` (signature changed from
  `RunAsync(TextReader stdin)`) — CodeSharp's generated hook command
  (`dotnet dnx CCStash -- stash --transcript {TranscriptPath} --project {ProjectDir}`, Task 5) now
  resolves correctly.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/CCStash.Tests/StashVerbArgsTests.cs
using CCStash.Verbs;

namespace CCStash.Tests;

public class StashVerbArgsTests
{
    [Fact]
    public async Task RunAsync_uses_transcript_arg_when_stdin_has_no_valid_json()
    {
        // Empty stdin would normally make HookInput.FromJson throw; --transcript/--project let the
        // CodeSharp hook (which has no stdin payload) still resolve a usable HookInput.
        var dir = Path.Combine(Path.GetTempPath(), $"ccstash-stash-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var missingTranscript = Path.Combine(dir, "transcript.jsonl");
            var args = new[] { "--transcript", missingTranscript, "--project", dir };

            var exitCode = await StashVerb.RunAsync(args, new StringReader(string.Empty));

            // stash is hook-safe: even with a nonexistent transcript file, it logs and returns 0.
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CCStash.Tests --filter "FullyQualifiedName~StashVerbArgsTests"`
Expected: FAIL — `StashVerb.RunAsync(string[], TextReader)` overload does not exist (compile error).

- [ ] **Step 3: Implement**

```csharp
// src/CCStash/Verbs/StashVerb.cs — replace the file
using CCStash.Core;
using CCStash.Core.Config;
using CCStash.Core.Hooks;

namespace CCStash.Verbs;

/// <summary>Handles the <c>stash</c> verb (invoked by the PreCompact hook). Never throws.</summary>
internal static class StashVerb
{
    /// <summary>
    /// Read the hook payload from stdin, apply any <c>--transcript</c>/<c>--project</c> overrides,
    /// stash incrementally, and always exit 0.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, TextReader stdin)
    {
        try
        {
            var stdinText = await stdin.ReadToEndAsync();
            var input = BuildInput(args, stdinText);
            var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);

            // Bound the work so a slow embed can never hang compaction.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.StashTimeoutSeconds));
            var svc = await Composition.BuildStashAsync(input.Cwd, cfg);
            var result = await svc.StashAsync(
                new StashRequest(input.TranscriptPath, CCStashPaths.ProjectHash(input.Cwd), input.SessionId),
                cts.Token);
            Log($"stash ok: +{result.NewChunks} ({result.TotalChunks} total) {result.StashId}");

            // Record this project's root against its db hash so `gc` can later distinguish a live
            // project from one whose directory was removed. Best-effort; never throws.
            ProjectRegistry.Record(CCStashPaths.DataDir, input.Cwd, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            Log($"stash failed: {ex.Message}");
        }

        return 0; // hook safety: always succeed
    }

    /// <summary>
    /// Parse stdin as <see cref="HookInput"/> when present, falling back to a minimal input built
    /// from <c>--transcript</c>/<c>--project</c> when stdin is empty/invalid — the CodeSharp hook
    /// invocation supplies no stdin payload, only these CLI args. CLI args always override the
    /// corresponding stdin field when both are present.
    /// </summary>
    private static HookInput BuildInput(string[] args, string stdinText)
    {
        var transcript = ArgValue(args, "--transcript");
        var project = ArgValue(args, "--project");

        HookInput input;
        try
        {
            input = HookInput.FromJson(stdinText);
        }
        catch (System.Text.Json.JsonException) when (transcript is not null && project is not null)
        {
            input = new HookInput(SessionId: Guid.NewGuid().ToString("N"), TranscriptPath: transcript, Cwd: project, Source: null, CompactionTriggeredBy: null);
        }

        if (transcript is not null)
        {
            input = input with { TranscriptPath = transcript };
        }

        if (project is not null)
        {
            input = input with { Cwd = project };
        }

        return input;
    }

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(CCStashPaths.DataDir);
            File.AppendAllText(CCStashPaths.LogPath, $"{DateTimeOffset.Now:O} {msg}{Environment.NewLine}");
        }
        catch
        {
            // logging must never break the hook
        }
    }
}
```

```csharp
// src/CCStash/Program.cs line 15 — change
    "stash" => await StashVerb.RunAsync(Console.In),
// to
    "stash" => await StashVerb.RunAsync(args[1..], Console.In),
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CCStash.Tests --filter "FullyQualifiedName~StashVerbArgsTests"`
Expected: PASS.

Also run the full existing suite to confirm nothing else references the old `StashVerb.RunAsync(TextReader)` overload:
Run: `dotnet build CCStash.slnx`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/CCStash/Verbs/StashVerb.cs src/CCStash/Program.cs tests/CCStash.Tests/StashVerbArgsTests.cs
git commit -m "feat: stash accepts --transcript/--project overrides for non-Claude-Code hooks"
```

---

### Task 8: `InstallVerb` — non-interactive flag path (`install`, `uninstall`)

**Files:**
- Create: `src/CCStash/Verbs/InstallVerb.cs`
- Modify: `src/CCStash/CCStash.csproj` (add Spectre.Console — needed by Task 9, added now so the file compiles once Task 9 lands; this task's code does not call Spectre APIs yet)
- Test: `tests/CCStash.Tests/InstallVerbTests.cs`

This task implements only the flag-driven path + `Remove`/uninstall + dry-run + confirm gate. Task 9
adds the TUI branch and wires `Program.cs`/`init`.

**Interfaces:**
- Consumes: `IAgentAdapter`, `ClaudeCodeAdapter`, `CodeSharpAdapter`, `InstallContext`, `InstallPlan`,
  `InstallScope` (`CCStash.Core.Install`).
- Produces:
  - `InstallVerb.AllAdapters` (`IReadOnlyList<IAgentAdapter>`, internal) — `[new ClaudeCodeAdapter(), new CodeSharpAdapter()]`.
  - `InstallVerb.RunAsync(string[] args, string cwd, TextReader stdin, TextWriter stdout, TextWriter stderr)` — install path.
  - `InstallVerb.RunUninstallAsync(string[] args, string cwd, TextReader stdin, TextWriter stdout, TextWriter stderr)` — uninstall path.

- [ ] **Step 1: Add the Spectre.Console package reference (build-only for this task)**

```xml
<!-- src/CCStash/CCStash.csproj — add inside the existing PackageReference ItemGroup -->
    <PackageReference Include="Spectre.Console" Version="0.49.1" />
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/CCStash.Tests/InstallVerbTests.cs
using System.Text.Json.Nodes;
using CCStash.Core.Config;
using CCStash.Core.Install;
using CCStash.Verbs;

namespace CCStash.Tests;

public class InstallVerbTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _fakeHome;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public InstallVerbTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"ccstash-installverb-{Guid.NewGuid():N}");
        _fakeHome = Path.Combine(Path.GetTempPath(), $"ccstash-installverb-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectDir);
        Environment.SetEnvironmentVariable("CCSTASH_HOME_OVERRIDE", _fakeHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CCSTASH_HOME_OVERRIDE", null);
        if (Directory.Exists(_projectDir))
        {
            Directory.Delete(_projectDir, recursive: true);
        }

        if (Directory.Exists(_fakeHome))
        {
            Directory.Delete(_fakeHome, recursive: true);
        }
    }

    [Fact]
    public async Task Install_all_agents_project_scope_yes_writes_expected_files()
    {
        var args = new[] { "--agent", "all", "--scope", "project", "--project", _projectDir, "--yes" };

        var exit = await InstallVerb.RunAsync(args, _projectDir, new StringReader(string.Empty), _stdout, _stderr);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_projectDir, ".claude", "settings.json")));
        Assert.True(File.Exists(Path.Combine(_projectDir, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(_projectDir, "appsettings.json")));
        Assert.True(File.Exists(Path.Combine(_projectDir, ".codesharp", "skills", CodeSharpAdapter.SkillFileName)));
    }

    [Fact]
    public async Task Install_dry_run_writes_nothing()
    {
        var args = new[] { "--agent", "claude", "--scope", "project", "--project", _projectDir, "--dry-run" };

        var exit = await InstallVerb.RunAsync(args, _projectDir, new StringReader(string.Empty), _stdout, _stderr);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(_projectDir, ".claude", "settings.json")));
        Assert.Contains("dry-run", _stdout.ToString());
    }

    [Fact]
    public async Task Install_without_yes_aborts_on_declined_confirm()
    {
        var args = new[] { "--agent", "claude", "--scope", "project", "--project", _projectDir };

        var exit = await InstallVerb.RunAsync(args, _projectDir, new StringReader("n\n"), _stdout, _stderr);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(Path.Combine(_projectDir, ".claude", "settings.json")));
    }

    [Fact]
    public async Task Install_without_yes_applies_on_confirmed_yes()
    {
        var args = new[] { "--agent", "claude", "--scope", "project", "--project", _projectDir };

        var exit = await InstallVerb.RunAsync(args, _projectDir, new StringReader("y\n"), _stdout, _stderr);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_projectDir, ".claude", "settings.json")));
    }

    [Fact]
    public async Task Uninstall_removes_only_ccstash_entries_written_by_install()
    {
        var installArgs = new[] { "--agent", "all", "--scope", "project", "--project", _projectDir, "--yes" };
        await InstallVerb.RunAsync(installArgs, _projectDir, new StringReader(string.Empty), _stdout, _stderr);

        var uninstallArgs = new[] { "--agent", "all", "--scope", "project", "--project", _projectDir, "--yes" };
        var exit = await InstallVerb.RunUninstallAsync(uninstallArgs, _projectDir, new StringReader(string.Empty), _stdout, _stderr);

        Assert.Equal(0, exit);
        var mcp = JsonConfigEditor.LoadOrEmpty(Path.Combine(_projectDir, ".mcp.json"));
        Assert.Null(mcp["mcpServers"]?["ccstash"]);
        Assert.False(File.Exists(Path.Combine(_projectDir, ".codesharp", "skills", CodeSharpAdapter.SkillFileName)));
    }

    [Fact]
    public async Task Install_unknown_agent_reports_error_and_exits_nonzero()
    {
        var args = new[] { "--agent", "nope", "--scope", "project", "--project", _projectDir, "--yes" };

        var exit = await InstallVerb.RunAsync(args, _projectDir, new StringReader(string.Empty), _stdout, _stderr);

        Assert.Equal(1, exit);
        Assert.Contains("nope", _stderr.ToString());
    }

    [Fact]
    public async Task Install_user_scope_writes_under_fake_home_not_project()
    {
        var args = new[] { "--agent", "claude", "--scope", "user", "--project", _projectDir, "--yes" };

        var exit = await InstallVerb.RunAsync(args, _projectDir, new StringReader(string.Empty), _stdout, _stderr);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(_projectDir, ".claude", "settings.json")));
        Assert.True(File.Exists(CCStashPaths.ClaudeUserSettingsPath));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/CCStash.Tests --filter "FullyQualifiedName~InstallVerbTests"`
Expected: FAIL — `InstallVerb` does not exist (compile error).

- [ ] **Step 4: Implement**

```csharp
// src/CCStash/Verbs/InstallVerb.cs
using CCStash.Core.Install;

namespace CCStash.Verbs;

/// <summary>
/// Handles the <c>install</c>/<c>uninstall</c> verbs: wires (or unwires) CCStash's config into one
/// or more agents, at project or user scope, via <see cref="IAgentAdapter"/> — non-interactively from
/// flags, or through a TUI when no flags are given and a terminal is attached.
/// </summary>
internal static class InstallVerb
{
    internal const string UsageLine =
        "Usage: ccstash install --agent <claude|codesharp|all> --scope <project|user> [--project <path>] [--yes] [--dry-run]";

    internal static readonly IReadOnlyList<IAgentAdapter> AllAdapters =
    [
        new ClaudeCodeAdapter(),
        new CodeSharpAdapter(),
    ];

    /// <summary>Run <c>install</c>: flag-driven when flags are present, otherwise usage/error (TUI added in a later task).</summary>
    public static Task<int> RunAsync(string[] args, string cwd, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (HasActionableFlags(args))
        {
            return RunSelectedAsync(args, cwd, stdin, stdout, stderr, uninstall: false);
        }

        stderr.WriteLine(UsageLine);
        return Task.FromResult(1);
    }

    /// <summary>Run <c>uninstall</c>: same selectors as <c>install</c>, dispatching to <see cref="IAgentAdapter.Remove"/>.</summary>
    public static Task<int> RunUninstallAsync(string[] args, string cwd, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        RunSelectedAsync(args, cwd, stdin, stdout, stderr, uninstall: true);

    private static async Task<int> RunSelectedAsync(
        string[] args, string cwd, TextReader stdin, TextWriter stdout, TextWriter stderr, bool uninstall)
    {
        var requestedAgents = ParseAgents(args);
        var unknown = requestedAgents.Where(id => AllAdapters.All(a => a.Id != id)).ToList();
        if (unknown.Count > 0)
        {
            stderr.WriteLine($"Unknown agent(s): {string.Join(',', unknown)}. Known: {string.Join(',', AllAdapters.Select(a => a.Id))}");
            return 1;
        }

        var scope = ParseScope(args);
        var projectDir = ParseProject(args, cwd);
        var dryRun = args.Contains("--dry-run");
        var yes = args.Contains("--yes");

        var selected = AllAdapters.Where(a => requestedAgents.Contains(a.Id)).ToList();
        var entries = new List<(IAgentAdapter Adapter, InstallContext Ctx, InstallPlan Plan)>();
        foreach (var adapter in selected)
        {
            if (!adapter.SupportsScope(scope))
            {
                stdout.WriteLine($"  skip   {adapter.DisplayName} does not support {scope} scope");
                continue;
            }

            var ctx = new InstallContext(scope, projectDir);
            var plan = uninstall ? new InstallPlan(adapter.Id, Array.Empty<InstallAction>()) : adapter.Plan(ctx);
            entries.Add((adapter, ctx, plan));
        }

        PrintPlan(stdout, entries, uninstall);

        if (!uninstall && dryRun)
        {
            stdout.WriteLine("[dry-run] no changes written.");
            return 0;
        }

        if (!yes && !await ConfirmAsync(stdin, stdout))
        {
            stdout.WriteLine("Aborted.");
            return 1;
        }

        foreach (var (adapter, ctx, plan) in entries)
        {
            if (uninstall)
            {
                adapter.Remove(ctx);
                stdout.WriteLine($"  removed {adapter.DisplayName} ({ctx.Scope})");
            }
            else
            {
                adapter.Apply(plan, ctx);
                stdout.WriteLine($"  applied {adapter.DisplayName} ({ctx.Scope})");
            }
        }

        return 0;
    }

    private static void PrintPlan(
        TextWriter stdout,
        IReadOnlyList<(IAgentAdapter Adapter, InstallContext Ctx, InstallPlan Plan)> entries,
        bool uninstall)
    {
        foreach (var (adapter, ctx, plan) in entries)
        {
            stdout.WriteLine($"{adapter.DisplayName} ({ctx.Scope}):");
            if (uninstall)
            {
                stdout.WriteLine("  remove all ccstash-authored entries");
                continue;
            }

            foreach (var action in plan.Actions)
            {
                var status = action.AlreadyPresent ? "already present" : "will write";
                stdout.WriteLine($"  [{status}] {action.Target} — {action.Description}");
            }
        }
    }

    private static async Task<bool> ConfirmAsync(TextReader stdin, TextWriter stdout)
    {
        stdout.Write("Apply? [y/N] ");
        var line = await stdin.ReadLineAsync();
        return string.Equals(line?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(line?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasActionableFlags(string[] args) =>
        args.Any(a => a is "--agent" or "--scope" or "--project" or "--yes" or "--dry-run");

    private static List<string> ParseAgents(string[] args)
    {
        var values = ArgValues(args, "--agent");
        if (values.Count == 0)
        {
            return AllAdapters.Select(a => a.Id).ToList();
        }

        var ids = values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        return ids.Contains("all", StringComparer.OrdinalIgnoreCase)
            ? AllAdapters.Select(a => a.Id).ToList()
            : ids.Select(id => id.ToLowerInvariant()).ToList();
    }

    private static InstallScope ParseScope(string[] args)
    {
        var value = ArgValue(args, "--scope");
        return string.Equals(value, "user", StringComparison.OrdinalIgnoreCase) ? InstallScope.User : InstallScope.Project;
    }

    private static string ParseProject(string[] args, string cwd) => ArgValue(args, "--project") ?? cwd;

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static List<string> ArgValues(string[] args, string flag)
    {
        var values = new List<string>();
        for (var i = Array.IndexOf(args, flag); i >= 0 && i + 1 < args.Length; i = Array.IndexOf(args, flag, i + 1))
        {
            values.Add(args[i + 1]);
        }

        return values;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/CCStash.Tests --filter "FullyQualifiedName~InstallVerbTests"`
Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

```bash
git add src/CCStash/CCStash.csproj src/CCStash/Verbs/InstallVerb.cs tests/CCStash.Tests/InstallVerbTests.cs
git commit -m "feat: add InstallVerb flag-driven install/uninstall path"
```

---

### Task 9: TUI path + `Program.cs` wiring + `init` alias + remove `InitVerb`

**Files:**
- Modify: `src/CCStash/Verbs/InstallVerb.cs`
- Modify: `src/CCStash/Program.cs`
- Delete: `src/CCStash/Verbs/InitVerb.cs`

No new automated tests: per the design spec ("TUI logic is kept thin... the testable logic lives in
adapters") and the writing-plans guidance that interactive console UI is verified by running it, not
unit-tested. This task is verified by a manual run (Step 4) plus the full existing suite staying green.

**Interfaces:**
- Consumes: `AnsiConsole` (Spectre.Console, added in Task 8), `InstallVerb.AllAdapters`,
  `InstallVerb.RunSelectedAsync`-equivalent flow (reuse by building the same args-shaped call, or
  extract a shared `ApplySelectionAsync` — see Step 1).

- [ ] **Step 1: Refactor `InstallVerb.RunAsync` to add the TUI branch**

```csharp
// src/CCStash/Verbs/InstallVerb.cs — replace RunAsync, add RunTuiAsync
using Spectre.Console;

// ... (keep existing usings, AllAdapters, RunUninstallAsync, RunSelectedAsync, PrintPlan, ConfirmAsync,
//      HasActionableFlags, ParseAgents, ParseScope, ParseProject, ArgValue, ArgValues unchanged)

    /// <summary>
    /// Run <c>install</c>: flag-driven when flags are present; otherwise a Spectre.Console TUI when a
    /// terminal is attached; otherwise usage + non-zero exit (never hangs waiting for a TUI with no TTY).
    /// </summary>
    public static Task<int> RunAsync(string[] args, string cwd, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (HasActionableFlags(args))
        {
            return RunSelectedAsync(args, cwd, stdin, stdout, stderr, uninstall: false);
        }

        if (AnsiConsole.Profile.Capabilities.Interactive)
        {
            return RunTuiAsync(cwd, stdout);
        }

        stderr.WriteLine(UsageLine);
        return Task.FromResult(1);
    }

    private static Task<int> RunTuiAsync(string cwd, TextWriter stdout)
    {
        var prompt = new MultiSelectionPrompt<string>()
            .Title("Select agents to install CCStash for:")
            .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to confirm)[/]")
            .UseConverter(id => AllAdapters.First(a => a.Id == id).DisplayName);
        foreach (var adapter in AllAdapters)
        {
            var detected = adapter.Detect(new InstallContext(InstallScope.Project, cwd));
            prompt.AddChoice(adapter.Id).Select(detected);
        }

        var selectedIds = AnsiConsole.Prompt(prompt);

        if (selectedIds.Count == 0)
        {
            stdout.WriteLine("No agents selected; nothing to do.");
            return Task.FromResult(0);
        }

        var scopeChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Install scope:")
                .AddChoices("project", "user"));

        var args = new List<string> { "--agent", string.Join(',', selectedIds), "--scope", scopeChoice, "--project", cwd };

        var entries = selectedIds
            .Select(id => AllAdapters.First(a => a.Id == id))
            .Where(a => a.SupportsScope(scopeChoice == "user" ? InstallScope.User : InstallScope.Project))
            .Select(a =>
            {
                var ctx = new InstallContext(scopeChoice == "user" ? InstallScope.User : InstallScope.Project, cwd);
                return (Adapter: a, Ctx: ctx, Plan: a.Plan(ctx));
            })
            .ToList();

        PrintPlan(stdout, entries, uninstall: false);

        if (!AnsiConsole.Confirm("Apply these changes?"))
        {
            stdout.WriteLine("Aborted.");
            return Task.FromResult(1);
        }

        foreach (var (adapter, ctx, plan) in entries)
        {
            adapter.Apply(plan, ctx);
            stdout.WriteLine($"  applied {adapter.DisplayName} ({ctx.Scope})");
        }

        return Task.FromResult(0);
    }
```

Pre-checking detected agents uses `MultiSelectionPrompt<T>.AddChoice(T)` returning a
`MultiSelectionItem<T>` with a `.Select(bool)` method — each choice is added and marked selected
individually (there is no bulk "select these ids" overload), which is why the loop above calls
`AddChoice` per adapter rather than a single `.AddChoices(...)`.

- [ ] **Step 2: Wire `Program.cs`**

```csharp
// src/CCStash/Program.cs — full replacement
using CCStash.Verbs;

// Minimal verb dispatch. (The plan specifies System.CommandLine; manual dispatch is used here
// to avoid a pre-release dependency — the verb contract is identical.)
if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: ccstash <stash|pointer|status|search|gc|mcp|install|uninstall|init> [args]");
    return 1;
}

var cwd = Environment.CurrentDirectory;

return args[0] switch
{
    "stash" => await StashVerb.RunAsync(args[1..], Console.In),
    "pointer" => await PointerVerb.RunAsync(Console.In, Console.Out),
    "status" => await StatusVerb.RunAsync(cwd, Console.Out),
    "search" => await SearchVerb.RunAsync(cwd, args.Length > 1 ? string.Join(' ', args[1..]) : string.Empty, Console.Out),
    "gc" => await GcVerb.RunAsync(args[1..], Console.Out),
    "mcp" => await McpVerb.RunAsync(ResolveProject(args, cwd)),
    "install" => await InstallVerb.RunAsync(args[1..], cwd, Console.In, Console.Out, Console.Error),
    "uninstall" => await InstallVerb.RunUninstallAsync(args[1..], cwd, Console.In, Console.Out, Console.Error),
    "init" => await InstallVerb.RunAsync(["--agent", "claude", "--scope", "project", "--yes"], cwd, Console.In, Console.Out, Console.Error),
    _ => Unknown(args[0]),
};

static int Unknown(string verb)
{
    Console.Error.WriteLine($"Unknown verb: {verb}");
    return 1;
}

// Resolve the project directory for the MCP server, which Claude Code may launch from any cwd.
// Precedence: --project arg (baked into project .mcp.json by `install`) > CLAUDE_PROJECT_DIR (set by
// Claude Code in the server's env — the robust choice for a user-scoped server shared across
// projects) > CCSTASH_PROJECT > cwd.
static string ResolveProject(string[] args, string cwd)
{
    var i = Array.IndexOf(args, "--project");
    if (i >= 0 && i + 1 < args.Length)
    {
        return args[i + 1];
    }

    return Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR")
        ?? Environment.GetEnvironmentVariable("CCSTASH_PROJECT")
        ?? cwd;
}
```

- [ ] **Step 3: Delete `InitVerb.cs`**

```bash
git rm src/CCStash/Verbs/InitVerb.cs
```

- [ ] **Step 4: Verify — build, full test suite, and a manual smoke run**

Run: `dotnet build CCStash.slnx`
Expected: Build succeeded (confirms `InitVerb` had no other references, and the TUI code compiles).

Run: `dotnet test CCStash.slnx`
Expected: All tests PASS, including the full `InstallVerbTests`/`StashVerbArgsTests`/adapter suites from earlier tasks.

Run (manual, from a real terminal, in a scratch directory):
```bash
mkdir /tmp/ccstash-install-smoke && cd /tmp/ccstash-install-smoke
dotnet run --project <repo>/src/CCStash -- install --agent all --scope project --project . --dry-run
dotnet run --project <repo>/src/CCStash -- install --agent all --scope project --project . --yes
dotnet run --project <repo>/src/CCStash -- uninstall --agent all --scope project --project . --yes
```
Expected: dry-run prints the plan and writes nothing; the second command writes
`.claude/settings.json`, `.mcp.json`, `appsettings.json`, `.codesharp/skills/ccstash.md`; the third
removes the ccstash-authored entries from each.

- [ ] **Step 5: Commit**

```bash
git add src/CCStash/Verbs/InstallVerb.cs src/CCStash/Program.cs
git commit -m "feat: add install TUI, wire install/uninstall/init in Program.cs, remove InitVerb"
```

---

### Task 10: Update docs

**Files:**
- Modify: `docs/INSTALL.md`
- Modify: `README.md` (only if it documents `init` — check first)

- [ ] **Step 1: Update `docs/INSTALL.md` §3 "Wire it into your project"**

```markdown
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
```

- [ ] **Step 2: Check `README.md` for `init` references and update them to match**

Run: `grep -n "ccstash init\|dnx.*init" README.md`

If matches are found, update each to the `install` form shown in Step 1's snippet (mirroring the
exact wording change). If no matches, no change needed.

- [ ] **Step 3: Commit**

```bash
git add docs/INSTALL.md README.md
git commit -m "docs: document install/uninstall, keep init as documented shorthand"
```

---

## Self-Review

**Spec coverage:**
- Command surface (TUI default, flag-driven, `--agent/--scope/--project/--yes/--dry-run`, `uninstall`,
  `init` alias, no-TTY+no-flags → usage) → Tasks 8, 9.
- `IAgentAdapter` verbatim interface + records → Task 3.
- `JsonConfigEditor` extraction → Task 2.
- `ClaudeCodeAdapter` (refactor of `InitVerb`, project + user scope, `Detect`/`Remove`) → Task 4.
- `CodeSharpAdapter` (project-scope-only, appsettings MCP entry, hook-skill with placeholders) → Task 5.
- `stash --transcript` (and `--project`) → Task 7.
- Global constraint "generated invocations use `dotnet dnx CCStash -- <verb>`" → resolved explicitly
  in Global Constraints as agent-specific (Claude Code keeps `dnx -y CCStash --` for idempotency
  compatibility with existing installs; CodeSharp uses `dotnet dnx CCStash --` per spec verbatim).
- Spectre.Console added to CLI project only → Task 8 Step 1.
- Error handling (best-effort per adapter, malformed JSON tolerated, dry-run/TUI never write, no-TTY
  never hangs) → `JsonConfigEditor.LoadOrEmpty`'s tolerant parse (Task 2) + `InstallVerb`'s dry-run
  short-circuit and `AnsiConsole.Profile.Capabilities.Interactive` gate (Tasks 8–9).
- Testing plan (`JsonConfigEditor` unit tests, adapter Plan/Apply/Remove tests, flag-path smoke test,
  thin/untested TUI) → Tasks 2, 4, 5, 8, 9.
- Out of scope (CodeSharp user-scope, transcript-emit PR, CCStash CodeSharp parser, other adapters) →
  correctly not implemented anywhere in this plan.

**Placeholder scan:** No TBD/"add appropriate"/"similar to Task N" phrasing; every step has literal,
single, compiling code (Task 9's TUI step was corrected during pre-flight review to remove a
non-compiling draft block).

**Type consistency:** `IAgentAdapter`/`InstallContext`/`InstallAction`/`InstallPlan` (Task 3) are used
identically by `ClaudeCodeAdapter` (Task 4), `CodeSharpAdapter` (Task 5), and `InstallVerb` (Tasks
8–9). `ClaudeCodeAdapter.StashCommand`/`PointerCommand` (Task 4) are the exact constants Task 8's
tests assert against via `ClaudeCodeAdapter.StashCommand`. `CodeSharpAdapter.SkillFileName` (Task 5)
is the exact constant Task 8's tests use. `StashVerb.RunAsync(string[], TextReader)` (Task 7) matches
the call site added in `Program.cs` in the same task, and stays unchanged through Task 9.
