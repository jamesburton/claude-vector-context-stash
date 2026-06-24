using CCStash.Core.Embeddings;
using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CCStash.Embeddings.Onnx;

/// <summary>
/// Local sentence-embedding via all-MiniLM-L6-v2 (ONNX) with mean pooling + L2 normalization.
/// Loads an ONNX model and a Hugging Face <c>tokenizer.json</c> from disk; no network or API key.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private const int MaxTokens = 256;
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly string _inputIds;
    private readonly string _attentionMask;
    private readonly string? _tokenTypeIds;

    private OnnxEmbedder(InferenceSession session, BertTokenizer tokenizer)
    {
        _session = session;
        _tokenizer = tokenizer;

        var inputs = session.InputMetadata.Keys.ToList();
        _inputIds = inputs.FirstOrDefault(k => k.Contains("input_ids", StringComparison.OrdinalIgnoreCase)) ?? "input_ids";
        _attentionMask = inputs.FirstOrDefault(k => k.Contains("attention", StringComparison.OrdinalIgnoreCase)) ?? "attention_mask";
        _tokenTypeIds = inputs.FirstOrDefault(k => k.Contains("token_type", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public int Dimension => 384;

    /// <inheritdoc/>
    public string ModelId => "all-MiniLM-L6-v2";

    /// <summary>Load the model and tokenizer from a directory holding <c>model.onnx</c> and <c>tokenizer.json</c>.</summary>
    public static async Task<OnnxEmbedder> LoadAsync(string modelDir)
    {
        var session = new InferenceSession(Path.Combine(modelDir, "model.onnx"));
        var tokenizer = new BertTokenizer();
        await tokenizer.LoadTokenizerJsonAsync(Path.Combine(modelDir, "tokenizer.json"));
        return new OnnxEmbedder(session, tokenizer);
    }

    // Rows per ONNX Run. Batching many sequences into a single inference is the dominant
    // throughput win on CPU; the sub-batch bounds peak tensor memory and gives cancellation a
    // checkpoint between Runs.
    private const int SubBatch = 32;

    /// <inheritdoc/>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => (await EmbedBatchAsync([text ?? string.Empty], ct))[0];

    /// <inheritdoc/>
    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new float[texts.Count][];
        for (var start = 0; start < texts.Count; start += SubBatch)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(SubBatch, texts.Count - start);
            EmbedSubBatch(texts, start, count, result);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(result);
    }

    /// <summary>Release the inference session.</summary>
    public void Dispose() => _session.Dispose();

    // Tokenize a contiguous slice of texts, stack them into a single [count, maxLen] batch padded
    // to the slice's true maximum token length, run one inference, and mean-pool each row. Padded
    // positions carry attention-mask 0, so trimming to the batch maximum yields the same pooled
    // vectors as embedding each text alone (the model masks padding out of attention).
    private void EmbedSubBatch(IReadOnlyList<string> texts, int start, int count, float[][] outArr)
    {
        var rowIds = new long[count][];
        var rowMask = new long[count][];
        var rowTypes = _tokenTypeIds is null ? null : new long[count][];
        var maxLen = 1;

        for (var r = 0; r < count; r++)
        {
            var (ids, mask, types) = _tokenizer.Encode(texts[start + r] ?? string.Empty, MaxTokens);
            rowIds[r] = ids.ToArray();
            rowMask[r] = mask.ToArray();
            if (rowTypes is not null)
            {
                rowTypes[r] = types.ToArray();
            }

            var trueLen = 0;
            for (var t = 0; t < rowMask[r].Length; t++)
            {
                if (rowMask[r][t] != 0)
                {
                    trueLen = t + 1;
                }
            }

            maxLen = Math.Max(maxLen, trueLen);
        }

        var idsT = new DenseTensor<long>([count, maxLen]);
        var maskT = new DenseTensor<long>([count, maxLen]);
        var typesT = rowTypes is null ? null : new DenseTensor<long>([count, maxLen]);
        for (var r = 0; r < count; r++)
        {
            for (var t = 0; t < maxLen; t++)
            {
                idsT[r, t] = t < rowIds[r].Length ? rowIds[r][t] : 0;
                maskT[r, t] = t < rowMask[r].Length ? rowMask[r][t] : 0;
                if (typesT is not null)
                {
                    typesT[r, t] = t < rowTypes![r].Length ? rowTypes[r][t] : 0;
                }
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIds, idsT),
            NamedOnnxValue.CreateFromTensor(_attentionMask, maskT),
        };
        if (typesT is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIds!, typesT));
        }

        using var results = _session.Run(inputs);
        var hidden = results[0].AsTensor<float>(); // [count, maxLen, 384]
        for (var r = 0; r < count; r++)
        {
            outArr[start + r] = MeanPoolAndNormalize(hidden, r, rowMask[r], maxLen);
        }
    }

    private float[] MeanPoolAndNormalize(Tensor<float> tokens, int row, long[] mask, int len)
    {
        var pooled = new float[Dimension];
        float count = 0;
        for (var t = 0; t < len; t++)
        {
            if (t >= mask.Length || mask[t] == 0)
            {
                continue;
            }

            count++;
            for (var d = 0; d < Dimension; d++)
            {
                pooled[d] += tokens[row, t, d];
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
