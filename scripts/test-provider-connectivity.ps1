$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$envMap = @{}
Get-Content .env |
    Where-Object { $_ -match '^[A-Za-z_][A-Za-z0-9_]*=' } |
    ForEach-Object {
        $k, $v = $_ -split '=', 2
        $envMap[$k] = $v
    }

$providers = @(
    @{
        name = 'deepseek'
        key = $envMap['PROVIDER_DEEPSEEK_API_KEY']
        base = $(if ($envMap['PROVIDER_DEEPSEEK_BASE_URL']) { $envMap['PROVIDER_DEEPSEEK_BASE_URL'] } else { 'https://api.deepseek.com' })
        list = '/v1/models'
        chat = '/v1/chat/completions'
        modelHint = 'deepseek'
    },
    @{
        name = 'groq'
        key = $envMap['PROVIDER_GROQ_API_KEY']
        base = $(if ($envMap['PROVIDER_GROQ_BASE_URL']) { $envMap['PROVIDER_GROQ_BASE_URL'] } else { 'https://api.groq.com/openai' })
        list = '/v1/models'
        chat = '/v1/chat/completions'
        modelHint = 'llama|qwen|mixtral|deepseek'
    },
    @{
        name = 'openrouter'
        key = $envMap['PROVIDER_OPENROUTER_API_KEY']
        base = $(if ($envMap['PROVIDER_OPENROUTER_BASE_URL']) { $envMap['PROVIDER_OPENROUTER_BASE_URL'] } else { 'https://openrouter.ai/api' })
        list = '/v1/models'
        chat = '/v1/chat/completions'
        modelHint = 'qwen|deepseek|gpt-oss|llama'
    },
    @{
        name = 'nvidia'
        key = $envMap['PROVIDER_NVIDIA_API_KEY']
        base = $(if ($envMap['PROVIDER_NVIDIA_BASE_URL']) { $envMap['PROVIDER_NVIDIA_BASE_URL'] } else { 'https://integrate.api.nvidia.com' })
        list = '/v1/models'
        chat = '/v1/chat/completions'
        modelHint = 'qwen|deepseek|nemotron|gpt-oss'
    },
    @{
        name = 'ollama-cloud'
        key = $(if ($envMap['PROVIDER_OLLAMACLOUD_API_KEY']) { $envMap['PROVIDER_OLLAMACLOUD_API_KEY'] } else { $envMap['PROVIDER_OLLAMA_API_KEY'] })
        base = $(if ($envMap['PROVIDER_OLLAMA_BASE_URL']) { $envMap['PROVIDER_OLLAMA_BASE_URL'] } else { 'https://ollama.com' })
        list = '/api/tags'
        chat = '/api/chat'
        modelHint = 'gpt-oss|qwen|deepseek'
    }
)

$results = @()

foreach ($p in $providers) {
    if ([string]::IsNullOrWhiteSpace($p.key)) {
        $results += [pscustomobject]@{
            provider = $p.name
            configured = $false
            list_status = 'missing_key'
            models_count = 0
            chat_status = 'not_tested'
            chat_model = ''
            latency_ms = 0
            error = 'missing key'
        }
        continue
    }

    $headers = @{ Authorization = "Bearer $($p.key)" }
    $listStatus = 'error'
    $models = @()
    $chatStatus = 'not_tested'
    $chatModel = ''
    $latency = 0
    $errorText = ''

    try {
        $listResp = Invoke-RestMethod -Uri ($p.base + $p.list) -Method Get -Headers $headers -TimeoutSec 40

        if ($listResp.data) {
            $models = @($listResp.data | ForEach-Object {
                if ($_.id) { $_.id }
                elseif ($_.model) { $_.model }
                elseif ($_.name) { $_.name }
            })
        }
        elseif ($listResp.models) {
            $models = @($listResp.models | ForEach-Object {
                if ($_ -is [string]) { $_ }
                elseif ($_.model) { $_.model }
                elseif ($_.name) { $_.name }
                elseif ($_.id) { $_.id }
            })
        }

        $models = @($models | Where-Object { $_ } | Select-Object -Unique)
        $listStatus = 'ok'
    }
    catch {
        $errorText = $_.Exception.Message
        $listStatus = 'error'
    }

    if ($listStatus -eq 'ok' -and $models.Count -gt 0) {
        $chatModel = $models | Where-Object { $_ -match $p.modelHint } | Select-Object -First 1
        if (-not $chatModel) { $chatModel = $models[0] }

        try {
            if ($p.name -eq 'ollama-cloud') {
                $body = @{
                    model = $chatModel
                    stream = $false
                    messages = @(@{ role = 'user'; content = 'Reply exactly OK' })
                } | ConvertTo-Json -Depth 6
            }
            else {
                $body = @{
                    model = $chatModel
                    stream = $false
                    max_tokens = 32
                    temperature = 0.2
                    messages = @(@{ role = 'user'; content = 'Reply exactly OK' })
                } | ConvertTo-Json -Depth 6
            }

            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $chatResp = Invoke-RestMethod -Uri ($p.base + $p.chat) -Method Post -Headers ($headers + @{ 'Content-Type' = 'application/json' }) -Body $body -TimeoutSec 45
            $sw.Stop()

            $latency = [int]$sw.Elapsed.TotalMilliseconds
            $text = if ($chatResp.choices) { $chatResp.choices[0].message.content } else { $chatResp.message.content }
            if ([string]::IsNullOrWhiteSpace($text)) { $chatStatus = 'empty_response' } else { $chatStatus = 'ok' }
        }
        catch {
            $chatStatus = 'error'
            if ([string]::IsNullOrWhiteSpace($errorText)) { $errorText = $_.Exception.Message }
        }
    }

    $results += [pscustomobject]@{
        provider = $p.name
        configured = $true
        list_status = $listStatus
        models_count = $models.Count
        chat_status = $chatStatus
        chat_model = $chatModel
        latency_ms = $latency
        error = $errorText
    }
}

$proxy = Invoke-RestMethod -Uri 'http://localhost:11434/health' -Method Get

$out = [pscustomobject]@{
    generated_at_utc = (Get-Date).ToUniversalTime().ToString('o')
    proxy_providers = $proxy.providers
    summary = [pscustomobject]@{
        total = $results.Count
        list_ok = ($results | Where-Object { $_.list_status -eq 'ok' }).Count
        ok_chat = ($results | Where-Object { $_.chat_status -eq 'ok' }).Count
    }
    providers = $results
}

$outPath = 'docs/testing/provider-connectivity-20260604.json'
$out | ConvertTo-Json -Depth 8 | Set-Content $outPath -Encoding UTF8
$out | ConvertTo-Json -Depth 8
