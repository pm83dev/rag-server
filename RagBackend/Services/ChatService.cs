using System.Text;
using System.Text.Json;

public class ChatService : IChatService, IDisposable
{
    private readonly HttpClient _http;

    public ChatService(IConfiguration config)
    {
        var chatUrl = config["LlamaCppChatServer:Url"] ?? "http://localhost:9000";
        // Alzato dal default di 100s: con contesti grandi e risposte lunghe il modello può metterci di più.
        _http = new HttpClient { BaseAddress = new Uri(chatUrl), Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<string> AskAsync(string question, string context)
    {
        var systemPrompt =
            "Rispondi alla domanda dell'utente usando prima di tutto il contesto fornito qui sotto, " +
            "estratto dai documenti caricati dall'utente: è la fonte più affidabile e specifica per questo caso. " +
            "Se il contesto non copre completamente la domanda, integra la risposta con le tue conoscenze generali " +
            "per renderla utile e completa, indicando chiaramente quali informazioni provengono dal documento e " +
            "quali dalla tua conoscenza generale (es. \"Il documento indica... In generale, per questo tipo di prodotto...\").\n\n" +
            $"Contesto dal documento:\n{context}";

        var body = JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = question }
            }
        });

        var response = await _http.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseContent);

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    public void Dispose()
    {
        _http?.Dispose();
    }
}
