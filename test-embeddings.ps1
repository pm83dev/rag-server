$ErrorActionPreference = "Stop"
$url = "http://100.90.112.22:1337/embeddings"
$model = "nomic-embed-text-v1.5"

function Get-Embedding($text) {
    $body = @{ model = $model; input = $text } | ConvertTo-Json
    $resp = Invoke-RestMethod -Uri $url -Method Post -Body $body -ContentType "application/json"

    # gestisce sia formato OpenAI-compatible ({"data":[{"embedding":[...]}]})
    # sia formato nativo llama.cpp ([{"index":0,"embedding":[[...]]}])
    if ($resp -is [System.Array]) {
        $emb = $resp[0].embedding
    } else {
        $emb = $resp.data[0].embedding
    }

    # appiattisce se annidato [[...]]
    if ($emb[0] -is [System.Array]) {
        Write-Host "  -> formato annidato, righe: $($emb.Count), dim per riga: $($emb[0].Count)"
        if ($emb.Count -gt 1) {
            Write-Host "  -> ATTENZIONE: piu' di una riga = probabile embedding per-token (pooling non attivo)"
        }
        return ,$emb[0]
    }
    Write-Host "  -> formato piatto, dim: $($emb.Count)"
    return ,$emb
}

function Cosine($a, $b) {
    $dot = 0.0; $na = 0.0; $nb = 0.0
    for ($i = 0; $i -lt $a.Count; $i++) {
        $dot += $a[$i] * $b[$i]
        $na += $a[$i] * $a[$i]
        $nb += $b[$i] * $b[$i]
    }
    return $dot / ([Math]::Sqrt($na) * [Math]::Sqrt($nb))
}

Write-Host "Query:"
$q = Get-Embedding "search_query: codice cer legno trattato"

Write-Host "`nDoc pertinente (CER):"
$dRel = Get-Embedding "search_document: 15 01 03 Legno trattato con sostanze chimiche o rivestito con materiali diversi da quelli di cui alla voce 15 01 02"

Write-Host "`nDoc non pertinente (sicurezza lavoro):"
$dIrrel = Get-Embedding "search_document: L'invio del DURC avverra' esclusivamente tramite PEC ai sensi del DPCM 22 luglio 2011"

Write-Host "`n--- Risultati ---"
Write-Host ("Similarita' query <-> doc CER (pertinente):        {0:N4}" -f (Cosine $q $dRel))
Write-Host ("Similarita' query <-> doc sicurezza (non pertinente): {0:N4}" -f (Cosine $q $dIrrel))
