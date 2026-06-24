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

    /// <inheritdoc/>
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var (ids, mask, types) = _tokenizer.Encode(text ?? string.Empty, MaxTokens);
        var len = ids.Length;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIds, new DenseTensor<long>(ids.ToArray(), [1, len])),
            NamedOnnxValue.CreateFromTensor(_attentionMask, new DenseTensor<long>(mask.ToArray(), [1, len])),
        };
        if (_tokenTypeIds is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIds, new DenseTensor<long>(types.ToArray(), [1, len])));
        }

        using var results = _session.Run(inputs);
        var hidden = results[0].AsTensor<float>(); // [1, len, 384]
        return Task.FromResult(MeanPoolAndNormalize(hidden, mask.ToArray(), len));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>(texts.Count);
        foreach (var t in texts)
        {
            ct.ThrowIfCancellationRequested();
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
