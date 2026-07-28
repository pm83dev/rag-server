using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Ollama;

var gen = new OllamaEmbeddingGenerator("http://localhost:11434", "nomic-embed-text");
Console.WriteLine(gen.GetType().FullName);
