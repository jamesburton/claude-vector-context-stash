# CCStash Vertical Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the end-to-end CCStash loop — `PreCompact` distills the transcript into an embedded store, `SessionStart:compact` injects a minimal pointer, and a `retrieve_context` MCP tool returns relevant chunks on demand — proven on a real Claude Code session.

**Architecture:** A single .NET tool (`CCStash`, run via `dnx CCStash -- <verb>`) over a small set of `CCStash.*` libraries. Core logic depends only on interfaces (`IVectorStore`, `IEmbedder`), so the risky native integrations (sqlite-vec, ONNX) are isolated and validated independently while everything else is built and tested against in-memory fakes.

**Tech Stack:** .NET 10, C#, `System.CommandLine`, `Microsoft.Data.Sqlite` + sqlite-vec, `Microsoft.ML.OnnxRuntime` + `FastBertTokenizer`, `ModelContextProtocol` (MCP C# SDK), xUnit.

## Global Constraints

- **Target framework:** `net10.0` for every project.
- **Distribution:** the `CCStash` project is a .NET tool — `<PackAsTool>true</PackAsTool>`, `<ToolCommandName>ccstash</ToolCommandName>`. Hooks invoke it via `dnx -y CCStash -- <verb>` (the `-y` makes first-run download non-interactive).
- **Naming:** tool/package id `CCStash`; libraries `CCStash.Core`, `CCStash.Stores.Sqlite`, `CCStash.Embeddings.Onnx`, `CCStash.Mcp`. (`CCStash.Stores.Qdrant` is **deferred** — do not create it in this slice.)
- **The spine — pointer not blob:** post-compaction injection is a short pointer (a few hundred chars), never a context dump. Never re-inject bulk context.
- **Hook safety:** the `stash` and `pointer` verbs MUST always exit 0, never throw to the caller, never block compaction. All failures are caught and logged to the log file.
- **Slice scope:** sqlite-vec store only, local ONNX embeddings only, per-turn chunking, truncated `tool_result`s. No Qdrant, no API embeddings, no hybrid/FTS5 search, no `/clear` handling.
- **Conventions:** xUnit tests; StyleCop SA rules; XML doc comments on public APIs.

---

## File Structure

```
CCStash.sln
Directory.Build.props                      # net10.0, nullable, StyleCop, langversion
src/
  CCStash.Core/
    Transcript/TranscriptModels.cs         # ContentBlock, TranscriptTurn, BlockKind
    Transcript/ITranscriptParser.cs
    Transcript/TranscriptParser.cs
    Distillation/DistillModels.cs          # DistillOptions, DistilledTurn
    Distillation/IDistiller.cs
    Distillation/Distiller.cs
    Chunking/ChunkModels.cs                # Chunk, ChunkOptions
    Chunking/IChunker.cs
    Chunking/Chunker.cs
    Embeddings/IEmbedder.cs
    Embeddings/FakeEmbedder.cs             # deterministic, for tests + offline
    Storage/StorageModels.cs               # StoredChunk, SearchHit
    Storage/IVectorStore.cs
    Storage/InMemoryVectorStore.cs         # fake, cosine brute force
    Config/CCStashConfig.cs
    Config/CCStashPaths.cs
    Hooks/HookInput.cs                     # PreCompact / SessionStart stdin JSON
    StashService.cs                        # orchestrates parse->distill->chunk->embed->store
    RetrievalService.cs
  CCStash.Stores.Sqlite/
    SqliteVectorStore.cs
    SqliteVecLoader.cs
  CCStash.Embeddings.Onnx/
    OnnxEmbedder.cs
  CCStash.Mcp/
    RetrieveContextTools.cs                # [McpServerToolType]
  CCStash/
    CCStash.csproj                         # the dnx tool
    Program.cs                             # root command + verb wiring
    Verbs/StashVerb.cs
    Verbs/PointerVerb.cs
    Verbs/McpVerb.cs
    Verbs/InitVerb.cs
    Verbs/StatusVerb.cs
    Verbs/SearchVerb.cs
    Composition.cs                         # builds services from config
tests/
  CCStash.Core.Tests/
  CCStash.Stores.Sqlite.Tests/
  CCStash.Embeddings.Onnx.Tests/
  fixtures/sample-transcript.jsonl         # captured real lines
```

---

### Task 1: Solution scaffold

**Files:**
- Create: `CCStash.sln`, `Directory.Build.props`
- Create: `src/CCStash.Core/CCStash.Core.csproj`
- Create: `tests/CCStash.Core.Tests/CCStash.Core.Tests.csproj`, `tests/CCStash.Core.Tests/SmokeTest.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: a buildable solution with a green test run.

- [ ] **Step 1: Create the solution and Directory.Build.props**

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="StyleCop.Analyzers" Version="1.2.0-beta.556" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the core library and test projects**

Run:
```bash
cd c:/Development/claude-vector-context-stash
dotnet new sln -n CCStash
dotnet new classlib -o src/CCStash.Core -n CCStash.Core
dotnet new xunit -o tests/CCStash.Core.Tests -n CCStash.Core.Tests
dotnet sln add src/CCStash.Core tests/CCStash.Core.Tests
dotnet add tests/CCStash.Core.Tests reference src/CCStash.Core
rm src/CCStash.Core/Class1.cs tests/CCStash.Core.Tests/UnitTest1.cs
```

- [ ] **Step 3: Add a smoke test**

`tests/CCStash.Core.Tests/SmokeTest.cs`:
```csharp
namespace CCStash.Core.Tests;

public class SmokeTest
{
    [Fact]
    public void Solution_builds_and_tests_run() => Assert.True(true);
}
```

- [ ] **Step 4: Build and test**

Run: `dotnet test`
Expected: build succeeds, 1 test passes.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: scaffold CCStash solution"
```

---

### Task 2: Vector store interface + in-memory fake

**Files:**
- Create: `src/CCStash.Core/Storage/StorageModels.cs`, `src/CCStash.Core/Storage/IVectorStore.cs`, `src/CCStash.Core/Storage/InMemoryVectorStore.cs`
- Test: `tests/CCStash.Core.Tests/InMemoryVectorStoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `record StoredChunk(string Id, string Project, string Session, int TurnIndex, string Role, string Type, DateTimeOffset? Timestamp, string Text, float[] Embedding)`
  - `record SearchHit(StoredChunk Chunk, float Score)`
  - `IVectorStore` with: `Task InitializeAsync(int dimension, string embeddingModel, CancellationToken ct = default)`, `Task UpsertAsync(IReadOnlyList<StoredChunk> chunks, CancellationToken ct = default)`, `Task<IReadOnlyList<SearchHit>> SearchAsync(float[] query, int limit, string? session, CancellationToken ct = default)`, `Task<int> CountAsync(string? session, CancellationToken ct = default)`, `Task<int> GetHighWaterMarkAsync(string session, CancellationToken ct = default)`, `Task<string?> GetLatestSessionAsync(CancellationToken ct = default)`. Extends `IDisposable`.

- [ ] **Step 1: Write the failing tests**

`tests/CCStash.Core.Tests/InMemoryVectorStoreTests.cs`:
```csharp
using CCStash.Core.Storage;

namespace CCStash.Core.Tests;

public class InMemoryVectorStoreTests
{
    private static StoredChunk Chunk(string id, int turn, string session, float[] v) =>
        new(id, "proj", session, turn, "user", "text", DateTimeOffset.UnixEpoch, $"text {id}", v);

    [Fact]
    public async Task Search_ranks_by_cosine_similarity()
    {
        using var store = new InMemoryVectorStore();
        await store.InitializeAsync(2, "fake");
        await store.UpsertAsync([
            Chunk("a", 0, "s1", [1f, 0f]),
            Chunk("b", 1, "s1", [0f, 1f]),
        ]);

        var hits = await store.SearchAsync([1f, 0f], limit: 1, session: null);

        Assert.Single(hits);
        Assert.Equal("a", hits[0].Chunk.Id);
    }

    [Fact]
    public async Task Search_filters_by_session_when_provided()
    {
        using var store = new InMemoryVectorStore();
        await store.InitializeAsync(2, "fake");
        await store.UpsertAsync([
            Chunk("a", 0, "s1", [1f, 0f]),
            Chunk("b", 0, "s2", [1f, 0f]),
        ]);

        var hits = await store.SearchAsync([1f, 0f], limit: 5, session: "s2");

        Assert.Single(hits);
        Assert.Equal("s2", hits[0].Chunk.Session);
    }

    [Fact]
    public async Task Upsert_replaces_by_id_and_highwater_tracks_max_turn()
    {
        using var store = new InMemoryVectorStore();
        await store.InitializeAsync(2, "fake");
        await store.UpsertAsync([Chunk("a", 3, "s1", [1f, 0f])]);
        await store.UpsertAsync([Chunk("a", 3, "s1", [0f, 1f])]); // same id

        Assert.Equal(1, await store.CountAsync("s1"));
        Assert.Equal(3, await store.GetHighWaterMarkAsync("s1"));
        Assert.Equal(-1, await store.GetHighWaterMarkAsync("missing"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter InMemoryVectorStoreTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement the models, interface, and fake**

`src/CCStash.Core/Storage/StorageModels.cs`:
```csharp
namespace CCStash.Core.Storage;

/// <summary>A distilled, embedded unit of conversation stored for later retrieval.</summary>
public sealed record StoredChunk(
    string Id,
    string Project,
    string Session,
    int TurnIndex,
    string Role,
    string Type,
    DateTimeOffset? Timestamp,
    string Text,
    float[] Embedding);

/// <summary>A search result: a stored chunk plus its similarity score (higher is closer).</summary>
public sealed record SearchHit(StoredChunk Chunk, float Score);
```

`src/CCStash.Core/Storage/IVectorStore.cs`:
```csharp
namespace CCStash.Core.Storage;

/// <summary>An embedded vector store for distilled conversation chunks.</summary>
public interface IVectorStore : IDisposable
{
    /// <summary>Ensure the store exists and matches the given embedding dimension/model.</summary>
    Task InitializeAsync(int dimension, string embeddingModel, CancellationToken ct = default);

    /// <summary>Insert or replace chunks by <see cref="StoredChunk.Id"/>.</summary>
    Task UpsertAsync(IReadOnlyList<StoredChunk> chunks, CancellationToken ct = default);

    /// <summary>Return the nearest chunks to <paramref name="query"/>, optionally limited to one session.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(float[] query, int limit, string? session, CancellationToken ct = default);

    /// <summary>Count chunks, optionally for a single session.</summary>
    Task<int> CountAsync(string? session, CancellationToken ct = default);

    /// <summary>Highest stored turn index for a session, or -1 if none.</summary>
    Task<int> GetHighWaterMarkAsync(string session, CancellationToken ct = default);

    /// <summary>The session with the most recently stored chunk, or null if empty.</summary>
    Task<string?> GetLatestSessionAsync(CancellationToken ct = default);
}
```

`src/CCStash.Core/Storage/InMemoryVectorStore.cs`:
```csharp
namespace CCStash.Core.Storage;

/// <summary>In-memory cosine-similarity store used for tests and offline runs.</summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly Dictionary<string, StoredChunk> _chunks = new();

    /// <inheritdoc/>
    public Task InitializeAsync(int dimension, string embeddingModel, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task UpsertAsync(IReadOnlyList<StoredChunk> chunks, CancellationToken ct = default)
    {
        foreach (var c in chunks)
        {
            _chunks[c.Id] = c;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SearchHit>> SearchAsync(float[] query, int limit, string? session, CancellationToken ct = default)
    {
        IReadOnlyList<SearchHit> hits = _chunks.Values
            .Where(c => session is null || c.Session == session)
            .Select(c => new SearchHit(c, Cosine(query, c.Embedding)))
            .OrderByDescending(h => h.Score)
            .Take(limit)
            .ToList();

        return Task.FromResult(hits);
    }

    /// <inheritdoc/>
    public Task<int> CountAsync(string? session, CancellationToken ct = default)
        => Task.FromResult(_chunks.Values.Count(c => session is null || c.Session == session));

    /// <inheritdoc/>
    public Task<int> GetHighWaterMarkAsync(string session, CancellationToken ct = default)
    {
        var forSession = _chunks.Values.Where(c => c.Session == session).ToList();
        return Task.FromResult(forSession.Count == 0 ? -1 : forSession.Max(c => c.TurnIndex));
    }

    /// <inheritdoc/>
    public Task<string?> GetLatestSessionAsync(CancellationToken ct = default)
        => Task.FromResult(_chunks.Values
            .OrderByDescending(c => c.Timestamp ?? DateTimeOffset.MinValue)
            .Select(c => c.Session)
            .FirstOrDefault());

    /// <summary>No-op; nothing to release.</summary>
    public void Dispose()
    {
    }

    private static float Cosine(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter InMemoryVectorStoreTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: vector store interface + in-memory fake"
```

---

### Task 3: sqlite-vec store (RISK 1 — validate early)

**Files:**
- Create: `src/CCStash.Stores.Sqlite/CCStash.Stores.Sqlite.csproj`, `src/CCStash.Stores.Sqlite/SqliteVecLoader.cs`, `src/CCStash.Stores.Sqlite/SqliteVectorStore.cs`
- Create: `tests/CCStash.Stores.Sqlite.Tests/CCStash.Stores.Sqlite.Tests.csproj`, `tests/CCStash.Stores.Sqlite.Tests/SqliteVectorStoreTests.cs`

**Interfaces:**
- Consumes: `IVectorStore`, `StoredChunk`, `SearchHit` (Task 2).
- Produces: `SqliteVectorStore(string dbPath) : IVectorStore`.

> **RISK NOTE (from spec §8.1):** sqlite-vec ships a loadable native extension (`vec0`). This task validates loading it under `Microsoft.Data.Sqlite` on Windows **first**. If the extension cannot be loaded in your environment, the documented contingency is to implement `IVectorStore` with managed cosine over vectors stored as `BLOB`s in the same SQLite file (single-file, no daemon — still satisfies the architecture; the `IVectorStore` abstraction is exactly what makes this swap safe). Either way the downstream tasks are unblocked because they target `IVectorStore`.

- [ ] **Step 1: Create the project and acquire the native extension**

Run:
```bash
dotnet new classlib -o src/CCStash.Stores.Sqlite -n CCStash.Stores.Sqlite
dotnet sln add src/CCStash.Stores.Sqlite
dotnet add src/CCStash.Stores.Sqlite reference src/CCStash.Core
dotnet add src/CCStash.Stores.Sqlite package Microsoft.Data.Sqlite
rm src/CCStash.Stores.Sqlite/Class1.cs
dotnet new xunit -o tests/CCStash.Stores.Sqlite.Tests -n CCStash.Stores.Sqlite.Tests
dotnet sln add tests/CCStash.Stores.Sqlite.Tests
dotnet add tests/CCStash.Stores.Sqlite.Tests reference src/CCStash.Stores.Sqlite
rm tests/CCStash.Stores.Sqlite.Tests/UnitTest1.cs
```

Download the sqlite-vec loadable extension for `win-x64` from the sqlite-vec GitHub releases (`vec0.dll`) and place it at `src/CCStash.Stores.Sqlite/runtimes/win-x64/native/vec0.dll`. Add to the csproj so it copies to output:
```xml
<ItemGroup>
  <None Include="runtimes/**/*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing test**

`tests/CCStash.Stores.Sqlite.Tests/SqliteVectorStoreTests.cs`:
```csharp
using CCStash.Core.Storage;
using CCStash.Stores.Sqlite;

namespace CCStash.Stores.Sqlite.Tests;

public class SqliteVectorStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ccstash-{Guid.NewGuid():N}.db");

    private static StoredChunk Chunk(string id, int turn, string session, float[] v) =>
        new(id, "proj", session, turn, "user", "text", DateTimeOffset.UnixEpoch, $"text {id}", v);

    [Fact]
    public async Task Roundtrip_search_and_highwater()
    {
        using (var store = new SqliteVectorStore(_dbPath))
        {
            await store.InitializeAsync(2, "fake");
            await store.UpsertAsync([
                Chunk("a", 0, "s1", [1f, 0f]),
                Chunk("b", 5, "s1", [0f, 1f]),
            ]);

            var hits = await store.SearchAsync([1f, 0f], limit: 1, session: "s1");
            Assert.Equal("a", hits[0].Chunk.Id);
            Assert.Equal(5, await store.GetHighWaterMarkAsync("s1"));
            Assert.Equal("s1", await store.GetLatestSessionAsync());
        }

        // Reopen: data persists to the single file.
        using var reopened = new SqliteVectorStore(_dbPath);
        await reopened.InitializeAsync(2, "fake");
        Assert.Equal(2, await reopened.CountAsync("s1"));
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test --filter SqliteVectorStoreTests`
Expected: FAIL — `SqliteVectorStore` not defined.

- [ ] **Step 4: Implement the extension loader and store**

`src/CCStash.Stores.Sqlite/SqliteVecLoader.cs`:
```csharp
using Microsoft.Data.Sqlite;

namespace CCStash.Stores.Sqlite;

/// <summary>Loads the sqlite-vec (<c>vec0</c>) loadable extension into a connection.</summary>
internal static class SqliteVecLoader
{
    /// <summary>Enable and load the bundled <c>vec0</c> extension for the current platform.</summary>
    public static void Load(SqliteConnection connection)
    {
        var rid = OperatingSystem.IsWindows() ? "win-x64"
            : OperatingSystem.IsMacOS() ? "osx-arm64"
            : "linux-x64";
        var lib = OperatingSystem.IsWindows() ? "vec0.dll"
            : OperatingSystem.IsMacOS() ? "vec0.dylib"
            : "vec0.so";
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", lib);

        connection.EnableExtensions(true);
        connection.LoadExtension(path);
    }
}
```

`src/CCStash.Stores.Sqlite/SqliteVectorStore.cs`:
```csharp
using System.Globalization;
using CCStash.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CCStash.Stores.Sqlite;

/// <summary>A single-file sqlite-vec backed implementation of <see cref="IVectorStore"/>.</summary>
public sealed class SqliteVectorStore(string dbPath) : IVectorStore
{
    private SqliteConnection? _connection;
    private int _dimension;

    private SqliteConnection Conn => _connection ?? throw new InvalidOperationException("InitializeAsync not called.");

    /// <inheritdoc/>
    public async Task InitializeAsync(int dimension, string embeddingModel, CancellationToken ct = default)
    {
        _dimension = dimension;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connection = new SqliteConnection($"Data Source={dbPath}");
        await _connection.OpenAsync(ct);
        SqliteVecLoader.Load(_connection);

        await Exec(
            """
            CREATE TABLE IF NOT EXISTS chunks(
                id TEXT PRIMARY KEY, project TEXT, session TEXT, turn_index INTEGER,
                role TEXT, type TEXT, ts TEXT, text TEXT);
            """, ct);
        await Exec(
            $"CREATE VIRTUAL TABLE IF NOT EXISTS vec_chunks USING vec0(id TEXT PRIMARY KEY, embedding float[{dimension}]);",
            ct);
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(IReadOnlyList<StoredChunk> chunks, CancellationToken ct = default)
    {
        await using var tx = await Conn.BeginTransactionAsync(ct);
        foreach (var c in chunks)
        {
            await using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO chunks(id,project,session,turn_index,role,type,ts,text)
                VALUES($id,$p,$s,$t,$r,$ty,$ts,$txt)
                ON CONFLICT(id) DO UPDATE SET project=$p,session=$s,turn_index=$t,role=$r,type=$ty,ts=$ts,text=$txt;
                DELETE FROM vec_chunks WHERE id=$id;
                INSERT INTO vec_chunks(id,embedding) VALUES($id,$emb);
                """;
            cmd.Parameters.AddWithValue("$id", c.Id);
            cmd.Parameters.AddWithValue("$p", c.Project);
            cmd.Parameters.AddWithValue("$s", c.Session);
            cmd.Parameters.AddWithValue("$t", c.TurnIndex);
            cmd.Parameters.AddWithValue("$r", c.Role);
            cmd.Parameters.AddWithValue("$ty", c.Type);
            cmd.Parameters.AddWithValue("$ts", c.Timestamp?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            cmd.Parameters.AddWithValue("$txt", c.Text);
            cmd.Parameters.AddWithValue("$emb", ToBlob(c.Embedding));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(float[] query, int limit, string? session, CancellationToken ct = default)
    {
        // Over-fetch from the vec index, then join + optional session filter in SQL.
        await using var cmd = Conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT c.id,c.project,c.session,c.turn_index,c.role,c.type,c.ts,c.text,v.distance
            FROM vec_chunks v JOIN chunks c ON c.id = v.id
            WHERE v.embedding MATCH $q AND k = $k
              AND ($session IS NULL OR c.session = $session)
            ORDER BY v.distance;
            """;
        cmd.Parameters.AddWithValue("$q", ToBlob(query));
        cmd.Parameters.AddWithValue("$k", Math.Max(limit, limit * 4));
        cmd.Parameters.AddWithValue("$session", (object?)session ?? DBNull.Value);

        var hits = new List<SearchHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct) && hits.Count < limit)
        {
            var distance = (float)reader.GetDouble(8);
            hits.Add(new SearchHit(ReadChunk(reader), 1f - distance)); // score: higher is closer
        }

        return hits;
    }

    /// <inheritdoc/>
    public async Task<int> CountAsync(string? session, CancellationToken ct = default)
    {
        await using var cmd = Conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE ($s IS NULL OR session=$s);";
        cmd.Parameters.AddWithValue("$s", (object?)session ?? DBNull.Value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public async Task<int> GetHighWaterMarkAsync(string session, CancellationToken ct = default)
    {
        await using var cmd = Conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(turn_index), -1) FROM chunks WHERE session=$s;";
        cmd.Parameters.AddWithValue("$s", session);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public async Task<string?> GetLatestSessionAsync(CancellationToken ct = default)
    {
        await using var cmd = Conn.CreateCommand();
        cmd.CommandText = "SELECT session FROM chunks ORDER BY ts DESC LIMIT 1;";
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Close the underlying connection.</summary>
    public void Dispose() => _connection?.Dispose();

    private static byte[] ToBlob(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static StoredChunk ReadChunk(SqliteDataReader r)
    {
        var ts = r.GetString(6);
        return new StoredChunk(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
            r.GetString(4), r.GetString(5),
            string.IsNullOrEmpty(ts) ? null : DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture),
            r.GetString(7), []);
    }

    private async Task Exec(string sql, CancellationToken ct)
    {
        await using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter SqliteVectorStoreTests`
Expected: PASS. If `LoadExtension` throws, apply the RISK NOTE contingency (managed cosine over BLOBs) and re-run.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: sqlite-vec vector store"
```

---

### Task 4: Embedder interface + fake + ONNX embedder (RISK 2 — validate early)

**Files:**
- Create: `src/CCStash.Core/Embeddings/IEmbedder.cs`, `src/CCStash.Core/Embeddings/FakeEmbedder.cs`
- Create: `src/CCStash.Embeddings.Onnx/CCStash.Embeddings.Onnx.csproj`, `src/CCStash.Embeddings.Onnx/OnnxEmbedder.cs`
- Test: `tests/CCStash.Core.Tests/FakeEmbedderTests.cs`, `tests/CCStash.Embeddings.Onnx.Tests/OnnxEmbedderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `IEmbedder` with `int Dimension { get; }`, `string ModelId { get; }`, `Task<float[]> EmbedAsync(string text, CancellationToken ct = default)`, `Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)`.
  - `FakeEmbedder(int dimension = 8)` — deterministic hashing embedder.
  - `OnnxEmbedder(string modelPath, string vocabPath)` — local all-MiniLM-L6-v2, `Dimension = 384`.

- [ ] **Step 1: Write the failing test for the interface + fake**

`tests/CCStash.Core.Tests/FakeEmbedderTests.cs`:
```csharp
using CCStash.Core.Embeddings;

namespace CCStash.Core.Tests;

public class FakeEmbedderTests
{
    [Fact]
    public async Task Embed_is_deterministic_and_normalized()
    {
        var e = new FakeEmbedder(8);
        var a = await e.EmbedAsync("hello");
        var b = await e.EmbedAsync("hello");

        Assert.Equal(8, e.Dimension);
        Assert.Equal(a, b);
        Assert.Equal(1f, MathF.Sqrt(a.Sum(x => x * x)), 3);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FakeEmbedderTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement interface and fake**

`src/CCStash.Core/Embeddings/IEmbedder.cs`:
```csharp
namespace CCStash.Core.Embeddings;

/// <summary>Produces dense vector embeddings for text.</summary>
public interface IEmbedder
{
    /// <summary>Embedding vector length.</summary>
    int Dimension { get; }

    /// <summary>Identifier of the embedding model (recorded in the store).</summary>
    string ModelId { get; }

    /// <summary>Embed a single string.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>Embed many strings.</summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
```

`src/CCStash.Core/Embeddings/FakeEmbedder.cs`:
```csharp
namespace CCStash.Core.Embeddings;

/// <summary>Deterministic hashing embedder for tests and offline use.</summary>
public sealed class FakeEmbedder(int dimension = 8) : IEmbedder
{
    /// <inheritdoc/>
    public int Dimension { get; } = dimension;

    /// <inheritdoc/>
    public string ModelId => $"fake-{Dimension}";

    /// <inheritdoc/>
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var v = new float[Dimension];
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            v[(uint)token.GetHashCode(StringComparison.Ordinal) % Dimension] += 1f;
        }

        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0)
        {
            for (var i = 0; i < v.Length; i++)
            {
                v[i] /= norm;
            }
        }

        return Task.FromResult(v);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>(texts.Count);
        foreach (var t in texts)
        {
            result.Add(await EmbedAsync(t, ct));
        }

        return result;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FakeEmbedderTests`
Expected: PASS.

- [ ] **Step 5: Create the ONNX project and a real-model smoke test**

Run:
```bash
dotnet new classlib -o src/CCStash.Embeddings.Onnx -n CCStash.Embeddings.Onnx
dotnet sln add src/CCStash.Embeddings.Onnx
dotnet add src/CCStash.Embeddings.Onnx reference src/CCStash.Core
dotnet add src/CCStash.Embeddings.Onnx package Microsoft.ML.OnnxRuntime
dotnet add src/CCStash.Embeddings.Onnx package FastBertTokenizer
rm src/CCStash.Embeddings.Onnx/Class1.cs
dotnet new xunit -o tests/CCStash.Embeddings.Onnx.Tests -n CCStash.Embeddings.Onnx.Tests
dotnet sln add tests/CCStash.Embeddings.Onnx.Tests
dotnet add tests/CCStash.Embeddings.Onnx.Tests reference src/CCStash.Embeddings.Onnx
rm tests/CCStash.Embeddings.Onnx.Tests/UnitTest1.cs
```

Download `all-MiniLM-L6-v2` ONNX (`model.onnx`) and `vocab.txt` (from the Hugging Face repo `sentence-transformers/all-MiniLM-L6-v2`, ONNX export) into a local `models/all-MiniLM-L6-v2/` folder used by the test via an env var `CCSTASH_MODEL_DIR`.

`tests/CCStash.Embeddings.Onnx.Tests/OnnxEmbedderTests.cs`:
```csharp
using CCStash.Embeddings.Onnx;

namespace CCStash.Embeddings.Onnx.Tests;

public class OnnxEmbedderTests
{
    [Fact]
    public async Task Similar_sentences_score_higher_than_unrelated()
    {
        var dir = Environment.GetEnvironmentVariable("CCSTASH_MODEL_DIR");
        Skip.If(dir is null, "Set CCSTASH_MODEL_DIR to run the ONNX smoke test.");

        using var e = new OnnxEmbedder(Path.Combine(dir!, "model.onnx"), Path.Combine(dir!, "vocab.txt"));
        Assert.Equal(384, e.Dimension);

        var cat = await e.EmbedAsync("the cat sat on the mat");
        var kitten = await e.EmbedAsync("a kitten rested on a rug");
        var finance = await e.EmbedAsync("quarterly tax accounting report");

        Assert.True(Dot(cat, kitten) > Dot(cat, finance));
    }

    private static float Dot(float[] a, float[] b)
    {
        float d = 0;
        for (var i = 0; i < a.Length; i++)
        {
            d += a[i] * b[i];
        }

        return d;
    }
}
```
(Use the `Xunit.SkippableFact` package for `Skip.If`, or replace with a `[Fact(Skip=...)]` guard if the model is absent.)

- [ ] **Step 6: Implement the ONNX embedder**

`src/CCStash.Embeddings.Onnx/OnnxEmbedder.cs`:
```csharp
using CCStash.Core.Embeddings;
using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CCStash.Embeddings.Onnx;

/// <summary>Local sentence-embedding via all-MiniLM-L6-v2 ONNX with mean pooling + L2 normalize.</summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private const int MaxTokens = 256;
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;

    /// <summary>Load the model and vocabulary from disk.</summary>
    public OnnxEmbedder(string modelPath, string vocabPath)
    {
        _session = new InferenceSession(modelPath);
        _tokenizer = new BertTokenizer();
        _tokenizer.LoadVocabulary(vocabPath, convertInputToLowercase: true);
    }

    /// <inheritdoc/>
    public int Dimension => 384;

    /// <inheritdoc/>
    public string ModelId => "all-MiniLM-L6-v2";

    /// <inheritdoc/>
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var (ids, mask, types) = _tokenizer.Encode(text, MaxTokens);
        var len = ids.Length;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids.ToArray(), [1, len])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask.ToArray(), [1, len])),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(types.ToArray(), [1, len])),
        };

        using var results = _session.Run(inputs);
        var tokenEmbeddings = results.First().AsTensor<float>(); // [1, len, 384]
        return Task.FromResult(MeanPoolAndNormalize(tokenEmbeddings, mask.ToArray(), len));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>(texts.Count);
        foreach (var t in texts)
        {
            result.Add(await EmbedAsync(t, ct));
        }

        return result;
    }

    /// <summary>Release the inference session.</summary>
    public void Dispose() => _session.Dispose();

    private float[] MeanPoolAndNormalize(Tensor<float> tokens, long[] mask, int len)
    {
        var pooled = new float[Dimension];
        float count = 0;
        for (var t = 0; t < len; t++)
        {
            if (mask[t] == 0)
            {
                continue;
            }

            count++;
            for (var d = 0; d < Dimension; d++)
            {
                pooled[d] += tokens[0, t, d];
            }
        }

        if (count > 0)
        {
            for (var d = 0; d < Dimension; d++)
            {
                pooled[d] /= count;
            }
        }

        var norm = MathF.Sqrt(pooled.Sum(x => x * x));
        if (norm > 0)
        {
            for (var d = 0; d < Dimension; d++)
            {
                pooled[d] /= norm;
            }
        }

        return pooled;
    }
}
```

> **API NOTE:** `FastBertTokenizer`'s exact `Encode` signature/return shape should be confirmed against the installed version; adjust the tuple destructuring if needed. The test in Step 5 is the gate — if the model proves hard to wire, the spec's fallback (Ollama `nomic-embed-text` via a small HTTP embedder) implements the same `IEmbedder` and unblocks the slice.

- [ ] **Step 7: Run the smoke test**

Run: `CCSTASH_MODEL_DIR=models/all-MiniLM-L6-v2 dotnet test --filter OnnxEmbedderTests`
Expected: PASS (or skipped if the model dir is unset).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: embedder interface, fake, and ONNX embedder"
```

---

### Task 5: Transcript parser

**Files:**
- Create: `src/CCStash.Core/Transcript/TranscriptModels.cs`, `src/CCStash.Core/Transcript/ITranscriptParser.cs`, `src/CCStash.Core/Transcript/TranscriptParser.cs`
- Create fixture: `tests/fixtures/sample-transcript.jsonl`
- Test: `tests/CCStash.Core.Tests/TranscriptParserTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum BlockKind { Text, Thinking, ToolUse, ToolResult }`
  - `record ContentBlock(BlockKind Kind, string Text, string? ToolName)`
  - `record TranscriptTurn(int Index, string Role, DateTimeOffset? Timestamp, IReadOnlyList<ContentBlock> Blocks)`
  - `ITranscriptParser` with `IReadOnlyList<TranscriptTurn> Parse(string jsonlPath)`; class `TranscriptParser`.

- [ ] **Step 1: Capture a small real fixture**

Create `tests/fixtures/sample-transcript.jsonl` with ~8 representative lines copied from a real transcript at `~/.claude/projects/<project>/<session>.jsonl` — include at least: a `user` line, an `assistant` line with a `text` block, an `assistant` line with a `tool_use` block, and a line carrying a `tool_result`. Scrub any sensitive content. Mark it copy-to-output in the test csproj:
```xml
<ItemGroup>
  <None Include="../fixtures/sample-transcript.jsonl" CopyToOutputDirectory="PreserveNewest" Link="fixtures/sample-transcript.jsonl" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing test**

`tests/CCStash.Core.Tests/TranscriptParserTests.cs`:
```csharp
using CCStash.Core.Transcript;

namespace CCStash.Core.Tests;

public class TranscriptParserTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "sample-transcript.jsonl");

    [Fact]
    public void Parses_turns_with_typed_blocks_and_sequential_index()
    {
        var turns = new TranscriptParser().Parse(Fixture);

        Assert.NotEmpty(turns);
        Assert.Contains(turns, t => t.Role == "user");
        Assert.Contains(turns, t => t.Blocks.Any(b => b.Kind == BlockKind.Text));
        Assert.Contains(turns, t => t.Blocks.Any(b => b.Kind == BlockKind.ToolUse));
        Assert.Equal(turns.Select((_, i) => i), turns.Select(t => t.Index)); // 0..n-1
    }

    [Fact]
    public void Skips_unparseable_lines_without_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, "not json\n{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n");
        try
        {
            var turns = new TranscriptParser().Parse(path);
            Assert.Single(turns);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test --filter TranscriptParserTests`
Expected: FAIL — types not defined.

- [ ] **Step 4: Implement the models and parser**

`src/CCStash.Core/Transcript/TranscriptModels.cs`:
```csharp
namespace CCStash.Core.Transcript;

/// <summary>The kind of a content block within a turn.</summary>
public enum BlockKind
{
    /// <summary>Plain assistant/user text.</summary>
    Text,

    /// <summary>Assistant reasoning.</summary>
    Thinking,

    /// <summary>A tool invocation.</summary>
    ToolUse,

    /// <summary>A tool result payload.</summary>
    ToolResult,
}

/// <summary>One typed piece of a turn's content.</summary>
public sealed record ContentBlock(BlockKind Kind, string Text, string? ToolName);

/// <summary>A single conversation turn with its content blocks.</summary>
public sealed record TranscriptTurn(int Index, string Role, DateTimeOffset? Timestamp, IReadOnlyList<ContentBlock> Blocks);
```

`src/CCStash.Core/Transcript/ITranscriptParser.cs`:
```csharp
namespace CCStash.Core.Transcript;

/// <summary>Parses a Claude Code session JSONL transcript into typed turns.</summary>
public interface ITranscriptParser
{
    /// <summary>Parse the JSONL at <paramref name="jsonlPath"/>; unparseable lines are skipped.</summary>
    IReadOnlyList<TranscriptTurn> Parse(string jsonlPath);
}
```

`src/CCStash.Core/Transcript/TranscriptParser.cs`:
```csharp
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

        if (!root.TryGetProperty("type", out var typeEl))
        {
            return null;
        }

        var type = typeEl.GetString();
        if (type is not ("user" or "assistant"))
        {
            return null; // only conversation turns are stashable
        }

        if (!root.TryGetProperty("message", out var message))
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
        => el.TryGetProperty(prop, out var v) ? v.GetString() ?? string.Empty : string.Empty;

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
```

> **NOTE:** Adjust property access to the exact fixture you captured in Step 1 (e.g. nested `message.content` vs top-level). The fixture-based test is the gate.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter TranscriptParserTests`
Expected: PASS (2 tests). Tune property names against the real fixture until green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: transcript JSONL parser"
```

---

### Task 6: Distiller

**Files:**
- Create: `src/CCStash.Core/Distillation/DistillModels.cs`, `src/CCStash.Core/Distillation/IDistiller.cs`, `src/CCStash.Core/Distillation/Distiller.cs`
- Test: `tests/CCStash.Core.Tests/DistillerTests.cs`

**Interfaces:**
- Consumes: `TranscriptTurn`, `ContentBlock`, `BlockKind` (Task 5).
- Produces:
  - `record DistillOptions(int MaxToolResultChars = 800, bool IncludeThinking = true)`
  - `record DistilledTurn(int Index, string Role, DateTimeOffset? Timestamp, string Text)`
  - `IDistiller` with `IReadOnlyList<DistilledTurn> Distill(IReadOnlyList<TranscriptTurn> turns, DistillOptions options)`; class `Distiller`.

- [ ] **Step 1: Write the failing test**

`tests/CCStash.Core.Tests/DistillerTests.cs`:
```csharp
using CCStash.Core.Distillation;
using CCStash.Core.Transcript;

namespace CCStash.Core.Tests;

public class DistillerTests
{
    private static TranscriptTurn Turn(int i, string role, params ContentBlock[] blocks) =>
        new(i, role, DateTimeOffset.UnixEpoch, blocks);

    [Fact]
    public void Truncates_tool_results_and_keeps_tool_name()
    {
        var big = new string('x', 5000);
        var turns = new[]
        {
            Turn(0, "assistant",
                new ContentBlock(BlockKind.ToolUse, "{\"path\":\"a.cs\"}", "Read"),
                new ContentBlock(BlockKind.ToolResult, big, null)),
        };

        var d = new Distiller().Distill(turns, new DistillOptions(MaxToolResultChars: 100));

        Assert.Single(d);
        Assert.Contains("Read", d[0].Text);
        Assert.True(d[0].Text.Length < 500);
        Assert.Contains("truncated", d[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Excludes_thinking_when_disabled_and_drops_empty_turns()
    {
        var turns = new[]
        {
            Turn(0, "assistant", new ContentBlock(BlockKind.Thinking, "secret reasoning", null)),
            Turn(1, "user", new ContentBlock(BlockKind.Text, "hello", null)),
        };

        var d = new Distiller().Distill(turns, new DistillOptions(IncludeThinking: false));

        Assert.Single(d);
        Assert.Equal("hello", d[0].Text);
        Assert.Equal(1, d[0].Index);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter DistillerTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement the distiller**

`src/CCStash.Core/Distillation/DistillModels.cs`:
```csharp
namespace CCStash.Core.Distillation;

/// <summary>Controls how raw turns are distilled into compact text.</summary>
public sealed record DistillOptions(int MaxToolResultChars = 800, bool IncludeThinking = true);

/// <summary>A turn reduced to a single compact text representation.</summary>
public sealed record DistilledTurn(int Index, string Role, DateTimeOffset? Timestamp, string Text);
```

`src/CCStash.Core/Distillation/IDistiller.cs`:
```csharp
using CCStash.Core.Transcript;

namespace CCStash.Core.Distillation;

/// <summary>Reduces transcript turns to compact, embeddable text.</summary>
public interface IDistiller
{
    /// <summary>Distill turns, truncating bulky tool output per <paramref name="options"/>.</summary>
    IReadOnlyList<DistilledTurn> Distill(IReadOnlyList<TranscriptTurn> turns, DistillOptions options);
}
```

`src/CCStash.Core/Distillation/Distiller.cs`:
```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter DistillerTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: turn distiller with tool-output truncation"
```

---

### Task 7: Chunker

**Files:**
- Create: `src/CCStash.Core/Chunking/ChunkModels.cs`, `src/CCStash.Core/Chunking/IChunker.cs`, `src/CCStash.Core/Chunking/Chunker.cs`
- Test: `tests/CCStash.Core.Tests/ChunkerTests.cs`

**Interfaces:**
- Consumes: `DistilledTurn` (Task 6).
- Produces:
  - `record ChunkOptions(int MaxChars = 3200)`
  - `record Chunk(int TurnIndex, string Role, DateTimeOffset? Timestamp, string Type, string Text)` where `Type` is `"turn"` or `"turn-part"`.
  - `IChunker` with `IReadOnlyList<Chunk> Chunk(IReadOnlyList<DistilledTurn> turns, ChunkOptions options)`; class `Chunker`.

- [ ] **Step 1: Write the failing test**

`tests/CCStash.Core.Tests/ChunkerTests.cs`:
```csharp
using CCStash.Core.Chunking;
using CCStash.Core.Distillation;

namespace CCStash.Core.Tests;

public class ChunkerTests
{
    [Fact]
    public void Short_turn_becomes_one_chunk()
    {
        var turns = new[] { new DistilledTurn(0, "user", DateTimeOffset.UnixEpoch, "hello world") };
        var chunks = new Chunker().Chunk(turns, new ChunkOptions(MaxChars: 100));

        Assert.Single(chunks);
        Assert.Equal("turn", chunks[0].Type);
        Assert.Equal(0, chunks[0].TurnIndex);
    }

    [Fact]
    public void Long_turn_splits_into_multiple_parts_preserving_turn_index()
    {
        var turns = new[] { new DistilledTurn(3, "assistant", null, new string('a', 250)) };
        var chunks = new Chunker().Chunk(turns, new ChunkOptions(MaxChars: 100));

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.Equal(3, c.TurnIndex));
        Assert.All(chunks, c => Assert.Equal("turn-part", c.Type));
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 100));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ChunkerTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement the chunker**

`src/CCStash.Core/Chunking/ChunkModels.cs`:
```csharp
namespace CCStash.Core.Chunking;

/// <summary>Controls chunk sizing.</summary>
public sealed record ChunkOptions(int MaxChars = 3200);

/// <summary>An embeddable chunk derived from a distilled turn.</summary>
public sealed record Chunk(int TurnIndex, string Role, DateTimeOffset? Timestamp, string Type, string Text);
```

`src/CCStash.Core/Chunking/IChunker.cs`:
```csharp
using CCStash.Core.Distillation;

namespace CCStash.Core.Chunking;

/// <summary>Splits distilled turns into size-bounded chunks.</summary>
public interface IChunker
{
    /// <summary>Chunk turns to at most <see cref="ChunkOptions.MaxChars"/> characters each.</summary>
    IReadOnlyList<Chunk> Chunk(IReadOnlyList<DistilledTurn> turns, ChunkOptions options);
}
```

`src/CCStash.Core/Chunking/Chunker.cs`:
```csharp
using CCStash.Core.Distillation;

namespace CCStash.Core.Chunking;

/// <summary>Default per-turn chunker; oversized turns split into ordered parts.</summary>
public sealed class Chunker : IChunker
{
    /// <inheritdoc/>
    public IReadOnlyList<Chunk> Chunk(IReadOnlyList<DistilledTurn> turns, ChunkOptions options)
    {
        var chunks = new List<Chunk>();

        foreach (var turn in turns)
        {
            if (turn.Text.Length <= options.MaxChars)
            {
                chunks.Add(new Chunk(turn.Index, turn.Role, turn.Timestamp, "turn", turn.Text));
                continue;
            }

            for (var offset = 0; offset < turn.Text.Length; offset += options.MaxChars)
            {
                var len = Math.Min(options.MaxChars, turn.Text.Length - offset);
                chunks.Add(new Chunk(turn.Index, turn.Role, turn.Timestamp, "turn-part", turn.Text.Substring(offset, len)));
            }
        }

        return chunks;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ChunkerTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: per-turn chunker"
```

---

### Task 8: Config + paths

**Files:**
- Create: `src/CCStash.Core/Config/CCStashConfig.cs`, `src/CCStash.Core/Config/CCStashPaths.cs`
- Test: `tests/CCStash.Core.Tests/CCStashPathsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `record CCStashConfig(string Store = "sqlite", string EmbeddingProvider = "onnx", string EmbeddingModel = "all-MiniLM-L6-v2", int MaxToolResultChars = 800, bool IncludeThinking = true, string RetrievalScope = "session", int RetrievalLimit = 6)` with `static CCStashConfig Load(string path)` and `bool ProjectWide => RetrievalScope == "project"`.
  - `static class CCStashPaths` with `string DataDir`, `string ConfigPath`, `string LogPath`, `string ProjectHash(string cwd)`, `string DbPath(string cwd)`.

- [ ] **Step 1: Write the failing test**

`tests/CCStash.Core.Tests/CCStashPathsTests.cs`:
```csharp
using CCStash.Core.Config;

namespace CCStash.Core.Tests;

public class CCStashPathsTests
{
    [Fact]
    public void ProjectHash_is_stable_and_path_safe()
    {
        var h1 = CCStashPaths.ProjectHash(@"C:\Dev\Foo");
        var h2 = CCStashPaths.ProjectHash(@"C:\Dev\Foo");

        Assert.Equal(h1, h2);
        Assert.DoesNotContain(Path.GetInvalidFileNameChars(), h1.Contains);
        Assert.EndsWith(".db", CCStashPaths.DbPath(@"C:\Dev\Foo"));
    }

    [Fact]
    public void Config_defaults_load_when_file_missing()
    {
        var cfg = CCStashConfig.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.Equal("sqlite", cfg.Store);
        Assert.Equal(6, cfg.RetrievalLimit);
        Assert.False(cfg.ProjectWide);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter CCStashPathsTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement config and paths**

`src/CCStash.Core/Config/CCStashConfig.cs`:
```csharp
using System.Text.Json;

namespace CCStash.Core.Config;

/// <summary>User-tunable CCStash settings.</summary>
public sealed record CCStashConfig(
    string Store = "sqlite",
    string EmbeddingProvider = "onnx",
    string EmbeddingModel = "all-MiniLM-L6-v2",
    int MaxToolResultChars = 800,
    bool IncludeThinking = true,
    string RetrievalScope = "session",
    int RetrievalLimit = 6)
{
    /// <summary>True when retrieval should span all sessions in the project.</summary>
    public bool ProjectWide => string.Equals(RetrievalScope, "project", StringComparison.OrdinalIgnoreCase);

    /// <summary>Load config from <paramref name="path"/>, or defaults if it is missing/invalid.</summary>
    public static CCStashConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new CCStashConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<CCStashConfig>(File.ReadAllText(path)) ?? new CCStashConfig();
        }
        catch (JsonException)
        {
            return new CCStashConfig();
        }
    }
}
```

`src/CCStash.Core/Config/CCStashPaths.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;

namespace CCStash.Core.Config;

/// <summary>Resolves CCStash's on-disk locations under the user profile.</summary>
public static class CCStashPaths
{
    /// <summary>Root data directory (<c>~/.claude/ccstash</c>).</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ccstash");

    /// <summary>Path to the global config file.</summary>
    public static string ConfigPath => Path.Combine(DataDir, "config.json");

    /// <summary>Path to the log file.</summary>
    public static string LogPath => Path.Combine(DataDir, "ccstash.log");

    /// <summary>A stable, filename-safe hash identifying a project directory.</summary>
    public static string ProjectHash(string cwd)
    {
        var normalized = Path.TrimEndingDirectorySeparator(cwd).ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>Path to the per-project sqlite database.</summary>
    public static string DbPath(string cwd) => Path.Combine(DataDir, $"{ProjectHash(cwd)}.db");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter CCStashPathsTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: config and path resolution"
```

---

### Task 9: StashService orchestration

**Files:**
- Create: `src/CCStash.Core/StashService.cs`
- Test: `tests/CCStash.Core.Tests/StashServiceTests.cs`

**Interfaces:**
- Consumes: `ITranscriptParser`, `IDistiller`, `IChunker`, `IEmbedder`, `IVectorStore`, `CCStashConfig`, model records from Tasks 2–8.
- Produces:
  - `record StashRequest(string TranscriptPath, string Project, string Session)`
  - `record StashResult(int NewChunks, int TotalChunks, string StashId)`
  - `IStashService` with `Task<StashResult> StashAsync(StashRequest request, CancellationToken ct = default)`; class `StashService(ITranscriptParser parser, IDistiller distiller, IChunker chunker, IEmbedder embedder, IVectorStore store, CCStashConfig config)`.
  - Chunk id format: `"{Project}:{Session}:{TurnIndex}:{partOrdinal}"`.

- [ ] **Step 1: Write the failing test**

`tests/CCStash.Core.Tests/StashServiceTests.cs`:
```csharp
using CCStash.Core;
using CCStash.Core.Chunking;
using CCStash.Core.Config;
using CCStash.Core.Distillation;
using CCStash.Core.Embeddings;
using CCStash.Core.Storage;
using CCStash.Core.Transcript;

namespace CCStash.Core.Tests;

public class StashServiceTests
{
    private static string WriteTranscript(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"t-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static StashService Build(IVectorStore store) => new(
        new TranscriptParser(), new Distiller(), new Chunker(),
        new FakeEmbedder(8), store, new CCStashConfig());

    [Fact]
    public async Task Stashes_turns_and_is_incremental_on_second_run()
    {
        using var store = new InMemoryVectorStore();
        await store.InitializeAsync(8, "fake-8");

        var path = WriteTranscript(
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"first question\"}}",
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"first answer\"}]}}");
        var req = new StashRequest(path, "proj", "s1");

        var r1 = await Build(store).StashAsync(req);
        var r2 = await Build(store).StashAsync(req); // same transcript, nothing new

        Assert.Equal(2, r1.NewChunks);
        Assert.Equal(0, r2.NewChunks);
        Assert.Equal(2, r2.TotalChunks);
        Assert.Equal("proj:s1", r2.StashId);

        File.Delete(path);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter StashServiceTests`
Expected: FAIL — `StashService` not defined.

- [ ] **Step 3: Implement the stash service**

`src/CCStash.Core/StashService.cs`:
```csharp
using CCStash.Core.Chunking;
using CCStash.Core.Config;
using CCStash.Core.Distillation;
using CCStash.Core.Embeddings;
using CCStash.Core.Storage;
using CCStash.Core.Transcript;

namespace CCStash.Core;

/// <summary>Input describing what to stash.</summary>
public sealed record StashRequest(string TranscriptPath, string Project, string Session);

/// <summary>Outcome of a stash operation.</summary>
public sealed record StashResult(int NewChunks, int TotalChunks, string StashId);

/// <summary>Orchestrates parse → distill → chunk → embed → store, incrementally.</summary>
public interface IStashService
{
    /// <summary>Stash any turns newer than what is already stored for the session.</summary>
    Task<StashResult> StashAsync(StashRequest request, CancellationToken ct = default);
}

/// <inheritdoc cref="IStashService"/>
public sealed class StashService(
    ITranscriptParser parser,
    IDistiller distiller,
    IChunker chunker,
    IEmbedder embedder,
    IVectorStore store,
    CCStashConfig config) : IStashService
{
    /// <inheritdoc/>
    public async Task<StashResult> StashAsync(StashRequest request, CancellationToken ct = default)
    {
        await store.InitializeAsync(embedder.Dimension, embedder.ModelId, ct);

        var highWater = await store.GetHighWaterMarkAsync(request.Session, ct);
        var turns = parser.Parse(request.TranscriptPath).Where(t => t.Index > highWater).ToList();

        var distilled = distiller.Distill(turns, new DistillOptions(config.MaxToolResultChars, config.IncludeThinking));
        var chunks = chunker.Chunk(distilled, new ChunkOptions());

        var stored = new List<StoredChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var embedding = await embedder.EmbedAsync(c.Text, ct);
            var id = $"{request.Project}:{request.Session}:{c.TurnIndex}:{i}";
            stored.Add(new StoredChunk(
                id, request.Project, request.Session, c.TurnIndex,
                c.Role, c.Type, c.Timestamp, c.Text, embedding));
        }

        if (stored.Count > 0)
        {
            await store.UpsertAsync(stored, ct);
        }

        var total = await store.CountAsync(request.Session, ct);
        return new StashResult(stored.Count, total, $"{request.Project}:{request.Session}");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter StashServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: incremental stash orchestration"
```

---

### Task 10: RetrievalService

**Files:**
- Create: `src/CCStash.Core/RetrievalService.cs`
- Test: `tests/CCStash.Core.Tests/RetrievalServiceTests.cs`

**Interfaces:**
- Consumes: `IEmbedder`, `IVectorStore`, `SearchHit` (Tasks 2, 4).
- Produces:
  - `record RetrievedChunk(string Text, int TurnIndex, string Role, DateTimeOffset? Timestamp, float Score)`
  - `IRetrievalService` with `Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, int limit, string? session, CancellationToken ct = default)`; class `RetrievalService(IEmbedder embedder, IVectorStore store)`.

- [ ] **Step 1: Write the failing test**

`tests/CCStash.Core.Tests/RetrievalServiceTests.cs`:
```csharp
using CCStash.Core;
using CCStash.Core.Embeddings;
using CCStash.Core.Storage;

namespace CCStash.Core.Tests;

public class RetrievalServiceTests
{
    [Fact]
    public async Task Retrieves_most_similar_chunk_text()
    {
        var embedder = new FakeEmbedder(8);
        using var store = new InMemoryVectorStore();
        await store.InitializeAsync(8, embedder.ModelId);
        await store.UpsertAsync([
            new StoredChunk("1", "p", "s1", 0, "user", "turn", null, "database migration plan",
                await embedder.EmbedAsync("database migration plan")),
            new StoredChunk("2", "p", "s1", 1, "user", "turn", null, "lunch menu options",
                await embedder.EmbedAsync("lunch menu options")),
        ]);

        var hits = await new RetrievalService(embedder, store).RetrieveAsync("database migration plan", 1, "s1");

        Assert.Single(hits);
        Assert.Equal("database migration plan", hits[0].Text);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter RetrievalServiceTests`
Expected: FAIL — `RetrievalService` not defined.

- [ ] **Step 3: Implement the retrieval service**

`src/CCStash.Core/RetrievalService.cs`:
```csharp
using CCStash.Core.Embeddings;
using CCStash.Core.Storage;

namespace CCStash.Core;

/// <summary>A retrieved chunk projected for presentation.</summary>
public sealed record RetrievedChunk(string Text, int TurnIndex, string Role, DateTimeOffset? Timestamp, float Score);

/// <summary>Embeds a query and returns the nearest stored chunks.</summary>
public interface IRetrievalService
{
    /// <summary>Retrieve up to <paramref name="limit"/> chunks, optionally scoped to a session.</summary>
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, int limit, string? session, CancellationToken ct = default);
}

/// <inheritdoc cref="IRetrievalService"/>
public sealed class RetrievalService(IEmbedder embedder, IVectorStore store) : IRetrievalService
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, int limit, string? session, CancellationToken ct = default)
    {
        await store.InitializeAsync(embedder.Dimension, embedder.ModelId, ct);
        var q = await embedder.EmbedAsync(query, ct);
        var hits = await store.SearchAsync(q, limit, session, ct);
        return hits
            .Select(h => new RetrievedChunk(h.Chunk.Text, h.Chunk.TurnIndex, h.Chunk.Role, h.Chunk.Timestamp, h.Score))
            .ToList();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter RetrievalServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: retrieval service"
```

---

### Task 11: CLI tool — `stash`, `pointer`, `status`, `search`

**Files:**
- Create: `src/CCStash/CCStash.csproj`, `src/CCStash/Program.cs`, `src/CCStash/Composition.cs`, `src/CCStash/Hooks/HookInput.cs` (in Core), `src/CCStash/Verbs/StashVerb.cs`, `src/CCStash/Verbs/PointerVerb.cs`, `src/CCStash/Verbs/StatusVerb.cs`, `src/CCStash/Verbs/SearchVerb.cs`
- Create: `src/CCStash.Core/Hooks/HookInput.cs`
- Test: `tests/CCStash.Core.Tests/HookInputTests.cs`

**Interfaces:**
- Consumes: all services (Tasks 8–10), `CCStashPaths`, `CCStashConfig`.
- Produces:
  - `record HookInput(string SessionId, string TranscriptPath, string Cwd, string? Source, string? CompactionTriggeredBy)` with `static HookInput FromJson(string json)`.
  - `static class Composition` with `IStashService BuildStash(string cwd, CCStashConfig cfg)`, `IRetrievalService BuildRetrieval(string cwd, CCStashConfig cfg)`, `IVectorStore BuildStore(string cwd, CCStashConfig cfg)`, `IEmbedder BuildEmbedder(CCStashConfig cfg)`.
  - Tool command: `ccstash` (via `dnx CCStash -- <verb>`).

- [ ] **Step 1: Write the failing test for hook input parsing**

`tests/CCStash.Core.Tests/HookInputTests.cs`:
```csharp
using CCStash.Core.Hooks;

namespace CCStash.Core.Tests;

public class HookInputTests
{
    [Fact]
    public void Parses_precompact_payload()
    {
        var json = """
        {"session_id":"abc","transcript_path":"/t.jsonl","cwd":"/proj",
         "hook_event_name":"PreCompact","compaction_triggered_by":"auto"}
        """;

        var input = HookInput.FromJson(json);

        Assert.Equal("abc", input.SessionId);
        Assert.Equal("/t.jsonl", input.TranscriptPath);
        Assert.Equal("/proj", input.Cwd);
        Assert.Equal("auto", input.CompactionTriggeredBy);
    }

    [Fact]
    public void Parses_sessionstart_compact_payload()
    {
        var json = """{"session_id":"abc","transcript_path":"/t.jsonl","cwd":"/proj","source":"compact"}""";
        var input = HookInput.FromJson(json);
        Assert.Equal("compact", input.Source);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter HookInputTests`
Expected: FAIL — `HookInput` not defined.

- [ ] **Step 3: Implement HookInput**

`src/CCStash.Core/Hooks/HookInput.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCStash.Core.Hooks;

/// <summary>The JSON Claude Code passes to a hook on stdin (fields we use).</summary>
public sealed record HookInput(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("transcript_path")] string TranscriptPath,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("compaction_triggered_by")] string? CompactionTriggeredBy)
{
    /// <summary>Parse a hook payload; missing optional fields become null.</summary>
    public static HookInput FromJson(string json)
        => JsonSerializer.Deserialize<HookInput>(json)
           ?? throw new JsonException("Empty hook input.");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter HookInputTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Create the tool project and composition root**

Run:
```bash
dotnet new console -o src/CCStash -n CCStash
dotnet sln add src/CCStash
dotnet add src/CCStash reference src/CCStash.Core src/CCStash.Stores.Sqlite src/CCStash.Embeddings.Onnx
dotnet add src/CCStash package System.CommandLine --prerelease
```

Edit `src/CCStash/CCStash.csproj` to make it a tool:
```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>ccstash</ToolCommandName>
  <PackageId>CCStash</PackageId>
  <Version>0.1.0</Version>
</PropertyGroup>
```

`src/CCStash/Composition.cs`:
```csharp
using CCStash.Core;
using CCStash.Core.Chunking;
using CCStash.Core.Config;
using CCStash.Core.Distillation;
using CCStash.Core.Embeddings;
using CCStash.Core.Storage;
using CCStash.Core.Transcript;
using CCStash.Embeddings.Onnx;
using CCStash.Stores.Sqlite;

namespace CCStash;

/// <summary>Builds CCStash services from configuration. Single composition root.</summary>
internal static class Composition
{
    public static IEmbedder BuildEmbedder(CCStashConfig cfg)
    {
        if (cfg.EmbeddingProvider == "onnx")
        {
            var dir = Environment.GetEnvironmentVariable("CCSTASH_MODEL_DIR")
                      ?? Path.Combine(CCStashPaths.DataDir, "models", cfg.EmbeddingModel);
            var model = Path.Combine(dir, "model.onnx");
            if (File.Exists(model))
            {
                return new OnnxEmbedder(model, Path.Combine(dir, "vocab.txt"));
            }
        }

        return new FakeEmbedder(384); // offline fallback so hooks never fail
    }

    public static IVectorStore BuildStore(string cwd, CCStashConfig cfg)
        => new SqliteVectorStore(CCStashPaths.DbPath(cwd));

    public static IStashService BuildStash(string cwd, CCStashConfig cfg)
        => new StashService(new TranscriptParser(), new Distiller(), new Chunker(),
            BuildEmbedder(cfg), BuildStore(cwd, cfg), cfg);

    public static IRetrievalService BuildRetrieval(string cwd, CCStashConfig cfg)
        => new RetrievalService(BuildEmbedder(cfg), BuildStore(cwd, cfg));
}
```

- [ ] **Step 6: Implement the verbs**

`src/CCStash/Verbs/StashVerb.cs`:
```csharp
using CCStash.Core;
using CCStash.Core.Config;
using CCStash.Core.Hooks;

namespace CCStash.Verbs;

/// <summary>Handles the <c>stash</c> verb (invoked by the PreCompact hook). Never throws.</summary>
internal static class StashVerb
{
    public static async Task<int> RunAsync(TextReader stdin)
    {
        try
        {
            var input = HookInput.FromJson(await stdin.ReadToEndAsync());
            var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);
            var svc = Composition.BuildStash(input.Cwd, cfg);
            var result = await svc.StashAsync(new StashRequest(input.TranscriptPath, CCStashPaths.ProjectHash(input.Cwd), input.SessionId));
            Log($"stash ok: +{result.NewChunks} ({result.TotalChunks} total) {result.StashId}");
        }
        catch (Exception ex)
        {
            Log($"stash failed: {ex}");
        }

        return 0; // hook safety: always succeed
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

`src/CCStash/Verbs/PointerVerb.cs`:
```csharp
using System.Text.Json;
using CCStash.Core.Config;
using CCStash.Core.Hooks;

namespace CCStash.Verbs;

/// <summary>Handles the <c>pointer</c> verb (SessionStart:compact). Emits a minimal pointer only.</summary>
internal static class PointerVerb
{
    public static async Task<int> RunAsync(TextReader stdin, TextWriter stdout)
    {
        try
        {
            var input = HookInput.FromJson(await stdin.ReadToEndAsync());
            var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);
            using var store = Composition.BuildStore(input.Cwd, cfg);
            await store.InitializeAsync(Composition.BuildEmbedder(cfg).Dimension, cfg.EmbeddingModel);

            var count = await store.CountAsync(input.SessionId);
            if (count == 0)
            {
                return 0; // nothing stashed: emit nothing
            }

            var pointer =
                $"🗄️ Detailed pre-compaction context for this session is stashed " +
                $"({count} chunks). Call the `retrieve_context` tool to pull specific earlier " +
                $"details (decisions, file contents, errors) when you need them.";

            var output = new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "SessionStart",
                    additionalContext = pointer,
                },
            };
            await stdout.WriteAsync(JsonSerializer.Serialize(output));
        }
        catch
        {
            // hook safety: emit nothing on failure
        }

        return 0;
    }
}
```

`src/CCStash/Verbs/StatusVerb.cs`:
```csharp
using CCStash.Core.Config;

namespace CCStash.Verbs;

/// <summary>Handles the <c>status</c> verb: prints stash stats for the current project.</summary>
internal static class StatusVerb
{
    public static async Task<int> RunAsync(string cwd, TextWriter stdout)
    {
        var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);
        using var store = Composition.BuildStore(cwd, cfg);
        var embedder = Composition.BuildEmbedder(cfg);
        await store.InitializeAsync(embedder.Dimension, embedder.ModelId);

        var total = await store.CountAsync(null);
        var latest = await store.GetLatestSessionAsync();
        await stdout.WriteLineAsync($"DB: {CCStashPaths.DbPath(cwd)}");
        await stdout.WriteLineAsync($"Model: {embedder.ModelId} (dim {embedder.Dimension})");
        await stdout.WriteLineAsync($"Chunks: {total}; latest session: {latest ?? "(none)"}");
        return 0;
    }
}
```

`src/CCStash/Verbs/SearchVerb.cs`:
```csharp
using CCStash.Core.Config;

namespace CCStash.Verbs;

/// <summary>Handles the <c>search</c> verb: ad-hoc semantic search for debugging.</summary>
internal static class SearchVerb
{
    public static async Task<int> RunAsync(string cwd, string query, TextWriter stdout)
    {
        var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);
        var svc = Composition.BuildRetrieval(cwd, cfg);
        var hits = await svc.RetrieveAsync(query, cfg.RetrievalLimit, session: null);
        foreach (var h in hits)
        {
            await stdout.WriteLineAsync($"[{h.Score:F3}] turn {h.TurnIndex} ({h.Role}): {Preview(h.Text)}");
        }

        return 0;
    }

    private static string Preview(string text)
        => text.Length <= 120 ? text : text[..120] + "…";
}
```

- [ ] **Step 7: Wire the root command**

`src/CCStash/Program.cs`:
```csharp
using System.CommandLine;
using CCStash.Verbs;

var root = new RootCommand("CCStash — vector context stash for Claude Code");

var stash = new Command("stash", "Stash the transcript (PreCompact hook).");
stash.SetHandler(async () => Environment.ExitCode = await StashVerb.RunAsync(Console.In));

var pointer = new Command("pointer", "Emit the post-compaction pointer (SessionStart hook).");
pointer.SetHandler(async () => Environment.ExitCode = await PointerVerb.RunAsync(Console.In, Console.Out));

var status = new Command("status", "Show stash status for the current project.");
status.SetHandler(async () => Environment.ExitCode = await StatusVerb.RunAsync(Environment.CurrentDirectory, Console.Out));

var queryArg = new Argument<string>("query");
var search = new Command("search", "Semantic search the stash (debug).") { queryArg };
search.SetHandler(async (string q) => Environment.ExitCode = await SearchVerb.RunAsync(Environment.CurrentDirectory, q, Console.Out), queryArg);

root.AddCommand(stash);
root.AddCommand(pointer);
root.AddCommand(status);
root.AddCommand(search);
// `mcp` and `init` commands are added in Tasks 12 and 13.

return await root.InvokeAsync(args);
```

> **API NOTE:** `System.CommandLine` is pre-release and its `SetHandler`/`Argument` API shifts between versions. Confirm against the installed version and adjust the handler wiring; the behavior (verbs reading stdin/args) is what matters.

- [ ] **Step 8: Build and manually verify stash + pointer**

Run:
```bash
dotnet build
echo '{"session_id":"s1","transcript_path":"tests/fixtures/sample-transcript.jsonl","cwd":"'$PWD'","compaction_triggered_by":"manual"}' | dotnet run --project src/CCStash -- stash
echo '{"session_id":"s1","transcript_path":"x","cwd":"'$PWD'","source":"compact"}' | dotnet run --project src/CCStash -- pointer
dotnet run --project src/CCStash -- status
```
Expected: `stash` exits 0 and writes to the log; `pointer` prints a JSON object with `additionalContext`; `status` shows a non-zero chunk count.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: CLI verbs stash, pointer, status, search"
```

---

### Task 12: MCP server — `retrieve_context`

**Files:**
- Create: `src/CCStash.Mcp/CCStash.Mcp.csproj`, `src/CCStash.Mcp/RetrieveContextTools.cs`
- Create: `src/CCStash/Verbs/McpVerb.cs`; modify `src/CCStash/Program.cs`

**Interfaces:**
- Consumes: `IRetrievalService` (Task 10), `IVectorStore.GetLatestSessionAsync` (Task 2), `Composition` (Task 11).
- Produces: an MCP stdio server exposing `retrieve_context(query, limit)` and `list_stashes()`, run via `dnx CCStash -- mcp`.

- [ ] **Step 1: Create the MCP project**

Run:
```bash
dotnet new classlib -o src/CCStash.Mcp -n CCStash.Mcp
dotnet sln add src/CCStash.Mcp
dotnet add src/CCStash.Mcp reference src/CCStash.Core
dotnet add src/CCStash.Mcp package ModelContextProtocol --prerelease
dotnet add src/CCStash reference src/CCStash.Mcp
rm src/CCStash.Mcp/Class1.cs
```

- [ ] **Step 2: Implement the tool type**

`src/CCStash.Mcp/RetrieveContextTools.cs`:
```csharp
using System.ComponentModel;
using System.Text;
using CCStash.Core;
using CCStash.Core.Storage;
using ModelContextProtocol.Server;

namespace CCStash.Mcp;

/// <summary>MCP tools for retrieving stashed context. Constructed with project-scoped services.</summary>
[McpServerToolType]
public sealed class RetrieveContextTools(IRetrievalService retrieval, IVectorStore store, bool projectWide)
{
    /// <summary>Search the stash and return the most relevant earlier context.</summary>
    [McpServerTool(Name = "retrieve_context")]
    [Description("Retrieve specific earlier context (decisions, file contents, errors) stashed before compaction. Call when you need detail that was summarized away.")]
    public async Task<string> RetrieveContext(
        [Description("What you are looking for, in natural language.")] string query,
        [Description("Max chunks to return (default 6).")] int limit = 6)
    {
        var session = projectWide ? null : await store.GetLatestSessionAsync();
        var hits = await retrieval.RetrieveAsync(query, limit, session);
        if (hits.Count == 0)
        {
            return "No stashed context matched.";
        }

        var sb = new StringBuilder();
        foreach (var h in hits)
        {
            sb.AppendLine($"--- turn {h.TurnIndex} ({h.Role}, score {h.Score:F3}) ---");
            sb.AppendLine(h.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Report how much context is currently stashed.</summary>
    [McpServerTool(Name = "list_stashes")]
    [Description("Report the count of stashed context chunks available to retrieve.")]
    public async Task<string> ListStashes()
    {
        var total = await store.CountAsync(null);
        var latest = await store.GetLatestSessionAsync();
        return $"{total} chunks stashed; latest session: {latest ?? "(none)"}.";
    }
}
```

- [ ] **Step 3: Implement the `mcp` verb (host)**

`src/CCStash/Verbs/McpVerb.cs`:
```csharp
using CCStash.Core.Config;
using CCStash.Core.Storage;
using CCStash.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CCStash.Verbs;

/// <summary>Handles the <c>mcp</c> verb: runs the stdio MCP server for the current project.</summary>
internal static class McpVerb
{
    public static async Task<int> RunAsync(string cwd)
    {
        var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);
        var store = Composition.BuildStore(cwd, cfg);
        var embedder = Composition.BuildEmbedder(cfg);
        await store.InitializeAsync(embedder.Dimension, embedder.ModelId);
        var retrieval = Composition.BuildRetrieval(cwd, cfg);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton(new RetrieveContextTools(retrieval, store, cfg.ProjectWide));
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<RetrieveContextTools>();

        await builder.Build().RunAsync();
        return 0;
    }
}
```
Add packages to the tool project so the host builds:
```bash
dotnet add src/CCStash package Microsoft.Extensions.Hosting
dotnet add src/CCStash package ModelContextProtocol --prerelease
```

- [ ] **Step 4: Register the verb in Program.cs**

Add to `src/CCStash/Program.cs` before `root.InvokeAsync`:
```csharp
var mcp = new Command("mcp", "Run the retrieve_context MCP server (stdio).");
mcp.SetHandler(async () => Environment.ExitCode = await McpVerb.RunAsync(Environment.CurrentDirectory));
root.AddCommand(mcp);
```

> **API NOTE:** Confirm the `ModelContextProtocol` registration API (`AddMcpServer().WithStdioServerTransport().WithTools<T>()`) and attribute names against the installed pre-release version; adjust if the fluent surface differs. The tool contract (`retrieve_context(query, limit)`) is the invariant.

- [ ] **Step 5: Smoke-test the server starts**

Run:
```bash
dotnet build
printf '{"jsonrpc":"2.0","id":1,"method":"tools/list"}\n' | dotnet run --project src/CCStash -- mcp
```
Expected: a JSON-RPC response listing `retrieve_context` and `list_stashes` (then Ctrl-C). If the handshake requires `initialize` first, that is expected — the goal is confirming the server starts and advertises the tools.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: retrieve_context MCP server"
```

---

### Task 13: `init` verb + packaging + end-to-end

**Files:**
- Create: `src/CCStash/Verbs/InitVerb.cs`; modify `src/CCStash/Program.cs`
- Create: `docs/INSTALL.md`

**Interfaces:**
- Consumes: `CCStashPaths` (Task 8).
- Produces: `init` verb that writes hook config to `~/.claude/settings.json` and `.mcp.json`, writes a default `config.json`, and pre-warms the package.

- [ ] **Step 1: Implement the `init` verb**

`src/CCStash/Verbs/InitVerb.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using CCStash.Core.Config;

namespace CCStash.Verbs;

/// <summary>Handles the <c>init</c> verb: wires CCStash hooks + MCP server into Claude Code config.</summary>
internal static class InitVerb
{
    public static Task<int> RunAsync(string cwd, TextWriter stdout)
    {
        Directory.CreateDirectory(CCStashPaths.DataDir);
        if (!File.Exists(CCStashPaths.ConfigPath))
        {
            File.WriteAllText(CCStashPaths.ConfigPath,
                JsonSerializer.Serialize(new CCStashConfig(), new JsonSerializerOptions { WriteIndented = true }));
        }

        WriteHooks();
        WriteMcp(cwd);
        stdout.WriteLine("CCStash initialized: hooks + .mcp.json written, config at " + CCStashPaths.ConfigPath);
        stdout.WriteLine("Place an ONNX model under ~/.claude/ccstash/models/all-MiniLM-L6-v2/ (model.onnx + vocab.txt) for local embeddings.");
        return Task.FromResult(0);
    }

    private static void WriteHooks()
    {
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
        var root = LoadObject(settingsPath);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;

        hooks["PreCompact"] = HookArray("dnx -y CCStash -- stash");
        hooks["SessionStart"] = HookArray("dnx -y CCStash -- pointer", matcher: "compact");

        Save(settingsPath, root);
    }

    private static void WriteMcp(string cwd)
    {
        var mcpPath = Path.Combine(cwd, ".mcp.json");
        var root = LoadObject(mcpPath);
        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        root["mcpServers"] = servers;
        servers["ccstash"] = new JsonObject
        {
            ["command"] = "dnx",
            ["args"] = new JsonArray("-y", "CCStash", "--", "mcp"),
        };
        Save(mcpPath, root);
    }

    private static JsonArray HookArray(string command, string? matcher = null)
    {
        var hook = new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }),
        };
        if (matcher is not null)
        {
            hook["matcher"] = matcher;
        }

        return new JsonArray(hook);
    }

    private static JsonObject LoadObject(string path)
        => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();

    private static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
```

> **NOTE:** Confirm the exact `settings.json` hooks schema (event → array of `{matcher?, hooks:[{type,command}]}`) against the current Claude Code docs before relying on it; the `init` verb is a convenience over hand-editing. `init` should merge, not clobber, existing hooks in a real implementation — for the slice, document that it overwrites the `PreCompact`/`SessionStart` keys.

- [ ] **Step 2: Register the verb**

Add to `src/CCStash/Program.cs`:
```csharp
var init = new Command("init", "Wire CCStash hooks + MCP server into Claude Code.");
init.SetHandler(async () => Environment.ExitCode = await InitVerb.RunAsync(Environment.CurrentDirectory, Console.Out));
root.AddCommand(init);
```

- [ ] **Step 3: Build and run init against a temp HOME**

Run:
```bash
dotnet build
dotnet run --project src/CCStash -- init
cat ~/.claude/.mcp.json 2>/dev/null || cat .mcp.json
```
Expected: `.mcp.json` contains a `ccstash` server entry; `~/.claude/settings.json` contains `PreCompact` and `SessionStart` hook entries invoking `dnx -y CCStash`.

- [ ] **Step 4: Write the install/E2E doc**

`docs/INSTALL.md`:
```markdown
# Installing CCStash

Requires the .NET 10 SDK (provides `dnx`).

1. Pack and push (or use a local feed):
   `dotnet pack src/CCStash -c Release` then push `CCStash.0.1.0.nupkg` to your NuGet source.
2. Place a local embedding model at `~/.claude/ccstash/models/all-MiniLM-L6-v2/` (`model.onnx`, `vocab.txt`).
   Without it, CCStash falls back to a non-semantic fake embedder (the loop still runs).
3. From your project directory: `dnx -y CCStash -- init`
4. Start (or restart) Claude Code so it picks up the hooks and the `ccstash` MCP server.

## End-to-end check
1. Have a normal conversation, then run `/compact`.
2. `PreCompact` runs `ccstash stash` → check `~/.claude/ccstash/ccstash.log` shows `stash ok`.
3. After compaction, the `SessionStart` pointer appears as a system reminder mentioning `retrieve_context`.
4. Ask Claude something only answerable from earlier detail; confirm it calls `retrieve_context` and recovers it.
5. `dnx CCStash -- status` shows the stored chunk count.
```

- [ ] **Step 5: Full build + test gate**

Run: `dotnet test`
Expected: all unit tests pass (ONNX smoke test skipped unless `CCSTASH_MODEL_DIR` is set).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: init wiring, packaging, and E2E docs"
```

---

## Self-Review

**Spec coverage:**
- Pre-compaction stash → Tasks 5–9, 11 (`stash`). ✓
- Post-compaction minimal pointer → Task 11 (`pointer`). ✓
- `retrieve_context` MCP tool → Task 12. ✓
- sqlite-vec default store → Task 3 (with documented fallback). ✓
- Local ONNX embeddings + fake fallback → Task 4. ✓
- Distillation truncating tool output → Task 6. ✓
- Per-turn chunking → Task 7. ✓
- `project:session` identifier + current-session scope (latest-session approximation in MCP) → Tasks 8, 9, 12. ✓
- Config → Task 8. ✓
- Hook safety (always exit 0) → Task 11. ✓
- `dnx`/.NET tool distribution + `init` wiring → Tasks 11, 13. ✓
- Incremental high-water mark → Tasks 2, 9. ✓
- Deferred (Qdrant, hybrid, API embeddings, `/clear`, project-wide default) → correctly absent. ✓

**Placeholder scan:** No `TODO`/`TBD`. Three explicit `API NOTE`/`NOTE` callouts flag pre-release framework surfaces (sqlite-vec loading, ONNX tokenizer, `System.CommandLine`, MCP SDK, settings schema) to confirm against installed versions — these are validation instructions, not unfinished steps, each with a working fallback.

**Type consistency:** `IVectorStore` signatures (`InitializeAsync`, `UpsertAsync`, `SearchAsync`, `CountAsync`, `GetHighWaterMarkAsync`, `GetLatestSessionAsync`) are used consistently in Tasks 2, 3, 9, 10, 12. `StoredChunk`/`SearchHit`/`Chunk`/`DistilledTurn`/`TranscriptTurn`/`RetrievedChunk`/`HookInput` shapes match across producers and consumers. Chunk id format `{Project}:{Session}:{TurnIndex}:{ordinal}` defined in Task 9 and consumed nowhere conflicting. `IEmbedder` (`Dimension`, `ModelId`, `EmbedAsync`, `EmbedBatchAsync`) consistent across Tasks 4, 9, 10, 11.
