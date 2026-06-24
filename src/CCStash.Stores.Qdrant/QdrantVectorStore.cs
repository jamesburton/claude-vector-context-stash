using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CCStash.Core.Storage;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace CCStash.Stores.Qdrant;

/// <summary>
/// A Qdrant-backed <see cref="IVectorStore"/>. The collection name encodes the project, embedding
/// model, and dimension, so switching models never mixes incompatible vector spaces (a new model
/// simply uses a new collection). String chunk ids are mapped to deterministic UUID point ids;
/// the original id and all metadata live in the point payload.
/// </summary>
public sealed class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly string _baseName;
    private string _collection = string.Empty;

    /// <summary>Create a store against a Qdrant gRPC endpoint, scoped to a project base name.</summary>
    public QdrantVectorStore(string host, int port, string projectBaseName, string? apiKey = null)
    {
        _client = string.IsNullOrEmpty(apiKey)
            ? new QdrantClient(host, port)
            : new QdrantClient(host, port, apiKey: apiKey);
        _baseName = projectBaseName;
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(int dimension, string embeddingModel, CancellationToken ct = default)
    {
        _collection = Sanitize($"ccstash_{_baseName}_{embeddingModel}_{dimension}");
        if (!await _client.CollectionExistsAsync(_collection, ct))
        {
            await _client.CreateCollectionAsync(
                _collection,
                new VectorParams { Size = (ulong)dimension, Distance = Distance.Cosine },
                cancellationToken: ct);
        }
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(IReadOnlyList<StoredChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        var points = chunks.Select(c => new PointStruct
        {
            Id = new PointId { Uuid = DeterministicUuid(c.Id) },
            Vectors = c.Embedding,
            Payload =
            {
                ["id"] = c.Id,
                ["project"] = c.Project,
                ["session"] = c.Session,
                ["turn_index"] = c.TurnIndex,
                ["role"] = c.Role,
                ["type"] = c.Type,
                ["ts"] = c.Timestamp?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                ["text"] = c.Text,
            },
        }).ToList();

        await _client.UpsertAsync(_collection, points, cancellationToken: ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(float[] query, int limit, string? session, CancellationToken ct = default)
    {
        var hits = await _client.SearchAsync(
            _collection,
            query,
            filter: session is null ? null : SessionFilter(session),
            limit: (ulong)limit,
            payloadSelector: true,
            cancellationToken: ct);

        return hits.Select(p => new SearchHit(ToChunk(p.Payload), p.Score)).ToList();
    }

    /// <inheritdoc/>
    public async Task<int> CountAsync(string? session, CancellationToken ct = default)
    {
        var count = await _client.CountAsync(
            _collection,
            filter: session is null ? null : SessionFilter(session),
            cancellationToken: ct);
        return (int)count;
    }

    /// <inheritdoc/>
    public async Task<int> GetHighWaterMarkAsync(string session, CancellationToken ct = default)
    {
        var points = await ScrollAllAsync(SessionFilter(session), ct);
        return points.Count == 0 ? -1 : points.Max(p => (int)p.Payload["turn_index"].IntegerValue);
    }

    /// <inheritdoc/>
    public async Task<string?> GetLatestSessionAsync(CancellationToken ct = default)
    {
        var points = await ScrollAllAsync(null, ct);
        return points
            .OrderByDescending(p => p.Payload.TryGetValue("ts", out var ts) ? ts.StringValue : string.Empty)
            .Select(p => p.Payload["session"].StringValue)
            .FirstOrDefault();
    }

    /// <summary>Dispose the underlying gRPC client.</summary>
    public void Dispose() => _client.Dispose();

    private async Task<List<RetrievedPoint>> ScrollAllAsync(Filter? filter, CancellationToken ct)
    {
        var all = new List<RetrievedPoint>();
        PointId? offset = null;
        do
        {
            var page = await _client.ScrollAsync(
                _collection,
                filter: filter,
                limit: 256,
                offset: offset,
                payloadSelector: true,
                vectorsSelector: false,
                cancellationToken: ct);
            all.AddRange(page.Result);
            offset = page.NextPageOffset;
        }
        while (offset is not null);

        return all;
    }

    private static Filter SessionFilter(string session) => new()
    {
        Must =
        {
            new Condition { Field = new FieldCondition { Key = "session", Match = new Match { Keyword = session } } },
        },
    };

    private static StoredChunk ToChunk(Google.Protobuf.Collections.MapField<string, Value> p)
    {
        var ts = p.TryGetValue("ts", out var tsv) ? tsv.StringValue : string.Empty;
        return new StoredChunk(
            p["id"].StringValue,
            p["project"].StringValue,
            p["session"].StringValue,
            (int)p["turn_index"].IntegerValue,
            p["role"].StringValue,
            p["type"].StringValue,
            string.IsNullOrEmpty(ts) ? null : DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture),
            p["text"].StringValue,
            []);
    }

    private static string DeterministicUuid(string s)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return new Guid(hash).ToString();
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        return sb.ToString();
    }
}
