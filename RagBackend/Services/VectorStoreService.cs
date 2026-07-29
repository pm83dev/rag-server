using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public class VectorStoreService : IVectorStoreService
{
    private readonly QdrantClient _client;

    // Evita di richiamare ListCollectionsAsync (un round-trip di rete) ad ogni singolo upsert:
    // una volta verificata/creata la collection in questo processo, non serve ricontrollarla.
    private readonly HashSet<string> _ensuredCollections = new();
    private readonly SemaphoreSlim _ensureLock = new(1, 1);

    public VectorStoreService(QdrantClient client)
    {
        _client = client;
    }

    private async Task EnsureCollectionAsync(string collectionName, uint vectorSize)
    {
        if (_ensuredCollections.Contains(collectionName))
            return;

        await _ensureLock.WaitAsync();
        try
        {
            if (_ensuredCollections.Contains(collectionName))
                return;

            var collections = await _client.ListCollectionsAsync();
            if (!collections.Any(c => c == collectionName))
            {
                await _client.CreateCollectionAsync(
                    collectionName,
                    new VectorParams { Size = vectorSize, Distance = Distance.Cosine });
            }

            // Indice full-text sul contenuto: la ricerca puramente vettoriale ha poco potere
            // discriminante su testo enumerativo denso (es. cataloghi di codici), dove tutti i
            // chunk ottengono punteggi di similarità simili indipendentemente dal contenuto reale.
            // Con questo indice possiamo garantire che una parola chiave letterale (es. "legno")
            // venga sempre trovata, anche quando il suo punteggio vettoriale non la fa emergere.
            try
            {
                await _client.CreatePayloadIndexAsync(collectionName, "content", PayloadSchemaType.Text);
            }
            catch
            {
                // indice già esistente (collection creata da un processo precedente): non bloccante
            }

            _ensuredCollections.Add(collectionName);
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    private static PointStruct ToPoint(string id, float[] vector, Dictionary<string, object> metadata)
    {
        var point = new PointStruct
        {
            Id = Guid.Parse(id),
            Vectors = vector
        };
        foreach (var kvp in metadata)
        {
            point.Payload[kvp.Key] = kvp.Value?.ToString() ?? "";
        }
        return point;
    }

    public async Task UpsertAsync(string collectionName, string id, float[] vector, Dictionary<string, object> metadata)
    {
        await EnsureCollectionAsync(collectionName, (uint)vector.Length);
        await _client.UpsertAsync(collectionName, new[] { ToPoint(id, vector, metadata) });
    }

    public async Task UpsertBatchAsync(string collectionName, IEnumerable<(string Id, float[] Vector, Dictionary<string, object> Metadata)> points)
    {
        var pointList = points.ToList();
        if (pointList.Count == 0)
            return;

        await EnsureCollectionAsync(collectionName, (uint)pointList[0].Vector.Length);
        var qdrantPoints = pointList.Select(p => ToPoint(p.Id, p.Vector, p.Metadata)).ToList();
        await _client.UpsertAsync(collectionName, qdrantPoints);
    }

    public async Task<Dictionary<string, int>> ListSourcesAsync(string collectionName)
    {
        var counts = new Dictionary<string, int>();
        PointId? offset = null;

        while (true)
        {
            var response = await _client.ScrollAsync(
                collectionName,
                limit: 200,
                offset: offset,
                payloadSelector: new[] { "source" });

            foreach (var point in response.Result)
            {
                if (point.Payload.TryGetValue("source", out var value) && value.KindCase == Value.KindOneofCase.StringValue)
                {
                    var source = value.StringValue;
                    counts[source] = counts.GetValueOrDefault(source) + 1;
                }
            }

            if (response.Result.Count == 0 || response.NextPageOffset == null)
                break;
            offset = response.NextPageOffset;
        }

        return counts;
    }

    public async Task<ulong> DeleteBySourceAsync(string collectionName, string source)
    {
        var filter = new Filter { Must = { Conditions.MatchKeyword("source", source) } };

        var count = await _client.CountAsync(collectionName, filter);
        await _client.DeleteAsync(collectionName, filter);

        return count;
    }

    public async Task<List<(string Id, double Score, Dictionary<string, object> Metadata)>> SearchAsync(string collectionName, float[] queryVector, int limit = 5, string? source = null)
    {
        await EnsureCollectionAsync(collectionName, (uint)queryVector.Length);
        var results = await _client.SearchAsync(
            collectionName,
            queryVector,
            filter: source is null ? null : new Filter { Must = { Conditions.MatchKeyword("source", source) } },
            limit: (ulong)limit);

        return results.Select(r => (
            Id: r.Id.ToString(),
            Score: (double)r.Score,
            Metadata: PayloadToDict(r.Payload)
        )).ToList();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "che", "chi", "cosa", "quale", "quali", "come", "dove", "quando", "perche", "perché",
        "con", "del", "della", "dei", "delle", "dello", "degli", "dal", "dalla", "dai", "dalle",
        "nel", "nella", "nei", "nelle", "sono", "essere", "avere", "questo", "questa", "questi",
        "queste", "quello", "quella", "quelli", "quelle", "per", "una", "uno", "gli", "cioè"
    };

    // Parole di almeno 4 lettere, esclusi articoli/preposizioni/pronomi: usate per il fallback
    // lessicale in SearchAcrossSourcesAsync.
    private static List<string> ExtractKeywords(string query) =>
        query.Split(new[] { ' ', ',', '.', '?', '!', ';', ':' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3 && !StopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Ricerca il top-N per ciascun documento indicizzato e unisce i risultati, invece del top-N
    // globale: con corpus fortemente sbilanciati (un documento con molte più pagine di un altro)
    // il top-N globale è quasi sempre monopolizzato dal documento più grande anche quando quello
    // piccolo contiene il chunk realmente pertinente, perché il margine di similarità coseno tra
    // testo pertinente e non pertinente può essere molto stretto.
    //
    // In aggiunta, per ogni documento cerchiamo anche i chunk che contengono letteralmente una
    // delle parole chiave della domanda (full-text match): su testo enumerativo denso (es.
    // cataloghi di codici) la similarità vettoriale da sola discrimina poco, quindi un match
    // lessicale esatto potrebbe non emergere mai nel solo top-N vettoriale.
    public async Task<List<(string Id, double Score, Dictionary<string, object> Metadata)>> SearchAcrossSourcesAsync(
        string collectionName, float[] queryVector, string? queryText = null, int perSourceLimit = 3)
    {
        await EnsureCollectionAsync(collectionName, (uint)queryVector.Length);

        var sources = await ListSourcesAsync(collectionName);
        if (sources.Count == 0)
            return new List<(string, double, Dictionary<string, object>)>();

        var keywords = queryText is null ? new List<string>() : ExtractKeywords(queryText);

        var perSourceResults = await Task.WhenAll(sources.Keys.Select(async source =>
        {
            var sourceFilter = new Filter { Must = { Conditions.MatchKeyword("source", source) } };
            var results = await _client.SearchAsync(
                collectionName,
                queryVector,
                filter: sourceFilter,
                limit: (ulong)perSourceLimit);

            var combined = results.AsEnumerable();

            if (keywords.Count > 0)
            {
                var keywordFilter = new Filter
                {
                    Must = { Conditions.MatchKeyword("source", source) },
                    Should = { keywords.Select(k => Conditions.MatchText("content", k)) }
                };
                var keywordResults = await _client.SearchAsync(
                    collectionName,
                    queryVector,
                    filter: keywordFilter,
                    limit: (ulong)perSourceLimit);

                combined = combined.Concat(keywordResults).GroupBy(r => r.Id).Select(g => g.First());
            }

            return combined.Select(r => (
                Id: r.Id.ToString(),
                Score: (double)r.Score,
                Metadata: PayloadToDict(r.Payload)
            ));
        }));

        return perSourceResults.SelectMany(r => r).OrderByDescending(r => r.Score).ToList();
    }

    private static Dictionary<string, object> PayloadToDict(Google.Protobuf.Collections.MapField<string, Value> payload)
    {
        var dict = new Dictionary<string, object>();
        if (payload == null) return dict;
        foreach (var kvp in payload)
        {
            dict[kvp.Key] = kvp.Value.KindCase switch
            {
                Value.KindOneofCase.StringValue => kvp.Value.StringValue,
                Value.KindOneofCase.IntegerValue => kvp.Value.IntegerValue,
                Value.KindOneofCase.DoubleValue => kvp.Value.DoubleValue,
                Value.KindOneofCase.BoolValue => kvp.Value.BoolValue,
                _ => kvp.Value.ToString()
            };
        }
        return dict;
    }
}
