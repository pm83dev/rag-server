using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Linq;

public class EmbeddingService : IEmbeddingService, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private const int MaxChunkSize = 512; // llama.cpp batch size limit

    public EmbeddingService(IConfiguration config)
    {
        var llamaUrl = config["LlamaCppEmbeddingServer:Url"] ?? "http://localhost:1337";
        // Il default di 100s di HttpClient non basta per una chiamata batch con molti sotto-chunk
        // (documenti grandi indicizzati in background, dove il tempo non impatta la richiesta HTTP dell'utente).
        _http = new HttpClient { BaseAddress = new Uri(llamaUrl), Timeout = TimeSpan.FromMinutes(20) };
        _model = config["EmbeddingModel:Name"] ?? "nomic-embed-text-v1.5";
    }

    // nomic-embed-text distingue tra testo indicizzato e query di ricerca tramite un prefisso
    // di istruzione: ometterlo degrada pesantemente la qualità (e quindi la pertinenza) della
    // ricerca semantica. https://huggingface.co/nomic-ai/nomic-embed-text-v1.5
    private static string PrefixFor(EmbeddingTaskType taskType) =>
        taskType == EmbeddingTaskType.Query ? "search_query: " : "search_document: ";

    public async Task<float[]> GenerateAsync(string text, EmbeddingTaskType taskType = EmbeddingTaskType.Document)
    {
        var prefix = PrefixFor(taskType);

        // Chunk large texts to stay within batch size limits
        var chunks = ChunkText(text, MaxChunkSize).Select(c => prefix + c).ToList();

        if (chunks.Count == 1)
        {
            return await GenerateEmbeddingForSingle(chunks[0]);
        }

        // Average embeddings from all chunks
        var aggregated = await GenerateEmbeddingForSingle(chunks[0]);
        int embeddingDimensions = aggregated.Length;

        for (int c = 1; c < chunks.Count; c++)
        {
            var embedding = await GenerateEmbeddingForSingle(chunks[c]);
            for (int i = 0; i < embeddingDimensions; i++)
                aggregated[i] += embedding[i];
        }

        // Normalize by number of chunks
        for (int i = 0; i < embeddingDimensions; i++)
            aggregated[i] /= chunks.Count;

        return aggregated;
    }

    private async Task<float[]> GenerateEmbeddingForSingle(string text)
    {
        var body = JsonSerializer.Serialize(new { model = _model, input = text });
        var response = await _http.PostAsync("/embeddings", new StringContent(body, Encoding.UTF8, "application/json"));
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"llama.cpp embedding server returned {(int)response.StatusCode}: {content}");

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // OpenAI-compatible wrapper: {"data":[{"embedding":[...]}],"model":...}
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataArray))
        {
            return ExtractEmbeddingArray(dataArray[0].GetProperty("embedding"));
        }

        // llama.cpp native format: [{"index":0,"embedding":[[...]]}]
        if (root.ValueKind == JsonValueKind.Array)
        {
            var firstElement = root.EnumerateArray().First();
            if (firstElement.TryGetProperty("embedding", out var emb))
            {
                return ExtractEmbeddingArray(emb);
            }
        }

        throw new InvalidOperationException($"Unknown embedding response format: {content}");
    }

    // llama.cpp's native endpoint nests il vettore un livello più in profondità ([[...]]) di
    // quello OpenAI-compatible ([...]). Se il server non fa pooling lato suo (manca --pooling
    // mean/cls, o il modello non lo dichiara nei metadati GGUF), quel livello annidato contiene
    // un embedding PER TOKEN invece di un unico vettore aggregato: in quel caso facciamo noi la
    // media sui token, altrimenti prendere solo la prima riga darebbe un vettore quasi costante
    // (dominato dal primo token, es. il prefisso "search_document:"/"search_query:" condiviso da
    // tutti i testi), rendendo la ricerca semantica insensibile al contenuto reale.
    private static float[] ExtractEmbeddingArray(JsonElement embeddingElement)
    {
        var first = embeddingElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Array)
        {
            return embeddingElement.EnumerateArray().Select(e => e.GetSingle()).ToArray();
        }

        var rows = embeddingElement.EnumerateArray().ToList();
        if (rows.Count == 1)
        {
            return rows[0].EnumerateArray().Select(e => e.GetSingle()).ToArray();
        }

        var dims = rows[0].GetArrayLength();
        var mean = new float[dims];
        foreach (var row in rows)
        {
            var i = 0;
            foreach (var value in row.EnumerateArray())
                mean[i++] += value.GetSingle();
        }
        for (var i = 0; i < dims; i++)
            mean[i] /= rows.Count;

        return mean;
    }

    /// <summary>
    /// Splits text into chunks that fit within maxTokens tokenizer tokens.
    /// Uses character-level chunking with overlap as a simple approach.
    /// </summary>
    private List<string> ChunkText(string text, int maxTokens)
    {
        var chunks = new List<string>();

        // Simple word-based chunking targeting ~250 chars per chunk
        // This is a heuristic that typically corresponds to ~150-200 tokens
        var chunkSize = Math.Min(MaxChunkSize, text.Length);
        var overlap = MaxChunkSize / 4; // 25% overlap to preserve context

        if (text.Length <= chunkSize)
        {
            chunks.Add(text);
            return chunks;
        }

        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + chunkSize, text.Length);

            // Try to break at word boundary
            if (end < text.Length)
            {
                var searchStart = Math.Min(end + 10, text.Length - 1);
                var spaceIndex = text.LastIndexOf(' ', searchStart, Math.Min(20, searchStart + 1));
                if (spaceIndex > start)
                    end = spaceIndex;

                // Try to break at sentence boundary
                var sentenceEnds = new[] { '.', '!', '?' };
                for (int i = end; i < Math.Min(end + 50, text.Length); i++)
                {
                    if (sentenceEnds.Contains(text[i]))
                    {
                        end = i + 1;
                        break;
                    }
                }
            }

            var chunk = text.Substring(start, end - start).Trim();
            if (!string.IsNullOrEmpty(chunk))
                chunks.Add(chunk);

            if (end >= text.Length)
                break; // reached the end of the text, stop

            // Move forward with overlap, guaranteeing forward progress even when
            // the word/sentence boundary adjustment shrank this chunk to <= overlap in size
            var nextStart = end - overlap;
            start = nextStart > start ? nextStart : end;
        }

        return chunks;
    }

    public async Task<List<float[]>> GenerateBatchAsync(IEnumerable<string> texts, EmbeddingTaskType taskType = EmbeddingTaskType.Document)
    {
        var list = texts.ToList();
        var prefix = PrefixFor(taskType);

        // Ogni testo può superare il limite di token del modello (come in GenerateAsync): lo
        // suddividiamo in sotto-chunk, mandiamo TUTTI i sotto-chunk di TUTTI i testi in un'unica
        // chiamata batch, poi riaggreghiamo (media) per ricostruire un vettore per testo originale.
        var allSubChunks = new List<string>();
        var subChunkCounts = new List<int>();

        foreach (var text in list)
        {
            var subChunks = ChunkText(text, MaxChunkSize).Select(c => prefix + c).ToList();
            subChunkCounts.Add(subChunks.Count);
            allSubChunks.AddRange(subChunks);
        }

        var allVectors = await GenerateBatchRaw(allSubChunks);

        var result = new List<float[]>();
        var offset = 0;
        foreach (var count in subChunkCounts)
        {
            if (count == 1)
            {
                result.Add(allVectors[offset]);
            }
            else
            {
                var dims = allVectors[offset].Length;
                var aggregated = new float[dims];
                for (var c = 0; c < count; c++)
                    for (var d = 0; d < dims; d++)
                        aggregated[d] += allVectors[offset + c][d];
                for (var d = 0; d < dims; d++)
                    aggregated[d] /= count;
                result.Add(aggregated);
            }
            offset += count;
        }

        return result;
    }

    private async Task<List<float[]>> GenerateBatchRaw(List<string> texts)
    {
        var body = JsonSerializer.Serialize(new { model = _model, input = texts });
        var response = await _http.PostAsync("/embeddings", new StringContent(body, Encoding.UTF8, "application/json"));
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"llama.cpp embedding server returned {(int)response.StatusCode}: {content}");

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // OpenAI-compatible wrapper: {"data":[{"embedding":[...],...},...]}
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataArray))
        {
            var result = new List<float[]>();
            foreach (var data in dataArray.EnumerateArray())
                result.Add(ExtractEmbeddingArray(data.GetProperty("embedding")));
            return result;
        }

        // llama.cpp native format: [{"index":0,"embedding":[[...]]},...]
        if (root.ValueKind == JsonValueKind.Array)
        {
            var result = new List<float[]>();
            foreach (var data in root.EnumerateArray())
            {
                if (data.TryGetProperty("embedding", out var emb))
                    result.Add(ExtractEmbeddingArray(emb));
            }
            return result;
        }

        throw new InvalidOperationException($"Unknown embedding response format: {content}");
    }

    public void Dispose()
    {
        _http?.Dispose();
    }
}