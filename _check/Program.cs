using Microsoft.ML.OnnxRuntime;

var session = new InferenceSession(@"C:\DEV\PM_RAG_SERVER\rag-server\RagBackend\models\all-MiniLM-L6-v2.onnx");
Console.WriteLine("=== INPUTS ===");
foreach (var name in session.InputMetadata.Keys)
    Console.WriteLine($"  Input: {name}");
Console.WriteLine("=== OUTPUTS ===");
foreach (var name in session.OutputMetadata.Keys)
    Console.WriteLine($"  Output: {name} -> dims={string.Join('x', session.OutputMetadata[name].Dimensions)}");
