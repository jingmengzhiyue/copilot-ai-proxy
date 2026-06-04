# System Architecture

Comprehensive architecture documentation describing the proxy design, components, and data flow.

## Table of Contents

- [Overview](#overview)
- [Component Architecture](#component-architecture)
- [Data Flow](#data-flow)
- [Service Dependencies](#service-dependencies)
- [Configuration Management](#configuration-management)
- [Request Lifecycle](#request-lifecycle)
- [Failing Over](#failing-over)
- [Performance Optimizations](#performance-optimizations)

---

## Overview

The proxy is a high-performance ASP.NET Core minimal API application that bridges GitHub Copilot, Cursor, Continue.dev, and Ollama clients to multiple AI providers (DeepSeek, OpenAI, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud).

### Design Principles

1. **Multi-Provider Agnostic** — One API, N backends
2. **Zero Allocation Streaming** — Pass-through SSE without buffering
3. **Configuration-Driven** — Model defaults and provider routing via JSON
4. **Testability** — All services are unit-testable with in-memory fixtures
5. **Production-Ready** — Connection pooling, HTTP/2, timeout handling

### Technology Stack

- **Runtime:** .NET 10
- **Web Framework:** ASP.NET Core Minimal APIs
- **Serialization:** System.Text.Json
- **HTTP Client:** SocketsHttpHandler with connection pooling
- **Testing:** xUnit with WebApplicationFactory

---

## Component Architecture

### Application Startup (Program.cs)

```csharp
// 1. Create slim builder (minimal middleware overhead)
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

// 2. Register core services
builder.Services.AddSingleton<ProviderHttpClientFactory>();      // HTTP client creation
builder.Services.AddSingleton<ProviderRegistry>();               // Provider discovery
builder.Services.AddSingleton<ModelSelectionStore>();            // Model metadata loading
builder.Services.AddSingleton<ModelCatalogService>();            // Model availability
builder.Services.AddSingleton<ReasoningCacheService>();          // DeepSeek reasoning cache
builder.Services.AddSingleton<RequestTransformer>();             // Parameter filtering
builder.Services.AddSingleton<OllamaResponseBuilder>();          // Format conversion
builder.Services.AddSingleton<ChatStreamingService>();           // Streaming handler

// 3. Add background service for benchmarking / monitoring
builder.Services.AddHostedService<ProviderBenchmarkService>();

// 4. Build and map endpoints
app.MapOpenAiEndpoints();  // /v1/models, /v1/chat/completions
app.MapOllamaEndpoints();  // /api/tags, /api/chat, /api/show
app.MapHealthEndpoints();  // /health
```

### Core Components

#### 1. **ProviderHttpClientFactory**

**Responsibility:** Create and cache HTTP clients for each provider with proper authentication.

**Key Methods:**
```csharp
public HttpClient GetHttpClient(string providerName)
{
    // Returns pre-configured HttpClient for the provider
    // - Base URL set correctly
    // - Authorization header injected
    // - Connection pooling configured
}
```

**Implementations:**
- DeepSeek: `Authorization: Bearer {PROVIDER_DEEPSEEK_API_KEY}`
- OpenAI: `Authorization: Bearer {PROVIDER_OPENAI_API_KEY}`
- NVIDIA: Custom headers for NIM authentication
- Groq, OpenRouter, Ollama Cloud: Standard Bearer tokens

**Performance Features:**
- SocketsHttpHandler with 256 connections/server
- HTTP/2 multiplexing
- Connection reuse

#### 2. **ProviderRegistry**

**Responsibility:** Discover available providers and resolve model-to-provider mapping.

**Key Methods:**
```csharp
public List<string> ResolveCandidates(string requestedModel)
{
    // Returns ordered list of providers to try
    // 1. Direct model mapping (deepseek-v4-pro → deepseek)
    // 2. NVIDIA NIM fallback (if model not matched)
    // 3. Default provider (if configured)
}

public string DefaultModel { get; }
public IReadOnlyList<Provider> Providers { get; }
```

**Provider List:**
- DeepSeek (`https://api.deepseek.com/v1`)
- OpenAI (`https://api.openai.com/v1`)
- NVIDIA NIM (`https://api.nvcf.nvidia.com/v2`)
- Groq (`https://api.groq.com/openai/v1`)
- OpenRouter (`https://openrouter.ai/api/v1`)
- Ollama Cloud (`https://api.ollama.cloud/api`)

#### 3. **ModelSelectionStore**

**Responsibility:** Load and parse model metadata from `config/model-selection/*.json`.

**Key Methods:**
```csharp
public ModelConfig GetModelConfig(string modelId)
{
    // Returns model execution defaults:
    // - context_length, max_output_tokens
    // - temperature, max_tokens, reasoning_effort
    // - timeout_seconds, supports_tools, supports_vision
}
```

**File Structure:**
```
config/model-selection/
├── deepseek.json
├── openai.json
├── nvidia.json
├── groq.json
├── openrouter.json
└── ollamacloud.json
```

#### 4. **ModelCatalogService**

**Responsibility:** Maintain a live catalog of available models from all providers.

**Key Methods:**
```csharp
public async Task RefreshAvailableModels(CancellationToken ct)
{
    // On startup: Fetch model list from each provider API
    // Caches result in memory
    // Used by /v1/models and /api/tags endpoints
}

public IReadOnlyList<string> AvailableModels { get; }
public ModelInfo GetModelInfo(string modelId) { ... }
```

**Startup Flow:**
1. App starts
2. `RefreshAvailableModels()` called with default timeout
3. For each enabled provider, call `/v1/models` (or equivalent)
4. Merge all results, apply configuration defaults
5. Cache in memory for entire app lifetime

#### 5. **ReasoningCacheService**

**Responsibility:** Cache DeepSeek `reasoning_content` for multi-turn conversations.

**Key Methods:**
```csharp
public void CacheReasoning(string conversationId, string thinkingContent)
{
    // Store thinking content keyed by conversation ID
}

public string? GetCachedReasoning(string conversationId)
{
    // Retrieve cached thinking for this conversation
    // Returns null if not cached
}
```

**Lifecycle:**
1. DeepSeek reasoning response arrives with `thinking` field
2. Extract and store in `ReasoningCacheService`
3. On next user message in same conversation, retrieve and reuse
4. Inject into next DeepSeek request for context continuity

#### 6. **RequestTransformer**

**Responsibility:** Normalize and filter request parameters based on provider capabilities.

**Key Methods:**
```csharp
public string ApplyExecutionDefaults(
    string requestJson,
    string modelId,
    string? providerOverride = null)
{
    // 1. Parse JSON
    // 2. Inject default parameters from ModelSelectionStore
    // 3. Filter parameters based on provider support
    //    - Remove top_k if OpenAI
    //    - Remove reasoning_effort if not DeepSeek
    //    - Omit top_p if reasoning_effort present
    // 4. Validate parameter ranges
    // 5. Return transformed JSON
}

public string TransformRequest(string provider, string request)
{
    // Transform request for specific provider format
    // (currently pass-through, can be extended)
}
```

**Parameter Filtering Matrix:**
| Provider | temperature | top_p | top_k | reasoning_effort |
|----------|-------------|-------|-------|------------------|
| DeepSeek | ✅ | ❌ (if reasoning) | ✅ | ✅ |
| OpenAI | ✅ | ✅ | ❌ | ✅ (o-series) |
| NVIDIA | ✅ | ✅ | ✅ | ❌ |
| Groq | ✅ | ✅ | ✅ | ❌ |
| OpenRouter | ✅ | ✅ | ✅ | ✅ |

#### 7. **OllamaResponseBuilder**

**Responsibility:** Convert DeepSeek/OpenAI responses into Ollama-compatible format.

**Key Methods:**
```csharp
public OllamaResponse BuildFromOpenAi(OpenAiResponse openAiResponse)
{
    // Map OpenAI response fields to Ollama fields
    // - message.content → message.content
    // - usage.prompt_tokens → prompt_eval_count
    // - usage.completion_tokens → eval_count
}

public OllamaStreamResponse BuildStreamFromOpenAi(OpenAiStreamChunk chunk)
{
    // Convert SSE chunk to NDJSON format
}
```

**Response Mapping:**
| OpenAI | Ollama |
|--------|--------|
| `message.content` | `message.content` |
| `usage.prompt_tokens` | `prompt_eval_count` |
| `usage.completion_tokens` | `eval_count` |
| `choices[0].finish_reason` | `done` (true if "stop") |

#### 8. **ChatStreamingService**

**Responsibility:** Handle streaming responses with proper buffering and format conversion.

**Key Methods:**
```csharp
public async Task StreamChatCompletion(
    HttpContext ctx,
    OpenAiRequest request,
    string provider,
    CancellationToken ct)
{
    // 1. Transform request via RequestTransformer
    // 2. Forward to upstream provider
    // 3. If Ollama endpoint requested:
    //    - Convert SSE → NDJSON on-the-fly
    // 4. Stream response to client with minimal buffering
}
```

**Streaming Format Handling:**
- **OpenAI API:** Server-Sent Events (SSE) with `data: {...}\n\n` delimiter
- **Ollama API:** NDJSON with one complete object per line
- **Proxy:** Zero-copy pass-through with on-demand format conversion

#### 9. **ProviderBenchmarkService**

**Responsibility:** Background service that periodically measures provider response times and model availability.

**Key Features:**
- Runs on app startup
- Configurable refresh interval
- Logs metrics to console
- Detects provider outages early
- Non-blocking (background hosted service)

---

## Data Flow

### Request Flow: POST /v1/chat/completions (Streaming)

```
Client (GitHub Copilot)
    │
    ├─> POST /v1/chat/completions
    │   { "model": "deepseek-v4-pro", "messages": [...], "stream": true }
    │
    ▼
OpenAiEndpoints.cs
    │
    ├─> RequestTransformer.ApplyExecutionDefaults()
    │   (Inject temperature, max_tokens, reasoning_effort)
    │
    ├─> ProviderRegistry.ResolveCandidates("deepseek-v4-pro")
    │   Returns: ["deepseek", "openai"]  (deepseek is primary)
    │
    ├─> Retry Loop:
    │   │
    │   ├─> Attempt 1: DeepSeek Provider
    │   │   ├─> Forward to https://api.deepseek.com/v1/chat/completions
    │   │   ├─> Receive SSE stream
    │   │   ├─> Cache reasoning_content if present
    │   │   ├─> Stream to client (pass-through)
    │   │   └─> Response
    │   │
    │   └─> (If failed, retry with OpenAI)
    │
    ▼
Client (Stream complete)
```

### Request Flow: POST /api/chat (Ollama Format)

```
Client (Visual Studio BYOM)
    │
    ├─> POST /api/chat
    │   { "model": "deepseek-v4-pro", "messages": [...], "stream": false }
    │
    ▼
OllamaEndpoints.cs
    │
    ├─> ModelCatalogService.GetModelInfo()
    │   (Verify model exists)
    │
    ├─> RequestTransformer.ApplyExecutionDefaults()
    │   (Transform Ollama format → OpenAI format)
    │
    ├─> ProviderRegistry.ResolveCandidates()
    │   (Find provider)
    │
    ├─> Forward to upstream
    │   (e.g., https://api.deepseek.com/v1/chat/completions)
    │
    ├─> Receive OpenAI response
    │   {
    │     "choices": [{"message": {"content": "..."}}],
    │     "usage": {"prompt_tokens": 42, "completion_tokens": 10}
    │   }
    │
    ├─> OllamaResponseBuilder.BuildFromOpenAi()
    │   (Convert to Ollama format)
    │
    ▼
Client (Ollama response)
    {
      "model": "deepseek-v4-pro",
      "message": {"content": "..."},
      "prompt_eval_count": 42,
      "eval_count": 10,
      "done": true
    }
```

---

## Service Dependencies

### Dependency Injection Graph

```
Program.cs
├─> ProviderHttpClientFactory (Singleton)
│   └─ Creates HttpClient per provider
│
├─> ProviderRegistry (Singleton)
│   ├─ Requires: ProviderHttpClientFactory
│   └─ Resolves: Model → Provider mapping
│
├─> ModelSelectionStore (Singleton)
│   └─ Loads: config/model-selection/*.json
│
├─> ModelCatalogService (Singleton)
│   ├─ Requires: ProviderRegistry, ModelSelectionStore
│   └─ Maintains: Live model catalog from all providers
│
├─> ReasoningCacheService (Singleton)
│   └─ Caches: DeepSeek reasoning_content
│
├─> RequestTransformer (Singleton)
│   ├─ Requires: ModelCatalogService, ReasoningCacheService
│   └─ Transforms: Requests per provider specs
│
├─> OllamaResponseBuilder (Singleton)
│   └─ Builds: Ollama responses from OpenAI
│
├─> ChatStreamingService (Singleton)
│   ├─ Requires: RequestTransformer, ChatStreamingService
│   └─ Handles: SSE/NDJSON streaming
│
└─> ProviderBenchmarkService (Hosted Service)
    ├─ Requires: ProviderRegistry, ModelCatalogService
    └─ Monitors: Provider health and metrics
```

### Singleton Rationale

All services are **singletons** (app-scoped):
- **HttpClient reuse:** Enable connection pooling and HTTP/2
- **Model metadata caching:** Avoid reloading JSON on every request
- **Reasoning cache:** Per-conversation state persists across requests
- **Performance:** No allocation overhead from repeated DI lookups

---

## Configuration Management

### Configuration Sources (Priority Order)

1. **Environment Variables** (highest priority)
   - `PROVIDER_DEEPSEEK_API_KEY`, `PROXY_PORT`, etc.

2. **.env File**
   - Read by `DotEnv.Load()` in `Program.cs` if present

3. **appsettings.json**
   - Project default configuration

4. **Hardcoded Defaults** (lowest priority)
   - `DefaultModel = "deepseek-v4-pro"`
   - `DefaultPort = 11434`

### JSON Configuration Files

Located in `config/model-selection/`:

```json
{
  "provider": "deepseek",
  "models": [
    {
      "match": "deepseek-v4-pro",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1048576,
        "max_output_tokens": 384000,
        "temperature": 0.2,
        "max_tokens": 8192,
        "reasoning_effort": "high",
        "timeout_seconds": 180
      }
    }
  ]
}
```

**Loaded by:** `ModelSelectionStore` on app startup  
**Used by:** `RequestTransformer` to inject defaults

---

## Request Lifecycle

### 1. Request Arrives

```
POST /v1/chat/completions
{
  "model": "deepseek-v4-pro",
  "messages": [{role: "user", content: "hi"}],
  "stream": true
}
```

### 2. Endpoint Handler

```csharp
app.MapPost("/v1/chat/completions", async (
    HttpContext ctx,
    JsonElement body,
    ModelCatalogService modelCatalog,
    RequestTransformer transformer,
    ProviderRegistry registry,
    ChatStreamingService streaming) =>
{
    // 1. Parse model from request
    string model = body.GetProperty("model").GetString()!;

    // 2. Verify model exists
    if (!modelCatalog.AvailableModels.Contains(model))
        return Results.BadRequest($"Unknown model: {model}");

    // 3. Transform request (inject defaults, filter params)
    string transformed = transformer.ApplyExecutionDefaults(
        body.GetRawText(), model);

    // 4. Stream response
    await streaming.StreamChatCompletion(ctx, model, transformed);
});
```

### 3. Parameter Injection (RequestTransformer)

Input request is missing defaults:
```json
{
  "model": "deepseek-v4-pro",
  "messages": [...]
  // No temperature, max_tokens, reasoning_effort
}
```

After `ApplyExecutionDefaults()`:
```json
{
  "model": "deepseek-v4-pro",
  "messages": [...],
  "temperature": 0.2,           // ← Injected from config
  "max_tokens": 8192,            // ← Injected from config
  "reasoning_effort": "high"     // ← Injected from config
  // Note: top_p is OMITTED (incompatible with reasoning_effort)
}
```

### 4. Provider Resolution

```csharp
List<string> candidates = registry.ResolveCandidates("deepseek-v4-pro");
// Returns: ["deepseek", "openai"]  (deepseek has priority 1)
```

### 5. Retry Loop (Non-streaming)

```csharp
foreach (string provider in candidates)
{
    try
    {
        var response = await httpClient.PostAsync(
            upstream: $"https://api.{provider}.com/v1/chat/completions",
            body: transformed);

        if (response.IsSuccessStatusCode)
            return response;  // ✅ Success
    }
    catch (Exception ex)
    {
        // 🔄 Try next provider
    }
}

// ❌ All providers failed
return Results.BadGateway("All providers failed");
```

### 6. Response Handling

**If Ollama endpoint requested:** Convert response format via `OllamaResponseBuilder`  
**If OpenAI endpoint requested:** Return as-is  
**If streaming:** Pass through SSE chunks with minimal buffering

---

## Failing Over

### Failover Strategy

The proxy implements **graceful fallback** for non-streaming requests:

1. **Primary Provider:** Try first candidate (highest priority)
2. **Secondary Provider:** Try NVIDIA NIM if primary fails
3. **Tertiary:** Try default provider
4. **Final:** Return 502 Bad Gateway if all fail

### Error Conditions That Trigger Failover

- ❌ Network timeout
- ❌ HTTP 401 Unauthorized (bad API key)
- ❌ HTTP 429 Too Many Requests (rate limit)
- ❌ HTTP 500 Internal Server Error
- ❌ HTTP 503 Service Unavailable

### Error Conditions That DO NOT Failover

- ❌ 400 Bad Request (invalid parameter) — user error, no point retrying
- ❌ 404 Not Found (model doesn't exist) — doesn't exist elsewhere

### Streaming Failover Limitation

**Streaming requests do NOT failover** because headers are already sent to client. If the stream fails mid-execution:

```
data: {"choices":[...]}
data: {"choices":[...]}
⚠️ Connection drops
❌ No retry possible (headers already sent)
```

**Workaround:** Client should retry the entire request.

---

## Performance Optimizations

### 1. Connection Pooling

```csharp
// HttpClientFactory uses SocketsHttpHandler
var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 256,  // Pool size
    AutomaticDecompression = DecompressionMethods.All,
    // HTTP/2 is enabled by default
};
```

**Benefits:**
- Connection reuse across requests
- HTTP/2 multiplexing (multiple streams per connection)
- Reduced handshake overhead

### 2. Zero-Copy Streaming

SSE responses are streamed **without buffering**:

```csharp
// ❌ Inefficient: Buffer entire response
byte[] buffer = await response.Content.ReadAsByteArrayAsync();
await ctx.Response.Body.WriteAsync(buffer);

// ✅ Efficient: Stream directly
await response.Content.CopyToAsync(ctx.Response.Body);
```

**Result:** Memory usage is O(chunk_size) not O(total_response_size)

### 3. Slim Builder

```csharp
// Minimal middleware overhead
WebApplication.CreateSlimBuilder(args);  // No logging, auth, etc.

// Manually add only what we need
app.UseOptionalProxyAuthentication(proxyApiKey);
```

**Benefit:** Reduced CPU per request

### 4. JSON Serialization

Uses only `System.Text.Json` with source generators:

```csharp
[JsonSourceGenerationOptions(...)]
[JsonSerializable(typeof(OpenAiRequest))]
[...]
internal partial class JsonSerializerContext : JsonSerializerContext { }
```

**Benefit:** Zero reflection, compile-time code generation

### 5. Model Metadata In-Memory

```csharp
// Loaded once on startup
await modelCatalog.RefreshAvailableModels();

// Cached in memory
public IReadOnlyList<string> AvailableModels { get; private set; } = [];
```

**Benefit:** /v1/models endpoint responds in <1ms

---

## Related Documentation

- [API.md](API.md) — Endpoint specifications
- [CONFIGURATION.md](CONFIGURATION.md) — Configuration reference
- [TESTING.md](TESTING.md) — Test architecture and running tests
