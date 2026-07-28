using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public class VectorStoreService : IVectorStoreService
{
    private readonly QdrantClient _client;

    public VectorStoreService(QdrantClient client)
    {
        _client = client;
    }

    public async Task UpsertAsync(string collectionName, string id, float[] vector, Dictionary<string, object> metadata)
    {
        var collections = await _client.ListCollectionsAsync();
        if (!collections.Any(c => c == collectionName))
        {
            await _client.CreateCollectionAsync(
                collectionName,
                new VectorParams { Size = (uint)vector.Length, Distance = Distance.Cosine });
        }

        var point = new PointStruct
        {
            Id = Guid.Parse(id),
            Vectors = vector
        };
        foreach (var kvp in metadata)
        {
            point.Payload[kvp.Key] = kvp.Value?.ToString() ?? "";
        }

        await _client.UpsertAsync(collectionName, new[] { point });
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

    public async Task<List<(string Id, double Score, Dictionary<string, object> Metadata)>> SearchAsync(string collectionName, float[] queryVector, int limit = 5)
    {
        var results = await _client.SearchAsync(
            collectionName,
            queryVector,
            limit: (ulong)limit);

        return results.Select(r => (
            Id: r.Id.ToString(),
            Score: (double)r.Score,
            Metadata: PayloadToDict(r.Payload)
        )).ToList();
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
