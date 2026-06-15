# Provider integration gap analysis (2026-06-04)

## Audited files
- `Services/ProviderRegistry.cs`
- `Services/ModelCatalogService.cs`
- `Endpoints/OpenAiEndpoints.cs`
- `Endpoints/OllamaEndpoints.cs`
- `Services/ChatStreamingService.cs`

## Findings by provider

### OpenAI-compatible providers (OpenAI / Groq / OpenRouter / NVIDIA)

Status: generally aligned

What is aligned:
- Uses bearer auth in shared `HttpClient` factory
- Uses `/v1/models` for discovery by default
- Uses `/v1/chat/completions` for chat
- Supports non-stream and stream pass-through for SSE

Potential gaps:
1. No provider-specific diagnostics for model discovery failures (`403/404/429`) during catalog refresh.
2. OpenRouter optional headers (e.g. provider metadata) are not set; not strictly required but can improve routing visibility.
3. Current test script timeout (15s) is too aggressive for heavyweight coding/reasoning models and may report false negatives.

### Ollama Cloud

Status: adapted but partially fragile

What is aligned:
- Discovery path switched to `/api/tags`.
- OpenAI endpoint now branches to provider `ollama` and translates to `/api/chat`.
- Native `/api/chat` path supports cloud passthrough.

Potential gaps:
1. `/v1/chat/completions` stream mode for ollama currently synthesizes SSE from a non-stream cloud request; this preserves compatibility but can hide chunk timing behavior.
2. Some models are listable but can return empty content depending on account limits/model availability.

## Cross-provider operational gaps

1. No consolidated provider-by-provider validation script (current script is biased to NIM naming and fixed timeout).
2. No ranking artifact focused on coding/agent models by success/latency/provider.
3. Runtime logs do not include per-provider summary stats for last refresh and rejected models.

## Root-cause hypotheses for current failures seen

- `no_model` for groq/openrouter in prior runs likely due to key/plan-scoped model visibility at the time of refresh.
- `empty_response` on some ollama/open models likely account/model permission or provider behavior on short prompts.
- `timeout` likely from short client timeout in test script, not necessarily hard provider failure.

## Next implementation targets

1. Add provider diagnostics script with provider/model/status/latency evidence in `docs/testing/`.
2. Increase adaptive timeout policy in model probe script and classify timeout separately from auth/permission failures.
3. Produce a coding/agent recommendation artifact from empirical probes.
