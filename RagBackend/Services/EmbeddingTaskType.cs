// nomic-embed-text richiede un prefisso di istruzione diverso per testo indicizzato
// e per query di ricerca: senza questo prefisso la qualità degli embedding degrada
// pesantemente e la ricerca semantica diventa poco discriminante.
// https://huggingface.co/nomic-ai/nomic-embed-text-v1.5
public enum EmbeddingTaskType
{
    Document,
    Query
}
