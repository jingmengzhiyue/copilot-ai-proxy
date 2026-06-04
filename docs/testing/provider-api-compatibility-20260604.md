# Provider API compatibility matrix (2026-06-04)

## Scope
Providers analyzed for this proxy:
- OpenAI (OpenAI-compatible baseline)
- Groq
- OpenRouter
- NVIDIA NIM (OpenAI-compatible)
- Ollama Cloud

## Compatibility matrix

| Provider | Base URL (typical) | Auth | Model listing | Chat generation | Streaming format | Notes for proxy adaptation |
|---|---|---|---|---|---|---|
| OpenAI | `https://api.openai.com` | `Authorization: Bearer <key>` | `/v1/models` | `/v1/chat/completions` | SSE (`text/event-stream`) | Baseline contract already implemented by proxy |
| Groq | `https://api.groq.com/openai` | `Authorization: Bearer <key>` | `/v1/models` | `/v1/chat/completions` | SSE | OpenAI-compatible surface; model permissions vary by account |
| OpenRouter | `https://openrouter.ai/api` | `Authorization: Bearer <key>` | `/v1/models` | `/v1/chat/completions` | SSE | OpenAI-compatible, often benefits from optional routing headers |
| NVIDIA NIM | `https://integrate.api.nvidia.com` | `Authorization: Bearer <key>` | `/v1/models` | `/v1/chat/completions` | SSE | OpenAI-compatible; some models can timeout on short limits |
| Ollama Cloud | `https://ollama.com` | `Authorization: Bearer <OLLAMA cloud key>` | `/api/tags` | `/api/chat` | NDJSON (Ollama API) | Requires protocol translation between OpenAI payloads and Ollama payloads |

## Request shape differences

### OpenAI-compatible providers (OpenAI/Groq/OpenRouter/NVIDIA)
- Request fields commonly used by proxy: `model`, `messages`, `stream`, `temperature`, `top_p`, `max_tokens`, `tools`
- Responses expected by proxy: `choices[0].message.content`, optional `tool_calls`, optional streaming chunks in SSE format

### Ollama Cloud
- Listing endpoint: `/api/tags`
- Chat endpoint: `/api/chat`
- Request shape:
  - `model`
  - `messages`
  - `stream`
  - `options` object (for temperature/top_p/num_predict)
- Streaming response is NDJSON, not OpenAI SSE

## Initial adaptation checklist

1. Use provider-specific model listing endpoint:
   - default: `v1/models`
   - ollama cloud: `/api/tags`
2. Use provider-specific chat endpoint:
   - default: `/v1/chat/completions`
   - ollama cloud: `/api/chat`
3. Translate OpenAI request -> Ollama request where needed (`max_tokens` -> `options.num_predict`, etc.)
4. Normalize Ollama response to OpenAI for `/v1/chat/completions` clients
5. Keep native Ollama passthrough for `/api/chat` consumers
6. Validate per-provider model availability with account-scoped credentials

## Coding/agent-oriented model candidates

- OpenAI-compatible families (good coding/agents):
  - `gpt-oss*`
  - `qwen3-coder*`
  - `deepseek-v4*`
  - `nemotron-*` (coding/reasoning variants)
- Ollama Cloud examples:
  - `gpt-oss:120b`, `gpt-oss:20b`
  - `qwen3-coder*` / `deepseek*` variants when available in `/api/tags`

## Evidence source
- Provider docs pages (where accessible from environment)
- In-repo runtime behavior (`/health`, `/v1/models`, `/api/tags`, chat probes)
