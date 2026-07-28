using Qdrant.Client;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// 1. Configura Qdrant Client
builder.Services.AddSingleton<QdrantClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var qdrantUrl = config["Qdrant:Url"];
    if (!string.IsNullOrEmpty(qdrantUrl) && Uri.TryCreate(qdrantUrl, UriKind.Absolute, out var uri))
        return new QdrantClient(uri.Host, port: uri.Port);
    return new QdrantClient("localhost", port: 6333);
});

// 2. Configura Embedding Service con llama.cpp (OpenAI-compatible API)
// Nota: non usare AddHttpClient qui perché EmbeddingService crea il proprio HttpClient internamente
builder.Services.AddSingleton<IEmbeddingService, EmbeddingService>();
builder.Services.AddSingleton<IVectorStoreService, VectorStoreService>();

// 3. Configura Chat Service con llama.cpp (OpenAI-compatible /v1/chat/completions)
builder.Services.AddSingleton<IChatService, ChatService>();

// 3b. Coda di lavori in background per l'indicizzazione asincrona dei documenti
// (normative molto grandi impiegherebbero troppo per stare dentro una singola richiesta HTTP/proxy).
builder.Services.AddSingleton<IJobStore, JobStore>();
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddHostedService<QueuedHostedService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Alza il limite di dimensione del body multipart (default 128MB) per permettere PDF grandi.
// Il limite lato IIS (default ~28-30MB) è alzato in web.config (system.webServer/security/requestFiltering).
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 200 * 1024 * 1024; // 200 MB
});

// 4. CORS: consente al frontend Angular (servito su un'origine diversa, es. IIS) di chiamare queste API.
// Le origini consentite si configurano in appsettings.json sotto "Cors:AllowedOrigins".
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "*" };
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        if (allowedOrigins.Contains("*"))
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        else
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors("Frontend");

// Swagger abilitato sempre (anche in Production): questo backend è uno strumento di test interno,
// non un'API pubblica, quindi la UI di prova resta comoda anche fuori da Development.
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "RagBackend v1"));

// Endpoint di test per verificare l'indicizzazione
app.MapPost("/api/index", async (string content, IVectorStoreService vectorStore, IEmbeddingService embedding) =>
{
    var chunks = content.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

    foreach (var chunk in chunks)
    {
        if (string.IsNullOrWhiteSpace(chunk)) continue;

        var vector = await embedding.GenerateAsync(chunk);

        await vectorStore.UpsertAsync(
            collectionName: "documents",
            id: Guid.NewGuid().ToString(),
            vector: vector,
            metadata: new Dictionary<string, object> { { "content", chunk } }
        );
    }

    return Results.Ok($"Indicizzati {chunks.Length} chunk.");
});

app.MapPost("/api/upload-pdf", async (IFormFile file, IVectorStoreService vectorStore, IEmbeddingService embedding, IJobStore jobStore, IBackgroundTaskQueue taskQueue) =>
{
    if (file.Length == 0)
        return Results.BadRequest("Il file è vuoto.");

    if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("Solo file PDF sono supportati.");

    // Copia subito il contenuto: lo stream di IFormFile viene chiuso non appena la richiesta termina,
    // ma l'elaborazione vera e propria avviene dopo, in background.
    var pdfBytes = new MemoryStream();
    await file.CopyToAsync(pdfBytes);
    pdfBytes.Position = 0;

    var job = jobStore.Create(file.FileName);

    taskQueue.QueueBackgroundWorkItem(async _ =>
    {
        try
        {
            jobStore.Update(job.Id, j => j.Status = JobStatus.Processing);

            string text;
            using (pdfBytes)
            using (var document = UglyToad.PdfPig.PdfDocument.Open(pdfBytes))
            {
                var pages = new List<string>();
                foreach (var page in document.GetPages())
                    pages.Add(page.Text);
                text = string.Join("\n\n", pages);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                jobStore.Update(job.Id, j =>
                {
                    j.Status = JobStatus.Failed;
                    j.ErrorMessage = "Il PDF non contiene testo estratto.";
                });
                return;
            }

            var chunks = SplitText(text, maxChunkSize: 500, overlap: 50);
            jobStore.Update(job.Id, j => j.TotalChunks = chunks.Length);

            const int batchSize = 20;
            for (var i = 0; i < chunks.Length; i += batchSize)
            {
                var batch = chunks.Skip(i).Take(batchSize).ToList();
                var vectors = await embedding.GenerateBatchAsync(batch);

                for (var b = 0; b < batch.Count; b++)
                {
                    await vectorStore.UpsertAsync(
                        collectionName: "documents",
                        id: Guid.NewGuid().ToString(),
                        vector: vectors[b],
                        metadata: new Dictionary<string, object>
                        {
                            { "content", batch[b] },
                            { "source", job.FileName }
                        });
                }

                jobStore.Update(job.Id, j => j.ProcessedChunks += batch.Count);
            }

            jobStore.Update(job.Id, j => j.Status = JobStatus.Completed);
        }
        catch (Exception ex)
        {
            jobStore.Update(job.Id, j =>
            {
                j.Status = JobStatus.Failed;
                j.ErrorMessage = ex.Message;
            });
        }
    });

    return Results.Accepted($"/api/upload-status/{job.Id}", new { jobId = job.Id });
})
.DisableAntiforgery()
.Accepts<IFormFile>("multipart/form-data");

app.MapGet("/api/upload-status/{jobId}", (string jobId, IJobStore jobStore) =>
{
    var job = jobStore.Get(jobId);
    if (job is null)
        return Results.NotFound();

    return Results.Ok(new
    {
        jobId = job.Id,
        fileName = job.FileName,
        status = job.Status.ToString(),
        totalChunks = job.TotalChunks,
        processedChunks = job.ProcessedChunks,
        errorMessage = job.ErrorMessage
    });
});

app.MapGet("/api/search", async (string query, IVectorStoreService vectorStore, IEmbeddingService embedding) =>
{
    var vector = await embedding.GenerateAsync(query);
    var results = await vectorStore.SearchAsync("documents", vector, limit: 5);
    return Results.Ok(results.Select(r => new { r.Id, r.Score, r.Metadata }));
});

app.MapGet("/api/ask", async (string question, IVectorStoreService vectorStore, IEmbeddingService embedding, IChatService chat) =>
{
    var vector = await embedding.GenerateAsync(question);
    var results = await vectorStore.SearchAsync("documents", vector, limit: 3);

    if (results.Count == 0)
        return Results.Ok(new { answer = "Nessun documento indicizzato è rilevante per questa domanda.", sources = Array.Empty<object>() });

    var context = string.Join("\n\n---\n\n", results.Select(r => r.Metadata.GetValueOrDefault("content", "")));
    var answer = await chat.AskAsync(question, context);

    return Results.Ok(new
    {
        answer,
        sources = results.Select(r => new { r.Id, r.Score })
    });
});

app.MapGet("/api/documents", async (IVectorStoreService vectorStore) =>
{
    var sources = await vectorStore.ListSourcesAsync("documents");
    return Results.Ok(sources.Select(kvp => new { source = kvp.Key, chunks = kvp.Value }));
});

app.MapDelete("/api/documents/{source}", async (string source, IVectorStoreService vectorStore) =>
{
    var deleted = await vectorStore.DeleteBySourceAsync("documents", source);
    if (deleted == 0)
        return Results.NotFound($"Nessun chunk trovato per il documento '{source}'.");

    return Results.Ok($"Eliminati {deleted} chunk del documento '{source}'.");
});

app.Run();

static string[] SplitText(string text, int maxChunkSize = 500, int overlap = 50)
{
    var chunks = new List<string>();
    if (string.IsNullOrWhiteSpace(text)) return chunks.ToArray();

    var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
    var currentChunk = "";

    foreach (var paragraph in paragraphs)
    {
        var candidate = string.IsNullOrEmpty(currentChunk) ? paragraph : currentChunk + "\n\n" + paragraph;
        if (candidate.Length > maxChunkSize && !string.IsNullOrEmpty(currentChunk))
        {
            chunks.Add(currentChunk);
            var tail = currentChunk.Length > overlap ? currentChunk.Substring(currentChunk.Length - overlap) : "";
            currentChunk = string.IsNullOrEmpty(tail) ? paragraph : tail + "\n\n" + paragraph;
        }
        else
        {
            currentChunk = candidate;
        }
    }

    if (!string.IsNullOrWhiteSpace(currentChunk))
        chunks.Add(currentChunk);

    return chunks.ToArray();
}