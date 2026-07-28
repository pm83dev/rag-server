# PM RAG Server

Sistema RAG (Retrieval-Augmented Generation) basato su .NET 8 e Angular per l'indicizzazione di documenti PDF e il retrieval semantico con vettori embedding.

## 📋 Struttura del Progetto

```
rag-server/
├── RagBackend/              # Backend API (.NET 8 ASP.NET Core)
│   ├── Services/            # Servizi (Embedding, Chat, VectorStore)
│   ├── Models/              # Modelli dati
│   ├── Dockerfile           # Container Docker per il backend
│   ├── appsettings.json     # Configurazione
│   └── web.config           # Configurazione IIS
├── rag-frontend/            # Frontend Angular (v22)
│   ├── src/app/             # Applicazione Angular
│   └── package.json         # Dipendenze frontend
├── docker-compose.yml       # Orchestrazione servizi (Qdrant DB)
├── _check/                  # Progetto di test check
├── _inspect/                # Progetto di test inspect
├── _ns/                     # Progetto di test ns
└── readme.md                # Questo file
```

## 🏗️ Architettura

Il sistema è composto da:

- **RagBackend**: API REST in .NET 8 che gestisce indicizzazione, ricerca e chat
- **Qdrant**: Vector Database per il storage dei vettori di embedding (container Docker)
- **llama.cpp Server**: Server per generazione embedding e chat (configurabile esternamente)
- **Rag-Frontend**: Interfaccia Angular per interazione utente

## 📦 Requisiti

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js](https://nodejs.org/) (v18+ per Angular)
- [Docker](https://www.docker.com/) e Docker Compose
- [llama.cpp server](https://github.com/ggerganov/llama.cpp) con modello nomic-embed-text (per embedding)
- [llama.cpp chat server](https://github.com/ggerganov/llama.cpp) (per generazione risposte)

## 🚀 Avvio Rapido

### 1. Avviare Qdrant (Vector Database)

```bash
docker-compose up -d qdrant
```

Qdrant sarà disponibile su `http://localhost:6333` (gRPC) e `http://localhost:6334` (HTTP API).

### 2. Configurare il Server di Embedding

Avviare il server llama.cpp per gli embedding con il modello nomic-embed-text:

```powershell
.\llama-server.exe -m .\models\nomic-embed_text-Q8_0.gguf --embedding --host 0.0.0.0 --port 1337
```

**Nota:** Aggiornare l'URL in `RagBackend/appsettings.json` se il server non è in locale:

```json
{
  "LlamaCppEmbeddingServer": {
    "Url": "http://your-embedding-server-ip:1337"
  }
}
```

### 3. Avviare il Backend

```bash
cd RagBackend
dotnet run
```

Il backend sarà disponibile su `http://localhost:5000` (o la porta configurata).

### 4. Avviare il Frontend

```bash
cd rag-frontend
npm install
ng serve
```

Il frontend sarà disponibile su `http://localhost:4200`.

## ⚙️ Configurazione

### RagBackend/appsettings.json

```json
{
  "Qdrant": {
    "Url": "http://localhost:6334"
  },
  "LlamaCppEmbeddingServer": {
    "Url": "http://100.78.184.121:1337"
  },
  "LlamaCppChatServer": {
    "Url": "http://100.90.112.22:9000"
  },
  "EmbeddingModel": {
    "Name": "nomic-embed-text-v1.5"
  },
  "Cors": {
    "AllowedOrigins": ["*"]
  }
}
```

| Setting | Descrizione | Default |
|---------|------------|---------|
| `Qdrant:Url` | URL del server Qdrant | http://localhost:6334 |
| `LlamaCppEmbeddingServer:Url` | URL del server embedding llama.cpp | http://100.78.184.121:1337 |
| `LlamaCppChatServer:Url` | URL del server chat llama.cpp | http://100.90.112.22:9000 |
| `EmbeddingModel:Name` | Nome del modello embedding | nomic-embed-text-v1.5 |
| `Cors:AllowedOrigins` | Origini CORS consentite | ["*"] |

## 📡 API Endpoints

### Indicizzazione Contenuto Testuale

```http
POST /api/index?content=Testo%20da%20indicizzare
Content-Type: text/plain
```

Divide il testo in chunk e li indicizza nel vettore store.

**Risposta:**
```json
{
  "message": "Indicizzati N chunk."
}
```

### Upload PDF

```http
POST /api/upload-pdf
Content-Type: multipart/form-data

file: <pdf_file>
```

Carica un PDF, estrae il testo, genera embedding e li indicizza in background.

**Risposta:** (202 Accepted)
```json
{
  "jobId": "unique-job-id"
}
```

### Stato Indicizzazione

```http
GET /api/upload-status/{jobId}
```

**Risposta:**
```json
{
  "jobId": "unique-job-id",
  "fileName": "documento.pdf",
  "status": "Processing|Completed|Failed",
  "totalChunks": 10,
  "processedChunks": 5,
  "errorMessage": null
}
```

### Ricerca Semantica

```http
GET /api/search?query=testo%20ricerca
```

**Risposta:**
```json
[
  {
    "id": "uuid",
    "score": 0.85,
    "metadata": {
      "content": "...",
      "source": "documento.pdf"
    }
  }
]
```

### Chat con Contesto RAG

```http
GET /api/ask?question=La%20tua%20domanda
```

Combina ricerca semantica e generazione LLM per risposte contestuali.

**Risposta:**
```json
{
  "answer": "Testo della risposta generata dal modello...",
  "sources": [
    {
      "id": "uuid",
      "score": 0.85
    }
  ]
}
```

### Elenco Documenti Indicizzati

```http
GET /api/documents
```

**Risposta:**
```json
[
  {
    "source": "documento.pdf",
    "chunks": 10
  }
]
```

### Eliminazione Documento

```http
DELETE /api/documents/{source}
```

Elimina tutti i chunk associati a un documento sorgente.

**Risposta:**
```json
{
  "message": "Eliminati N chunk del documento 'documento.pdf'."
}
```

## 🔧 Servizi

### IEmbeddingService
Interfaccia per la generazione di vettori embedding. Supporta singolo testo e batch.

### IVectorStoreService
Interfaccia per operazioni sul vector store Qdrant (Upsert, Search, DeleteBySource, ListSources).

### IChatService
Interfaccia per interazione con il server chat llama.cpp.

## 🐳 Docker Deployment

### Build e avvio del backend

```bash
docker build -t rag-backend -f RagBackend/Dockerfile .
docker run -p 8080:8080 -p 8081:8081 rag-backend
```

### Avvio completo con docker-compose

```bash
docker-compose up -d
```

Questo avvia automaticamente Qdrant. Il backend va avviato separatamente.

## 📝 Note Tecniche

- **Chunking**: I documenti vengono divisi in chunk di max 500 caratterchi con overlap di 50
- **Batch Processing**: Gli embedding vengono generati in batch da 20 elementi
- **Background Jobs**: L'indicizzazione PDF avviene in background per evitare timeout
- **CORS**: Configurabile tramite `Cors:AllowedOrigins` in appsettings.json
- **Limite File**: 200 MB per upload PDF (configurabile)

## 🧪 Test Projects

| Progetto | Descrizione |
|----------|-------------|
| `_check/` | Progetto di test per verifiche |
| `_inspect/` | Progetto di test per ispezione |
| `_ns/` | Progetto di test ns |

## 🔗 Link Utili

- [Qdrant Documentation](https://qdrant.tech/documentation/)
- [llama.cpp GitHub](https://github.com/ggerganov/llama.cpp)
- [Angular Documentation](https://angular.io/docs)
- [.NET 8 Documentation](https://docs.microsoft.com/dotnet/core/)

## 📄 Licenza

[Da specificare]