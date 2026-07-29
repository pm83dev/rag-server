using System.Collections.Generic;
using System.Threading.Tasks;

public interface IVectorStoreService
{
    /// <summary>
    /// Salva un chunk di testo con il suo embedding in Qdrant.
    /// </summary>
    Task UpsertAsync(string collectionName, string id, float[] vector, Dictionary<string, object> metadata);

    /// <summary>
    /// Salva più chunk in un'unica chiamata batch (molto più veloce di upsert singoli in sequenza).
    /// </summary>
    Task UpsertBatchAsync(string collectionName, IEnumerable<(string Id, float[] Vector, Dictionary<string, object> Metadata)> points);

    /// <summary>
    /// Cerca i chunk più simili a un vettore query.
    /// </summary>
    Task<List<(string Id, double Score, Dictionary<string, object> Metadata)>> SearchAsync(string collectionName, float[] queryVector, int limit = 5, string? source = null);

    /// <summary>
    /// Cerca i chunk più simili per ciascun documento indicizzato e unisce i risultati, per evitare
    /// che un documento con molti più chunk monopolizzi il risultato a scapito di uno più piccolo.
    /// </summary>
    Task<List<(string Id, double Score, Dictionary<string, object> Metadata)>> SearchAcrossSourcesAsync(string collectionName, float[] queryVector, string? queryText = null, int perSourceLimit = 3);

    /// <summary>
    /// Elenca i documenti (fonti) indicizzati con il conteggio dei chunk per ciascuno.
    /// </summary>
    Task<Dictionary<string, int>> ListSourcesAsync(string collectionName);

    /// <summary>
    /// Elimina tutti i chunk provenienti da un dato documento (campo "source").
    /// </summary>
    Task<ulong> DeleteBySourceAsync(string collectionName, string source);
}
