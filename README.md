# Copilot AI Proxy

Copilot AI Proxy is a high-performance ASP.NET Core minimal API proxy that lets
GitHub Copilot, Cursor, Continue.dev, Visual Studio BYOM, Ollama clients, and
OpenAI-compatible SDKs use models from multiple upstream providers through one
local endpoint.

The proxy exposes two client-facing API surfaces:

| Client API | Local prefix | Typical clients |
|---|---|---|
| OpenAI-compatible | `/v1/*` | GitHub Copilot, Cursor, Continue.dev, OpenAI SDKs |
| Ollama-compatible | `/api/*` | Visual Studio BYOM, Ollama clients |

## Features

- Multi-provider routing by model name.
- OpenAI-compatible and Ollama-compatible client endpoints.
- Support for direct providers, aggregators, Ollama Cloud, local Ollama, and
  generic OpenAI-compatible providers.
- Curated model allowlists in `config/model-selection/*.json`.
- Optional short display names via `display_name`.
- Per-model context window, output token, timeout, temperature, tool, and vision
  metadata.
- Provider-specific parameter filtering before forwarding requests upstream.
- Streaming support for SSE and Ollama NDJSON.
- Optional proxy authentication with `PROXY_API_KEY`.
- Diagnostic response headers for routing visibility.

## Supported providers

Only providers with configured credentials are active at runtime.

| Provider key | Provider | API format | Default base URL | Key variable |
|---|---|---|---|---|
| `deepseek` | DeepSeek | OpenAI-compatible | `https://api.deepseek.com` | `PROVIDER_DEEPSEEK_API_KEY` |
| `openai` | OpenAI | OpenAI-compatible | `https://api.openai.com` | `PROVIDER_OPENAI_API_KEY` |
| `zhipu` | Zhipu / BigModel | OpenAI-compatible | `https://open.bigmodel.cn/api/paas` | `PROVIDER_ZHIPU_API_KEY` |
| `qwen` | Qwen / DashScope compatible mode | OpenAI-compatible | `https://dashscope.aliyuncs.com/compatible-mode` | `PROVIDER_QWEN_API_KEY` |
| `minimax` | MiniMax | OpenAI-compatible | `https://api.minimax.io` | `PROVIDER_MINIMAX_API_KEY` |
| `hunyuan` | Tencent Hunyuan / TokenHub | OpenAI-compatible | `https://tokenhub.tencentmaas.com` | `PROVIDER_HUNYUAN_API_KEY` |
| `google` | Google Gemini OpenAI-compatible API | OpenAI-compatible | `https://generativelanguage.googleapis.com` | `PROVIDER_GOOGLE_API_KEY` |
| `nvidia` | NVIDIA NIM | OpenAI-compatible | `https://integrate.api.nvidia.com` | `PROVIDER_NVIDIA_API_KEY` |
| `openrouter` | OpenRouter | OpenAI-compatible | `https://openrouter.ai/api` | `PROVIDER_OPENROUTER_API_KEY` |
| `groq` | Groq | OpenAI-compatible | `https://api.groq.com/openai` | `PROVIDER_GROQ_API_KEY` |
| `moonshot` | Moonshot / Kimi | OpenAI-compatible | `https://api.moonshot.ai` | `PROVIDER_MOONSHOT_API_KEY` |
| `kimi` | Kimi China | OpenAI-compatible | `https://api.moonshot.cn` | `PROVIDER_KIMI_API_KEY` |
| `cerebras` | Cerebras | OpenAI-compatible | `https://api.cerebras.ai` | `PROVIDER_CEREBRAS_API_KEY` |
| `zenmux` | ZenMux | OpenAI-compatible aggregator | `https://zenmux.ai/api` | `PROVIDER_ZENMUX_API_KEY` |
| `ollama` | Ollama Cloud or local Ollama | Ollama API | `https://ollama.com` | `PROVIDER_OLLAMACLOUD_API_KEY` |
| `customopenai` | Any OpenAI-compatible service, including Vercel AI Gateway | OpenAI-compatible | none | `PROVIDER_CUSTOMOPENAI_API_KEY` |

`customopenai` has no default base URL. You must set
`PROVIDER_CUSTOMOPENAI_BASE_URL`. The Vercel example uses
`https://ai-gateway.vercel.sh`.

## Requirements

- Release zip: no .NET SDK required.
- Source build: .NET 10 SDK, or Docker.
- At least one provider API key, unless you only use local Ollama.

## Release builds

For most users, the easiest installation path is a GitHub release zip. Each
release package is self-contained and includes:

- `ai-proxy-hub` or `ai-proxy-hub.exe`
- `.env.example`
- `config/model-selection/*.json`
- `README.md`
- `README.zh-CN.md`
- A platform start script

Download the package for your operating system:

| Package | Use on |
|---|---|
| `copilot-ai-proxy-vX.Y.Z-win-x64.zip` | Windows x64 |
| `copilot-ai-proxy-vX.Y.Z-linux-x64.zip` | Linux x64 |
| `copilot-ai-proxy-vX.Y.Z-osx-x64.zip` | Intel macOS |
| `copilot-ai-proxy-vX.Y.Z-osx-arm64.zip` | Apple Silicon macOS |

Run it:

```powershell
# Windows
Expand-Archive .\copilot-ai-proxy-vX.Y.Z-win-x64.zip
cd .\copilot-ai-proxy-vX.Y.Z-win-x64
.\start-windows.cmd
```

```bash
# Linux/macOS
unzip copilot-ai-proxy-vX.Y.Z-linux-x64.zip
cd copilot-ai-proxy-vX.Y.Z-linux-x64
chmod +x ./start-unix.sh ./ai-proxy-hub
./start-unix.sh
```

On the first run, the start script creates `.env` from `.env.example` and asks
you to edit it. Add at least one provider API key, then start the proxy again.

You can change model metadata by editing `config/model-selection/*.json` in the
same folder. Restart the proxy after changing `.env` or model JSON files.

## Quick start

Use this path when you want to run from source.

1. Copy the example environment file.

```bash
cp .env.example .env
```

On Windows PowerShell:

```powershell
Copy-Item .env.example .env
```

2. Edit `.env` and configure at least one provider.

```bash
PROVIDER_DEEPSEEK_API_KEY=sk-your-deepseek-key
PROVIDER_DEEPSEEK_BASE_URL=https://api.deepseek.com

PROXY_PORT=11434
DEEPSEEK_MODEL=deepseek-v4-pro
```

3. Run the proxy.

```bash
dotnet run
```

The default local base URL is:

```text
http://localhost:11434
```

4. Check the model list.

```bash
curl http://localhost:11434/v1/models
curl http://localhost:11434/api/tags
```

## Client configuration

### OpenAI-compatible clients

Use this with Cursor, Continue.dev, OpenAI SDKs, and clients that accept an
OpenAI-compatible base URL.

```text
Base URL: http://localhost:11434/v1
API key: any non-empty value, unless PROXY_API_KEY is set
Model:   choose a model from /v1/models
```

Example request:

```bash
curl http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "deepseek-v4-pro",
    "messages": [
      { "role": "user", "content": "Introduce yourself briefly." }
    ],
    "stream": true
  }'
```

### Visual Studio BYOM / Ollama-compatible clients

Point the Ollama-compatible client at:

```text
http://localhost:11434/api/chat
```

List available models with:

```bash
curl http://localhost:11434/api/tags
```

## Zhipu / BigModel example

The following upstream BigModel request:

```bash
curl -X POST "https://open.bigmodel.cn/api/paas/v4/chat/completions" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -d '{
    "model": "glm-5.2",
    "messages": [
      { "role": "user", "content": "Introduce yourself briefly." }
    ],
    "temperature": 1.0,
    "stream": true
  }'
```

is configured in this proxy as:

```bash
PROVIDER_ZHIPU_API_KEY=your-bigmodel-key
# Optional; this is the default:
PROVIDER_ZHIPU_BASE_URL=https://open.bigmodel.cn/api/paas
```

Then call the proxy:

```bash
curl http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "GLM 5.2",
    "messages": [
      { "role": "user", "content": "Introduce yourself briefly." }
    ],
    "temperature": 1.0,
    "stream": true
  }'
```

`GLM 5.2` is the configured display name. The proxy routes it to the upstream
model id `glm-5.2`.

## Qwen / DashScope example

Configure DashScope compatible mode:

```bash
PROVIDER_QWEN_API_KEY=your-dashscope-key
# Optional; this is the default:
PROVIDER_QWEN_BASE_URL=https://dashscope.aliyuncs.com/compatible-mode
```

The default Qwen examples are in `config/model-selection/qwen.json`:

- `qwen3-coder-plus`, displayed as `Qwen Coder`
- `qwen-plus`, displayed as `Qwen Plus`
- `qwen-turbo`, displayed as `Qwen Turbo`

## Kimi China example

Kimi China and Moonshot international use different API keys. Configure the
China endpoint with its own variables:

```bash
PROVIDER_KIMI_API_KEY=sk-your-kimi-cn-key
# Optional; this is the default:
PROVIDER_KIMI_BASE_URL=https://api.moonshot.cn
```

Available coding models are curated in `config/model-selection/kimi.json`.
When both `kimi` and `moonshot` are enabled, add `@kimi` to pin the request to
the China endpoint:

```bash
curl http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "kimi-k2.7-code@kimi",
    "messages": [{ "role": "user", "content": "Write a C# binary search function." }],
    "stream": true
  }'
```

## Vercel AI Gateway through `customopenai`

Vercel AI Gateway exposes an OpenAI-compatible model catalog and Chat
Completions API. The existing `customopenai` provider can connect to it without
adding a Vercel-specific provider.

### 1. Create the connection

Create an AI Gateway API key in Vercel and keep it only in the local `.env`
file:

```bash
PROVIDER_CUSTOMOPENAI_API_KEY=your-vercel-ai-gateway-key
PROVIDER_CUSTOMOPENAI_BASE_URL=https://ai-gateway.vercel.sh
```

Do not add `/v1` to the Base URL. This project appends `v1/models` and
`v1/chat/completions` when it sends requests upstream.

Query the upstream catalog when you need the current model IDs:

```bash
curl https://ai-gateway.vercel.sh/v1/models
```

Always copy the complete upstream ID, including its creator prefix such as
`meta/` or `kwaipilot/`.

### 2. Configure one model

The repository includes the verified Vercel model in
`config/model-selection/customopenai.json`:

```json
{
  "provider": "customopenai",
  "models": [
    {
      "match": "meta/muse-spark-1.1",
      "display_name": "Muse Spark 1.1",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1048576,
        "max_output_tokens": 1048576,
        "supports_tools": true,
        "supports_vision": true,
        "supports_reasoning": true,
        "family": "muse-spark",
        "temperature": 0.2,
        "max_tokens": 65536,
        "timeout_seconds": 300
      }
    }
  ]
}
```

The important fields are:

| Field | Meaning |
|---|---|
| `match` | Exact model ID returned by Vercel `/v1/models`. |
| `display_name` | Short, unique name shown in client model lists. |
| `priority` | Display and routing order; lower values come first. |
| `enabled` | Whether the model is exposed by this proxy. |
| `context_length` | Model input context capability. |
| `max_output_tokens` | Model output capability advertised to clients. |
| `supports_tools` | Enables tool/function calling metadata. |
| `supports_vision` | Enables image input metadata. |
| `supports_reasoning` | Enables reasoning capability metadata. |
| `max_tokens` | Default request output limit when the client omits one. |
| `timeout_seconds` | Upstream request timeout for this model. |

Use capability values published by the upstream catalog. `max_tokens` is a
per-request default and can be lower than the model's advertised
`max_output_tokens`.

### 3. Add multiple models to one connection

Add another object to the same `models` array. Every entry shares the single
`PROVIDER_CUSTOMOPENAI_API_KEY` and `PROVIDER_CUSTOMOPENAI_BASE_URL` connection:

```json
{
  "provider": "customopenai",
  "models": [
    {
      "match": "meta/muse-spark-1.1",
      "display_name": "Muse Spark 1.1",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1048576,
        "max_output_tokens": 1048576,
        "supports_tools": true,
        "supports_vision": true,
        "supports_reasoning": true,
        "family": "muse-spark",
        "temperature": 0.2,
        "max_tokens": 65536,
        "timeout_seconds": 300
      }
    },
    {
      "match": "kwaipilot/kat-coder-air-v2.5",
      "display_name": "Kat Coder Air V2.5",
      "priority": 2,
      "enabled": true,
      "execution": {
        "context_length": 256000,
        "max_output_tokens": 80000,
        "supports_tools": true,
        "supports_vision": false,
        "supports_reasoning": true,
        "family": "kat-coder",
        "temperature": 0.2,
        "max_tokens": 32768,
        "timeout_seconds": 300
      }
    }
  ]
}
```

For each model:

- Keep `match` equal to the exact upstream ID.
- Use a unique `display_name` so clients can distinguish models.
- Use unique priorities when you want a stable list order.
- Include only capabilities supported by that model.

### 4. Split a large model list across files

For a long list, create multiple files under `config/model-selection/`. File
names are arbitrary; every file must still declare the same provider.

`config/model-selection/customopenai-muse.json`:

```json
{
  "provider": "customopenai",
  "models": [
    {
      "match": "meta/muse-spark-1.1",
      "display_name": "Muse Spark 1.1",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1048576,
        "max_output_tokens": 1048576,
        "supports_tools": true,
        "supports_vision": true,
        "supports_reasoning": true,
        "family": "muse-spark",
        "max_tokens": 65536,
        "timeout_seconds": 300
      }
    }
  ]
}
```

`config/model-selection/customopenai-coding.json`:

```json
{
  "provider": "customopenai",
  "models": [
    {
      "match": "kwaipilot/kat-coder-air-v2.5",
      "display_name": "Kat Coder Air V2.5",
      "priority": 2,
      "enabled": true,
      "execution": {
        "context_length": 256000,
        "max_output_tokens": 80000,
        "supports_tools": true,
        "supports_vision": false,
        "supports_reasoning": true,
        "family": "kat-coder",
        "max_tokens": 32768,
        "timeout_seconds": 300
      }
    }
  ]
}
```

The loader merges non-duplicate models from all files whose `provider` is
`customopenai`. Do not repeat the same `match` in multiple files. Splitting files
only organizes models; it does not create separate API keys or Base URLs.

### 5. Restart and verify

Model-selection files are loaded at startup, so restart the proxy after every
JSON change. Verify both model-list formats:

```bash
curl http://localhost:11434/v1/models
curl http://localhost:11434/api/tags
```

Test a non-streaming OpenAI-compatible request:

```bash
curl http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"Muse Spark 1.1@customopenai","messages":[{"role":"user","content":"Hello"}],"stream":false}'
```

Test streaming:

```bash
curl http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"Muse Spark 1.1@customopenai","messages":[{"role":"user","content":"Hello"}],"stream":true}'
```

For OpenAI-compatible clients, use Base URL `http://localhost:11434/v1`. For
Visual Studio BYOM/Ollama-compatible clients, use `http://localhost:11434`.
Enter any non-empty local API key unless `PROXY_API_KEY` is set, and select a
model name returned by `/v1/models` or `/api/tags`.

## How model selection works

The proxy does not expose every model returned by an upstream provider. A model
is exposed only when both conditions are true:

1. The active provider's model catalog returns the model id.
2. The model id matches an enabled entry in `config/model-selection/{provider}.json`.

This allowlist keeps Copilot model lists small and prevents unsupported model
families, embedding models, rerankers, guard models, or accidental provider
catalog noise from appearing in client UIs.

## Model configuration format

Each provider has a JSON file under `config/model-selection/`.

Example:

```json
{
  "provider": "zhipu",
  "models": [
    {
      "match": "glm-5.2",
      "display_name": "GLM 5.2",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1000000,
        "max_output_tokens": 65536,
        "supports_tools": true,
        "supports_vision": false,
        "family": "z-ai",
        "temperature": 1.0,
        "max_tokens": 16384,
        "timeout_seconds": 240
      }
    }
  ]
}
```

### Top-level model fields

| Field | Required | Meaning |
|---|---:|---|
| `match` | yes | Exact model id or substring used to match an upstream model id. |
| `display_name` | no | Short name shown to clients and accepted in requests. Use this to avoid long model names in Copilot. |
| `priority` | yes | Lower values win when multiple providers expose the same upstream id. |
| `enabled` | yes | Set `false` to keep a configured model hidden. |
| `execution` | no | Per-model metadata and request defaults. |

### Execution fields

| Field | Meaning |
|---|---|
| `context_length` | Input context window exposed through `/api/show` and `/api/tags`. |
| `max_output_tokens` | Maximum model output capacity exposed to clients. |
| `supports_tools` | Whether the model should be advertised as tool-capable. |
| `supports_vision` | Whether the model should be advertised as image-capable. |
| `family` | Model family label shown in Ollama-compatible metadata. |
| `temperature` | Default temperature injected when the client omits it. |
| `top_p` | Default top-p injected when the client omits it and the provider supports it. |
| `max_tokens` | Default request `max_tokens` injected when the client omits it. |
| `max_completion_tokens` | Default request `max_completion_tokens` for providers that use the current OpenAI field. |
| `reasoning_effort` | Default reasoning effort for providers that support it. |
| `timeout_seconds` | Per-model upstream request timeout. |
| `override_client_params` | When `true`, configured defaults overwrite client-supplied values. |
| `supports_reasoning` | Optional metadata flag for reasoning-capable models. |

### Model capability metadata

Each model entry can describe the model id, display name, endpoint provider,
token limits, and feature support exposed to OpenAI-compatible and
Ollama-compatible clients.

| Capability | Proxy configuration |
|---|---|
| User-facing model name | `models[].display_name` |
| Upstream model id or match rule | `models[].match` |
| Provider endpoint | `PROVIDER_<PROVIDER>_BASE_URL` in `.env` |
| Input context window | `models[].execution.context_length` |
| Maximum output capacity | `models[].execution.max_output_tokens` |
| Tool calling support | `models[].execution.supports_tools` |
| Image input support | `models[].execution.supports_vision` |

`context_length` and `max_output_tokens` are intentionally separate.
`context_length` is the input context window that clients can use for prompt and
conversation sizing. `max_output_tokens` is the model's output capacity.
`max_tokens` and `max_completion_tokens` are different from capability metadata:
they are alternative request fields for the default output limit sent upstream.
Configure only the field documented by that provider; the proxy never sends both.

### Image context support

The proxy already supports image-capable OpenAI-style chat messages and
Ollama-style image payloads. Enable image context per model with:

```json
{
  "match": "provider-vision-model",
  "execution": {
    "supports_vision": true
  }
}
```

When `supports_vision` is `true`, Ollama-compatible metadata also exposes
`supports_images: true`. Only enable this flag for models whose upstream API
really accepts image input. If a provider releases a text-only model and the
flag is enabled by mistake, clients may send image payloads that the upstream
provider rejects.

## Adding new models

Use the same flow for DeepSeek, Zhipu, Qwen, and other providers.

1. Confirm the provider is active in `.env`.

```bash
PROVIDER_ZHIPU_API_KEY=your-bigmodel-key
```

2. Check the upstream model id.

```bash
curl http://localhost:11434/v1/models
```

If the provider key is configured but the model is missing, call the upstream
provider's model catalog directly and confirm the exact id it returns.

3. Edit the matching selection file.

Examples:

- DeepSeek models: `config/model-selection/deepseek.json`
- Zhipu / BigModel models: `config/model-selection/zhipu.json`
- Qwen / DashScope models: `config/model-selection/qwen.json`
- Generic OpenAI-compatible models: `config/model-selection/customopenai.json`

4. Add or update a model entry.

```json
{
  "match": "new-upstream-model-id",
  "display_name": "Short Name",
  "priority": 10,
  "enabled": true,
  "execution": {
    "context_length": 128000,
    "max_output_tokens": 8192,
    "supports_tools": true,
    "supports_vision": false,
    "family": "provider-family",
    "temperature": 0.2,
    "max_tokens": 8192,
    "timeout_seconds": 180
  }
}
```

5. Restart the proxy.

6. Confirm the display name and routing.

```bash
curl http://localhost:11434/api/tags
curl http://localhost:11434/v1/models
```

If `display_name` is set, clients can request either the display name or the
upstream model id. For example, `GLM 5.2` routes to `glm-5.2`.

## Updating context windows

Context and output limits are configured in the model selection JSON files, not
in C# code.

When a provider changes a model's context window:

1. Open the provider's file in `config/model-selection/`.
2. Find the model entry.
3. Update:
   - `execution.context_length`
   - `execution.max_output_tokens`
   - `execution.max_tokens`, if you also want to change the default request size
4. Restart the proxy.
5. Check `GET /api/show?model=<model>` or `GET /api/tags`.

Example:

```json
{
  "match": "glm-5.2",
  "display_name": "GLM 5.2",
  "execution": {
    "context_length": 2000000,
    "max_output_tokens": 131072,
    "max_tokens": 32768
  }
}
```

`context_length` and `max_output_tokens` describe model capability. `max_tokens`
is the default value sent upstream when the client does not provide one.

## Routing aliases

The proxy accepts several model id forms:

| Form | Example | Behavior |
|---|---|---|
| Upstream id | `glm-5.2` | Routes to the winning provider for that upstream id. |
| Display name | `GLM 5.2` | Routes to the upstream id configured by `display_name`. |
| Qualified upstream alias | `glm-5.2@zhipu` | Pins the request to a specific provider. |
| Qualified display alias | `GLM 5.2@zhipu` | Pins the short display name to a specific provider. |
| Ollama tag suffix | `GLM 5.2:latest` | The `:latest` suffix is stripped before routing. |

Use qualified aliases when the same model id is available from multiple
providers and you want to avoid failover or priority-based routing.

## Diagnostics

Chat responses include routing headers:

| Header | Meaning |
|---|---|
| `X-Proxy-Requested-Model` | Model name sent by the client. |
| `X-Proxy-Resolved-Model` | Internal model alias after normalization. |
| `X-Proxy-Upstream-Model` | Upstream model id sent to the provider. |
| `X-Proxy-Provider` | Provider selected for the request. |
| `X-Proxy-Candidate-Count` | Number of failover candidates for OpenAI-compatible requests. |

## Authentication

Provider API keys authenticate the proxy to upstream providers. They are not the
same as the optional local proxy key.

Set `PROXY_API_KEY` to require a bearer token from local clients:

```bash
PROXY_API_KEY=your-local-proxy-key
```

Then clients must send:

```text
Authorization: Bearer your-local-proxy-key
```

Never commit `.env`. It is intentionally ignored by Git.

## Build and test

```bash
dotnet build
dotnet test
```

Useful focused test commands:

```bash
dotnet test --filter "FullyQualifiedName~ModelSelectionStoreTests"
dotnet test --filter "FullyQualifiedName~ModelCatalogServiceTests"
dotnet test --filter "FullyQualifiedName~ProviderRegistryTests"
```

## Creating a release

Maintainers can create local release packages with:

```powershell
.\scripts\package-release.ps1 -Version vX.Y.Z
```

To publish a GitHub release, push a version tag:

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

The `Release` workflow tests the project, creates self-contained packages for
Windows, Linux, and macOS, and uploads the zip files to the GitHub release.

## Troubleshooting

### The provider does not appear

- Confirm the provider API key is set in `.env`.
- Confirm the proxy was restarted after editing `.env`.
- For `customopenai`, confirm `PROVIDER_CUSTOMOPENAI_BASE_URL` is set.

### The model does not appear

- Confirm the upstream `/models` endpoint returns the model id.
- Confirm `config/model-selection/{provider}.json` has an enabled entry whose
  `match` value matches that upstream model id.
- Restart the proxy after changing JSON.

### The displayed model name is too long

Set `display_name` in the model entry:

```json
{
  "match": "very/long/provider/model/name",
  "display_name": "Short Name",
  "enabled": true
}
```

Then restart the proxy and use `Short Name` in the client.

### Context window metadata is wrong

Update `execution.context_length` and `execution.max_output_tokens` in the
matching model JSON file, restart the proxy, and check `/api/show` or
`/api/tags`.

## Documentation

- `docs/API.md` - Endpoint reference.
- `docs/CONFIGURATION.md` - Additional configuration details.
- `docs/ARCHITECTURE.md` - Internal architecture.
- `docs/TESTING.md` - Test architecture and commands.
- `docs/DEPLOYMENT.md` - Deployment notes.
- `README.zh-CN.md` - Simplified Chinese README.

## License

Licensed under the Apache License 2.0. See [LICENSE](LICENSE).

## Acknowledgements

This project was forked from [iqmeta/copilot-ollama-multi-provider-ai-proxy](https://github.com/iqmeta/copilot-ollama-multi-provider-ai-proxy) and has undergone extensive modifications.
