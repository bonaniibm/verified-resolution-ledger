using System.Text;
using System.Text.RegularExpressions;

namespace Vrl.Core.Similarity;

public interface IEmbeddingProvider
{
    /// <summary>Identifier persisted with each embedding so vectors from different models are never compared.</summary>
    string ModelId { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public static class VectorMath
{
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public static float[] Normalize(float[] v)
    {
        double n = 0;
        foreach (var x in v) n += x * x;
        if (n == 0) return v;
        var inv = (float)(1.0 / Math.Sqrt(n));
        for (var i = 0; i < v.Length; i++) v[i] *= inv;
        return v;
    }

    /// <summary>Compact persistence format (base64 of little-endian float32) for Dataverse memo columns.</summary>
    public static string ToBase64(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static float[]? FromBase64(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            var bytes = Convert.FromBase64String(s);
            if (bytes.Length % sizeof(float) != 0) return null;
            var v = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
            return v;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Dependency-free embedding using feature hashing of normalised unigrams and bigrams.
/// Good enough to detect near-duplicate problem descriptions in a sandbox without Azure OpenAI;
/// swap for <c>AzureOpenAIEmbeddingProvider</c> (Vrl.AI) in production for paraphrase robustness.
/// </summary>
public sealed partial class LocalHashingEmbeddingProvider : IEmbeddingProvider
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a","an","the","and","or","but","if","of","to","in","on","for","with","at","by","from","is","are","was","were",
        "be","been","it","its","this","that","these","those","i","me","my","we","our","you","your","he","she","they",
        "them","their","customer","agent","representative","called","call","chat","asked","about","regarding","has","have",
        "had","not","no","yes","please","thanks","thank","can","could","would","will","did","do","does","so","as","again",
        "still","because","want","wants","wanted","said","says","told","get","got",
    };

    private readonly int _dimensions;

    public LocalHashingEmbeddingProvider(int dimensions = 512) => _dimensions = dimensions;

    public string ModelId => $"local-hash-{_dimensions}-v1";

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Embed(text));

    public float[] Embed(string text)
    {
        var v = new float[_dimensions];
        var tokens = Tokenize(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            Add(v, tokens[i], 1.0f);
            if (i + 1 < tokens.Count) Add(v, tokens[i] + "_" + tokens[i + 1], 0.7f);
        }
        return VectorMath.Normalize(v);
    }

    private void Add(float[] v, string feature, float weight)
    {
        var h = Fnv1a(feature);
        var idx = (int)(h % (uint)_dimensions);
        // Sign bit from a different part of the hash reduces collision bias.
        v[idx] += ((h >> 31) & 1) == 0 ? weight : -weight;
    }

    internal static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        foreach (Match m in WordRegex().Matches(text.ToLowerInvariant()))
        {
            var w = m.Value;
            if (w.Length < 2 || StopWords.Contains(w)) continue;
            result.Add(Stem(w));
        }
        return result;
    }

    /// <summary>Very light suffix stripping – deliberately conservative.</summary>
    private static string Stem(string w)
    {
        foreach (var suffix in new[] { "ing", "ed", "es", "s" })
            if (w.Length > suffix.Length + 3 && w.EndsWith(suffix, StringComparison.Ordinal))
                return w[..^suffix.Length];
        return w;
    }

    private static uint Fnv1a(string s)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}
