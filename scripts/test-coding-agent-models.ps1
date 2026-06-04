$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$outDir = Join-Path $PSScriptRoot '..\docs\testing'
$outDir = [System.IO.Path]::GetFullPath($outDir)
if (!(Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$timestamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$outFile = Join-Path $outDir ("coding-agent-model-validation-{0}.json" -f $timestamp)

$modelsResp = Invoke-RestMethod -Uri 'http://localhost:11434/v1/models' -Method Get -TimeoutSec 30
$all = @($modelsResp.data)

$pattern = 'coder|code|gpt-oss|deepseek|qwen|nemotron|llama-4|mixtral|mistral-large|kimi'
$candidates = @($all | Where-Object { $_.id -match $pattern } | Select-Object -First 24)

$results = @()
$idx = 0

foreach ($m in $candidates) {
    $idx++
    $model = $m.id
    $provider = $m.owned_by

    Write-Output ("[{0}/{1}] Testing {2} ({3})" -f $idx, $candidates.Count, $model, $provider)

    $body = @{
        model = $model
        stream = $false
        temperature = 0.2
        top_p = 0.9
        max_tokens = 64
        messages = @(
            @{ role = 'system'; content = 'You are a coding assistant. Be concise.' },
            @{ role = 'user'; content = 'Return exactly one word: OK' }
        )
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $status = 'ok'
    $ok = $false
    $text = ''
    $err = ''

    try {
        $resp = Invoke-RestMethod -Uri 'http://localhost:11434/v1/chat/completions' -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 10) -TimeoutSec 45
        $text = $resp.choices[0].message.content
        if ([string]::IsNullOrWhiteSpace($text)) { $status = 'empty_response' } else { $ok = $true }
    }
    catch {
        $status = 'error'
        $err = $_.Exception.Message
    }

    $sw.Stop()

    $results += [pscustomobject]@{
        model = $model
        provider = $provider
        tested_at_utc = (Get-Date).ToUniversalTime().ToString('o')
        test = [pscustomobject]@{
            success = $ok
            status = $status
            latency_ms = [int]$sw.Elapsed.TotalMilliseconds
            sample = ($text -replace '\s+', ' ')
            error = $err
        }
        profile = [pscustomobject]@{
            suitable_for_coding_agents = $true
            prompt_style = 'short deterministic'
            recommended = [pscustomobject]@{
                temperature = 0.2
                top_p = 0.9
                max_tokens = 512
                stream = $true
            }
        }
    }
}

$final = [pscustomobject]@{
    summary = [pscustomobject]@{
        generated_at_utc = (Get-Date).ToUniversalTime().ToString('o')
        total_candidates = $candidates.Count
        successful = ($results | Where-Object { $_.test.success }).Count
        failed = ($results | Where-Object { -not $_.test.success }).Count
        source = 'http://localhost:11434/v1/models'
        selection_pattern = $pattern
    }
    models = $results
}

$final | ConvertTo-Json -Depth 12 | Set-Content -Path $outFile -Encoding UTF8

Write-Output ("JSON generado: {0}" -f $outFile)
$final.summary | ConvertTo-Json -Depth 5
