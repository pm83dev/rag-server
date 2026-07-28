using System.Collections.Generic;
using System.Threading.Tasks;

public interface IVectorStoreService
{
    /// <summary>
    /// Salva un chunk di testo con il suo embedding in Qdrant.
    /// </summary>
    Task UpsertAsync(string collectionName, string id, float[] vector, Dictionary<string, object> metadata);

    /// <summary>
    /// Cerca i chunk più simili a un vettore query.
    /// </summary>
    Task<List<(string Id, double Score, Dictionary<string, object> Metadata)>> SearchAsync(string collectionName, float[] queryVector, int limit = 5);

    /// <summary>
    /// Elenca i documenti (fonti) indicizzati con il conteggio dei chunk per ciascuno.
    /// </summary>
    Task<Dictionary<string, int>> ListSourcesAsync(string collectionName);

    /// <summary>
    /// Elimina tutti i chunk provenienti da un dato documento (campo "source").
    /// </summary>
    Task<ulong> DeleteBySourceAsync(string collectionName, string source);
}
