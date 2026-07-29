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

            const int batchSize = 80;
            var skippedChunks = new System.Collections.Concurrent.ConcurrentBag<string>();

            // Il server llama.cpp ha 4 slot paralleli (--parallel): mandare i batch uno alla volta
            // in sequenza li sotto-utilizza. Elaboriamo più batch in contemporanea, limitati a 4,
            // per sfruttare gli slot disponibili senza sommergere il server di richieste.
            var maxConcurrentBatches = 4;
            using var batchThrottle = new SemaphoreSlim(maxConcurrentBatches);

            var batchTasks = new List<Task>();
            for (var i = 0; i < chunks.Length; i += batchSize)
            {
                var offset = i;
                var batch = chunks.Skip(offset).Take(batchSize).ToList();

                await batchThrottle.WaitAsync();
                batchTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        List<(string Chunk, float[] Vector)> embedded;
                        try
                        {
                            var vectors = await embedding.GenerateBatchAsync(batch);
                            embedded = batch.Zip(vectors, (c, v) => (c, v)).ToList();
                        }
                        catch (Exception)
                        {
                            // Un chunk del batch può superare i limiti del server llama.cpp (es. testo
                            // estratto dal PDF particolarmente denso in token pur restando sotto il limite
                            // di caratteri). Isoliamo il batch chunk per chunk così un singolo chunk
                            // problematico non fa fallire l'intero documento: quello che continua a
                            // fallire da solo viene saltato e segnalato, il resto procede.
                            embedded = new List<(string, float[])>();
                            for (var b = 0; b < batch.Count; b++)
                            {
                                try
                                {
                                    embedded.Add((batch[b], await embedding.GenerateAsync(batch[b])));
                                }
                                catch (Exception exSingle)
                                {
                                    skippedChunks.Add($"[{offset + b}] {exSingle.Message}");
                                }
                            }
                        }

                        var points = embedded.Select(e => (
                            Id: Guid.NewGuid().ToString(),
                            Vector: e.Vector,
                            Metadata: new Dictionary<string, object>
                            {
                                { "content", e.Chunk },
                                { "source", job.FileName }
                            }));
                        await vectorStore.UpsertBatchAsync("documents", points);

                        jobStore.Update(job.Id, j => j.ProcessedChunks += batch.Count);
                    }
                    finally
                    {
                        batchThrottle.Release();
                    }
                }));
            }

            await Task.WhenAll(batchTasks);

            if (skippedChunks.Count > 0)
            {
                jobStore.Update(job.Id, j => j.ErrorMessage =
                    $"{skippedChunks.Count} chunk saltati (embedding fallito): {string.Join("; ", skippedChunks)}");
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

app.MapGet("/api/search", async (string query, IVectorStoreService vectorStore, IEmbeddingService embedding, string? source) =>
{
    var vector = await embedding.GenerateAsync(query, EmbeddingTaskType.Query);
    var results = await vectorStore.SearchAsync("documents", vector, limit: 5, source: source);
    return Results.Ok(results.Select(r => new { r.Id, r.Score, r.Metadata }));
});

app.MapGet("/api/ask", async (string question, IVectorStoreService vectorStore, IEmbeddingService embedding, IChatService chat) =>
{
    var vector = await embedding.GenerateAsync(question, EmbeddingTaskType.Query);
    var results = await vectorStore.SearchAcrossSourcesAsync("documents", vector, queryText: question, perSourceLimit: 3);

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

    var rawParagraphs = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);

    // PDF a impaginazione tabellare (es. elenchi di codici) spesso non producono interruzioni
    // di paragrafo (\n\n): senza questo passaggio un intero "paragrafo" di più pagine finirebbe
    // in un unico chunk enorme, il cui embedding (media di tutti i sotto-chunk) diluirebbe il
    // significato specifico di ogni singola riga, rendendolo poco rilevante per query mirate.
    var paragraphs = rawParagraphs.SelectMany(p => SplitOversizedParagraph(p, maxChunkSize, overlap));
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

static IEnumerable<string> SplitOversizedParagraph(string paragraph, int maxChunkSize, int overlap)
{
    if (paragraph.Length <= maxChunkSize)
    {
        yield return paragraph;
        yield break;
    }

    var start = 0;
    while (start < paragraph.Length)
    {
        var length = Math.Min(maxChunkSize, paragraph.Length - start);
        yield return paragraph.Substring(start, length);

        if (start + length >= paragraph.Length)
            yield break;

        start += length - overlap;
    }
}