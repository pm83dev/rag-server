using System.Collections.Generic;
using System.Threading.Tasks;

public interface IEmbeddingService
{
    /// <summary>
    /// Genera un vettore di embedding per un singolo testo.
    /// </summary>
    Task<float[]> GenerateAsync(string text);

    /// <summary>
    /// Genera vettori per una lista di testi (batch).
    /// </summary>
    Task<List<float[]>> GenerateBatchAsync(IEnumerable<string> texts);
}