# Evaluacion directa de NVIDIA NIM: latencia, exito, capacidades y parametros optimos.
# Llama directamente a integrate.api.nvidia.com (sin pasar por el proxy) para medir el upstream real.
# Genera artefacto en docs/testing/nvidia-nim-eval.json

param(
    [int]$TimeoutSec = 45,
    [int]$MaxTokens = 64
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env"
$outDir = Join-Path $repoRoot "docs/testing"
$outFile = Join-Path $outDir "nvidia-nim-eval.json"

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$key = (Select-String -Path $envPath -Pattern "PROVIDER_NVIDIA_API_KEY=(.*)").Matches.Groups[1].Value.Trim()
if ([string]::IsNullOrWhiteSpace($key)) { throw "No se encontro PROVIDER_NVIDIA_API_KEY en .env" }

$baseUrl = "https://integrate.api.nvidia.com"
$headers = @{ "Authorization" = "Bearer $key"; "Content-Type" = "application/json" }

# Candidatos: top code/reasoning models actuales del catalogo NVIDIA
$candidates = @(
    "deepseek-ai/deepseek-v4-pro",
    "deepseek-ai/deepseek-v4-flash",
    "qwen/qwen3-coder-480b-a35b-instruct",
    "qwen/qwen3.5-397b-a17b",
    "qwen/qwen3.5-122b-a10b",
    "qwen/qwen3-next-80b-a3b-instruct",
    "mistralai/mistral-large-3-675b-instruct-2512",
    "mistralai/mistral-small-4-119b-2603",
    "openai/gpt-oss-120b",
    "openai/gpt-oss-20b",
    "moonshotai/kimi-k2.6",
    "nvidia/nemotron-3-super-120b-a12b",
    "nvidia/nemotron-nano-3-30b-a3b",
    "nvidia/nvidia-nemotron-nano-9b-v2",
    "nvidia/llama-3.3-nemotron-super-49b-v1.5",
    "nvidia/llama-3.1-nemotron-ultra-253b-v1",
    "nvidia/nemotron-4-340b-instruct",
    "meta/llama-4-maverick-17b-128e-instruct"
)

$results = @()
$ok = 0
$fail = 0

foreach ($model in $candidates) {
    $body = @{
        model       = $model
        stream      = $false
        max_tokens  = $MaxTokens
        temperature = 0.2
        top_p       = 0.9
        messages    = @(@{ role = "user"; content = "Reply with a single word: OK" })
    } | ConvertTo-Json -Depth 4

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $entry = [ordered]@{
        model      = $model
        success    = $false
        status     = ""
        latency_ms = 0
        sample     = ""
        error      = ""
    }

    try {
        $resp = Invoke-RestMethod -Uri "$baseUrl/v1/chat/completions" -Method POST -Headers $headers -Body $body -TimeoutSec $TimeoutSec
        $sw.Stop()
        $entry.success = $true
        $entry.status = "ok"
        $entry.latency_ms = $sw.ElapsedMilliseconds
        $entry.sample = ($resp.choices[0].message.content -replace "\s+", " ").Trim()
        $ok++
        Write-Host ("[OK]   {0,-50} {1,7}ms  {2}" -f $model, $sw.ElapsedMilliseconds, $entry.sample)
    }
    catch {
        $sw.Stop()
        $entry.latency_ms = $sw.ElapsedMilliseconds
        $code = $null
        if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        $entry.status = if ($code) { "http_$code" } elseif ($sw.ElapsedMilliseconds -ge ($TimeoutSec * 1000 - 500)) { "timeout" } else { "error" }
        $entry.error = ($_.Exception.Message -replace "\s+", " ").Trim()
        $fail++
        Write-Host ("[FAIL] {0,-50} {1,7}ms  {2}" -f $model, $sw.ElapsedMilliseconds, $entry.status) -ForegroundColor Yellow
    }

    $results += [pscustomobject]$entry
}

$output = [ordered]@{
    summary = [ordered]@{
        generated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
        endpoint         = "$baseUrl/v1/chat/completions"
        total            = $candidates.Count
        success          = $ok
        failed           = $fail
        timeout_seconds  = $TimeoutSec
        max_tokens       = $MaxTokens
    }
    models  = $results
}

$output | ConvertTo-Json -Depth 6 | Out-File -FilePath $outFile -Encoding utf8
Write-Host "`nResumen: $ok OK / $fail FAIL de $($candidates.Count). Artefacto: $outFile"
