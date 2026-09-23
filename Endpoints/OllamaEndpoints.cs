using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class OllamaEndpoints
{
    internal static IEndpointRouteBuilder MapOllamaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/version", () => Results.Json(new { version = "0.6.4" }, JsonDefaults.SnakeCase));

        app.MapGet("/api/tags", (HttpContext ctx, ModelCatalogService modelCatalog, ProviderRegistry providerRegistry, ModelSelectionStore modelSelectionStore) =>
        {

            // Keep the configured default model visible even if provider discovery fails.
            modelCatalog.EnsureDefaultModelPresent(ctx.RequestAborted);

            // Build /api/tags strictly from enabled model-selection entries so the
            // Copilot BYOM list stays coherent with config/model-selection/*.json.
            // Only include providers that are currently active (API key configured).
            HashSet<string> activeProviders = new(
                providerRegistry.Providers.Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);

            List<(string Provider, string Model, int Priority, string? DisplayName)> configuredEnabled = [];
            foreach (string providerName in activeProviders)
            {
                ModelSelectionEntry[] entries = modelSelectionStore.GetProviderModelSelections(providerName);

                foreach (ModelSelectionEntry entry in entries)
                {
                    if (modelSelectionStore.FindExactModelSelectionEntry(entry.Match, providerName) is null)
                        continue;

                    configuredEnabled.Add((providerName, entry.Match, entry.Priority, entry.DisplayName));
                }
            }

            // Keep vendor prefixes so distinct upstream identities cannot collapse
            // into the same display name. Explicit custom labels retain their priority.
            var curated = configuredEnabled
                .Select(x => new
                {
                    x.Provider,
                    x.Model,
                    x.Priority,
                    x.DisplayName,
                    DisplayModel = string.IsNullOrWhiteSpace(x.DisplayName)
                        ? x.Model.Trim()
                        : x.DisplayName.Trim()
                })
                .GroupBy(x => $"{x.Provider.ToLowerInvariant()}::{x.DisplayModel.ToLowerInvariant()}")
                .Select(g => g
                    .OrderBy(x => x.Priority)
                    .ThenBy(x => x.Model.Contains('/') ? 1 : 0)
                    .ThenBy(x => x.Model.Length)
                    .First())
                .OrderBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Priority)
                .ThenBy(x => x.DisplayModel, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Json(new
            {
                models = curated.Select(x =>
                {
                    string providerPrefix = x.Provider.ToUpperInvariant();
                    bool hasCustomDisplayName = !string.IsNullOrWhiteSpace(x.DisplayName);
                    string displayName = $"{providerPrefix} - {x.DisplayModel}";
                    string routedModel = hasCustomDisplayName ? x.DisplayModel : x.Model;
                    string upstreamModel = x.Model;
                    // Use a provider-qualified alias so that when the client sends this back
                    // as the model name, the proxy routes it uniquely to the correct provider
                    // instead of falling back to the default (e.g. DeepSeek).
                    string qualifiedModel = $"{routedModel}@{x.Provider}:latest";
                    string qualifiedUpstreamModel = $"{upstreamModel}@{x.Provider}:latest";

                    (int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) p = modelCatalog.GetModelProfile(upstreamModel, x.Provider);
                    return new
                    {
                        name = displayName + ":latest",
                        // Both client-facing fields preserve the provider identity.
                        model = qualifiedUpstreamModel,
                        // Keep the provider-qualified alias in case clients need it for routing.
                        aliases = new[] { routedModel, qualifiedModel, upstreamModel, qualifiedUpstreamModel }
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        modified_at = DateTime.UtcNow.ToString("o"),
                        size = 3_826_793_677L,
                        digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
                        details = new
                        {
                            parent_model = "",
                            format = "api",
                            family = p.Family,
                            families = new[] { p.Family },
                            parameter_size = "api",
                            quantization_level = "none"
                        },
                        capabilities = p.Capabilities,
                            context_length = p.ContextLength,
                            max_output_tokens = p.MaxOutputTokens,
                            input_token_limit = p.ContextLength,
                            output_token_limit = p.MaxOutputTokens,
                            supports_tools = p.SupportsTools,
                            supports_tool_calls = p.SupportsTools,
                            supports_vision = p.SupportsVision,
                            supports_images = p.SupportsVision,
                            model_info = new Dictionary<string, object?>
                            {
                                ["general.architecture"] = p.Family,
                                ["general.basename"] = upstreamModel,
                                ["general.context_length"] = p.ContextLength,
                                ["context_length"] = p.ContextLength,
                                ["max_output_tokens"] = p.MaxOutputTokens,
                                ["input_token_limit"] = p.ContextLength,
                                ["output_token_limit"] = p.MaxOutputTokens,
                                ["supports_tools"] = p.SupportsTools,
                                ["supports_tool_calls"] = p.SupportsTools,
                                ["supports_vision"] = p.SupportsVision,
                                ["supports_images"] = p.SupportsVision
                            }
                    };
                }).ToArray()
            }, JsonDefaults.SnakeCase);
        });

        app.MapGet("/api/show", (HttpContext ctx, string? model, ModelCatalogService modelCatalog, ProviderRegistry providerRegistry, OllamaResponseBuilder ollamaResponseBuilder) =>
        {
            // Shared resolver: /api/tags names ("PROVIDER - model:latest"), qualified
            // aliases and bare ids all normalize to the same canonical model here.
            ResolvedRoute route = modelCatalog.ResolveRoute(model);
            return route.Error != RouteErrorKind.None
                ? Results.Json(new { error = route.ErrorMessage }, statusCode: 404)
                : Results.Json(ollamaResponseBuilder.BuildOllamaShowResponse(route.Model, route.Candidates[0].Provider.Name), JsonDefaults.SnakeCase);
        });

        app.MapPost("/api/show", async (HttpContext ctx, ModelCatalogService modelCatalog, ProviderRegistry providerRegistry, OllamaResponseBuilder ollamaResponseBuilder) =>
        {
            using StreamReader reader = new(ctx.Request.Body);
            string body = await reader.ReadToEndAsync(ctx.RequestAborted);
            string? model = null;
            try
            {
                using JsonDocument d = JsonDocument.Parse(body);
                if (d.RootElement.ValueKind != JsonValueKind.Object)
                    return Results.Json(new { error = "Invalid JSON request body." }, statusCode: 400);
                if (d.RootElement.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String)
                    model = m.GetString();
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "Invalid JSON request body." }, statusCode: 400);
            }

            ResolvedRoute route = modelCatalog.ResolveRoute(model);
            return route.Error != RouteErrorKind.None
                ? Results.Json(new { error = route.ErrorMessage }, statusCode: 404)
                : Results.Json(ollamaResponseBuilder.BuildOllamaShowResponse(route.Model, route.Candidates[0].Provider.Name), JsonDefaults.SnakeCase);
        });

        app.MapPost("api/chat", async (
            HttpContext ctx,
            ProviderRegistry providerRegistry,
            ModelCatalogService modelCatalog,
            ChatStreamingService chatStreaming,
            RequestTransformer requestTransformer) =>
        {
            CancellationToken ct = ctx.RequestAborted;
            using StreamReader reader = new(ctx.Request.Body);
            string body = await reader.ReadToEndAsync(ct);

            // A malformed body is a client error (400), never a 500 with a stack trace.
            JsonDocument? parsedDoc;
            try
            {
                parsedDoc = JsonDocument.Parse(body);
                if (parsedDoc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    parsedDoc.Dispose();
                    parsedDoc = null;
                }
            }
            catch (JsonException)
            {
                parsedDoc = null;
            }

            if (parsedDoc is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":\"Invalid JSON request body.\"}", ct);
                return;
            }

            using JsonDocument doc = parsedDoc;
            JsonElement root = doc.RootElement;
            string? invalidParameter = ChatProtocol.ValidateRequest(root, ollama: true);
            if (invalidParameter != null)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = $"Invalid field: {invalidParameter}." }, ct);
                return;
            }
            bool isStream = !root.TryGetProperty("stream", out JsonElement sp) || sp.GetBoolean();

            string ollamaRequestedModel = root.TryGetProperty("model", out JsonElement om) && om.ValueKind == JsonValueKind.String
                ? om.GetString()! : providerRegistry.DefaultModel;

            // Same central routing as /v1/chat/completions: an explicit
            // "model@provider" hint is strict and never falls back.
            ResolvedRoute route = modelCatalog.ResolveRoute(ollamaRequestedModel);
            ModelRequestLogging.SetRoute(ctx, route, isStream);
            if (route.Error != RouteErrorKind.None || route.Candidates.Count == 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "application/json";
                string message = route.ErrorMessage ?? $"No provider candidate available for model '{route.Model}'.";
                await ctx.Response.WriteAsync($"{{\"error\":{JsonSerializer.Serialize(message)}}}", ct);
                return;
            }

            string ollamaEffectiveModel = route.Model;
            (ProviderInfo ollamaProvider, string ollamaUpstreamModel) = route.Candidates[0];
            ModelExecutionConfig ollamaExec = modelCatalog.GetExecutionConfigForModel(
                ollamaEffectiveModel,
                ollamaProvider.Name);

            // ── Diagnostic headers ───────────────────────────────────────
            ctx.Response.Headers["X-Proxy-Requested-Model"] = route.RequestedModel;
            ctx.Response.Headers["X-Proxy-Normalized-Model"] = route.NormalizedModel;
            ctx.Response.Headers["X-Proxy-Resolved-Model"] = route.Model;
            ctx.Response.Headers["X-Proxy-Upstream-Model"] = ollamaUpstreamModel;
            ctx.Response.Headers["X-Proxy-Provider"] = ollamaProvider.Name;
            ctx.Response.Headers["X-Proxy-Fallback-Used"] = route.FallbackUsed ? "true" : "false";
            if (route.ProviderHint is not null)
            {
                ctx.Response.Headers["X-Proxy-Provider-Hint"] = route.ProviderHint;
            }

            using CancellationTokenSource? ollamaTimeoutCts = modelCatalog.CreateModelTimeoutCts(
                ollamaEffectiveModel,
                ollamaProvider.Name,
                ct);
            CancellationToken ollamaCt = ollamaTimeoutCts?.Token ?? ct;

            try
            {
                // Native options use the same execution policy without discarding native-only fields.
                if (ollamaProvider.Capabilities.ApiFormat == ApiFormat.Ollama)
                {
                    string upstreamBody = ChatProtocol.ApplyNativeDefaults(body, ollamaUpstreamModel, isStream, ollamaExec);
                    ModelRequestLogging.Attempt(ctx, ollamaProvider, ollamaUpstreamModel);

                    if (!isStream)
                    {
                        using StringContent ollamaContent = new(upstreamBody, Encoding.UTF8, "application/json");
                        using HttpRequestMessage ollamaRequest = new(HttpMethod.Post, ollamaProvider.Capabilities.ChatPath) { Content = ollamaContent };
                        using HttpResponseMessage ollamaResp = await ollamaProvider.Client.SendAsync(ollamaRequest, ollamaCt);
                        string ollamaRespBody = await ollamaResp.Content.ReadAsStringAsync(ollamaCt);
                        // Fallback: copy `thinking` into `content` for reasoning models that leave content empty.
                        ollamaRespBody = EnsureOllamaContentFromThinking(ollamaRespBody);
                        if (!ollamaResp.IsSuccessStatusCode) ModelRequestLogging.Failure(ctx, (int)ollamaResp.StatusCode, "upstream_http");
                        ctx.Response.StatusCode = (int)ollamaResp.StatusCode;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(ollamaRespBody, ct);
                        return;
                    }

                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/x-ndjson";
                    ctx.Response.Headers["X-Accel-Buffering"] = "no";

                    using StringContent ollamaStreamContent = new(upstreamBody, Encoding.UTF8, "application/json");
                    using HttpRequestMessage ollamaStreamReq = new(HttpMethod.Post, ollamaProvider.Capabilities.ChatPath) { Content = ollamaStreamContent };
                    using HttpResponseMessage ollamaStreamResp = await ollamaProvider.Client.SendAsync(
                        ollamaStreamReq, HttpCompletionOption.ResponseHeadersRead, ollamaCt);

                    if (!ollamaStreamResp.IsSuccessStatusCode)
                    {
                        ModelRequestLogging.Failure(ctx, (int)ollamaStreamResp.StatusCode, "upstream_http");
                        string errBody = await ollamaStreamResp.Content.ReadAsStringAsync(ollamaCt);
                        ctx.Response.StatusCode = (int)ollamaStreamResp.StatusCode;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(errBody, ct);
                        return;
                    }

                    await chatStreaming.StreamNdjsonPassthrough(ollamaStreamResp, ctx.Response, ollamaCt);
                    return;
                }

                // ── Convert Ollama → OpenAI request ──────────────────────────
                string openAiBody;
                try { openAiBody = ConvertOllamaToOpenAi(body, ollamaUpstreamModel, isStream); }
                catch (JsonException)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Tool result has no matching call." }, ct);
                    return;
                }
                using (JsonDocument openAiDoc = JsonDocument.Parse(openAiBody))
                    openAiBody = requestTransformer.ModifyRequest(openAiDoc) ?? openAiBody;

                // Apply execution defaults with provider-aware parameter filtering
                openAiBody = requestTransformer.ApplyExecutionDefaults(
                    openAiBody,
                    ollamaEffectiveModel,
                    ollamaProvider.Capabilities,
                    ollamaProvider.Name);

                ModelRequestLogging.Attempt(ctx, ollamaProvider, ollamaUpstreamModel);
                using StringContent reqContent = new(openAiBody, Encoding.UTF8, "application/json");

                if (!isStream)
                {
                    using HttpRequestMessage request = new(HttpMethod.Post, ollamaProvider.Capabilities.ChatPath) { Content = reqContent };
                    using HttpResponseMessage resp = await ollamaProvider.Client.SendAsync(request, ollamaCt);
                    string respBody = await resp.Content.ReadAsStringAsync(ollamaCt);

                    if (!resp.IsSuccessStatusCode)
                    {
                        ModelRequestLogging.Failure(ctx, (int)resp.StatusCode, "upstream_http");
                        ctx.Response.StatusCode = (int)resp.StatusCode;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(respBody, ct);
                        return;
                    }

                    using JsonDocument odoc = JsonDocument.Parse(respBody);
                    if (odoc.RootElement.ValueKind != JsonValueKind.Object || !odoc.RootElement.TryGetProperty("choices", out var choices)
                        || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0
                        || choices[0].ValueKind != JsonValueKind.Object || !choices[0].TryGetProperty("message", out var msg)
                        || msg.ValueKind != JsonValueKind.Object)
                        throw new JsonException("Missing upstream message.");
                    Dictionary<string, object?> ollamaResp = new()
                    {
                        ["model"] = ollamaEffectiveModel,
                        ["created_at"] = DateTime.UtcNow.ToString("o"),
                        ["message"] = new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : ""
                        },
                        ["done"] = true,
                        ["done_reason"] = choices[0].TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String ? finish.GetString() : "stop"
                    };
                    if (msg.TryGetProperty("tool_calls", out JsonElement tcs))
                        ((Dictionary<string, object?>)ollamaResp["message"]!)["tool_calls"] = ChatProtocol.ToOllamaTools(tcs);

                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(ollamaResp, JsonDefaults.SnakeCase), ct);
                }
                else
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/x-ndjson";
                    ctx.Response.Headers["X-Accel-Buffering"] = "no";

                    using HttpRequestMessage upstreamReq = new(HttpMethod.Post, ollamaProvider.Capabilities.ChatPath)
                    {
                        Content = reqContent
                    };
                    upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                    using HttpResponseMessage upstreamResp = await ollamaProvider.Client.SendAsync(
                        upstreamReq, HttpCompletionOption.ResponseHeadersRead, ollamaCt);

                    if (!upstreamResp.IsSuccessStatusCode)
                    {
                        ModelRequestLogging.Failure(ctx, (int)upstreamResp.StatusCode, "upstream_http");
                        string errBody = await upstreamResp.Content.ReadAsStringAsync(ollamaCt);
                        ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(errBody, ct);
                        return;
                    }

                    await chatStreaming.StreamOllamaAndCache(upstreamResp, ctx.Response, ollamaEffectiveModel, ollamaCt);
                }
            }
            catch (Exception ex) when ((ex is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) && !ct.IsCancellationRequested)
            {
                string kind = ex is OperationCanceledException ? "timeout" : ex is JsonException or InvalidOperationException or KeyNotFoundException ? "invalid_upstream" : "transport";
                ModelRequestLogging.Failure(ctx, null, kind);
                if (ctx.Response.HasStarted)
                {
                    ctx.Abort();
                    return;
                }
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsJsonAsync(new { error = "Upstream response failed." }, ct);
            }
        });

        return app;
    }

    /// <summary>
    /// Converts an Ollama API request body into an OpenAI-compatible request body.
    /// Preserves client-supplied parameters from the Ollama "options" block.
    /// Handles message content with embedded images (converts Ollama format to OpenAI multi-part format).
    /// </summary>
    private static string ConvertOllamaToOpenAi(string ollamaBody, string upstreamModel, bool isStream)
    {
        using JsonDocument doc = JsonDocument.Parse(ollamaBody);
        JsonElement root = doc.RootElement;

        using MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);

        writer.WriteStartObject();
        writer.WriteString("model", upstreamModel);
        writer.WriteBoolean("stream", isStream);

        // ── Messages (handle Ollama images → OpenAI multi-part content) ──
        if (root.TryGetProperty("messages", out JsonElement omsgs) && omsgs.ValueKind == JsonValueKind.Array)
        {
            writer.WritePropertyName("messages");
            writer.WriteStartArray();

            var pendingCalls = new List<(string Name, string Id)>();
            foreach (JsonElement msg in omsgs.EnumerateArray())
            {
                writer.WriteStartObject();
                bool hasImages = msg.TryGetProperty("images", out JsonElement imgs) && imgs.GetArrayLength() > 0;
                bool isToolResult = msg.TryGetProperty("role", out var role) && role.GetString() == "tool";
                if (isToolResult)
                {
                    string? name = msg.TryGetProperty("tool_name", out var toolName) ? toolName.GetString() : null;
                    string? id = msg.TryGetProperty("tool_call_id", out var callId) && callId.ValueKind == JsonValueKind.String ? callId.GetString() : null;
                    // Consume both explicit and inferred associations so a later result
                    // cannot accidentally reuse a call that already received its result.
                    int index = id != null ? pendingCalls.FindIndex(c => c.Id == id)
                        : name == null ? (pendingCalls.Count > 0 ? 0 : -1) : pendingCalls.FindIndex(c => c.Name == name);
                    if (index < 0) throw new JsonException("Tool result has no matching call.");
                    writer.WriteString("tool_call_id", pendingCalls[index].Id);
                    pendingCalls.RemoveAt(index);
                }
                if (hasImages && !msg.TryGetProperty("content", out _))
                {
                    writer.WritePropertyName("content");
                    writer.WriteStartArray();
                    foreach (var image in imgs.EnumerateArray())
                        JsonSerializer.Serialize(writer, new { type = "image_url", image_url = new { url = $"data:image/png;base64,{image.GetString()}" } });
                    writer.WriteEndArray();
                }

                foreach (JsonProperty mp in msg.EnumerateObject())
                {
                    if (mp.NameEquals("content") && hasImages)
                    {
                        string text = mp.Value.GetString() ?? "";
                        writer.WritePropertyName("content");
                        writer.WriteStartArray();
                        writer.WriteStartObject();
                        writer.WriteString("type", "text");
                        writer.WriteString("text", text);
                        writer.WriteEndObject();
                        foreach (JsonElement img in imgs.EnumerateArray())
                        {
                            string url = img.GetString()!;
                            if (!url.StartsWith("data:") && !url.StartsWith("http"))
                                url = $"data:image/png;base64,{url}";
                            writer.WriteStartObject();
                            writer.WriteString("type", "image_url");
                            writer.WritePropertyName("image_url");
                            writer.WriteStartObject();
                            writer.WriteString("url", url);
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    else if (mp.NameEquals("tool_calls"))
                    {
                        var calls = ChatProtocol.ToOpenAiTools(mp.Value);
                        foreach (var call in calls)
                            pendingCalls.Add((call!["function"]!["name"]!.GetValue<string>(), call["id"]!.GetValue<string>()));
                        writer.WritePropertyName("tool_calls");
                        calls.WriteTo(writer);
                    }
                    else if (mp.NameEquals("tool_name") || (isToolResult && mp.NameEquals("tool_call_id"))) continue;
                    else if (mp.NameEquals("images"))
                    {
                        // Already handled inside "content" above
                        continue;
                    }
                    else
                    {
                        mp.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        // ── Tools ──
        if (root.TryGetProperty("tools", out JsonElement tools))
        {
            writer.WritePropertyName("tools");
            tools.WriteTo(writer);
        }

        // ── Preserve client-supplied parameters from Ollama's "options" block ──
        // Ollama format: "options": { "temperature": 0.7, "top_p": 0.9, "num_predict": 4096 }
        bool hasOptionsBlock = root.TryGetProperty("options", out JsonElement options) && options.ValueKind == JsonValueKind.Object;

        if (hasOptionsBlock)
        {
            foreach (JsonProperty opt in options.EnumerateObject())
            {
                if (opt.NameEquals("num_predict"))
                {
                    writer.WriteNumber("max_tokens", opt.Value.GetInt32());
                }
                else if (opt.NameEquals("num_ctx") || opt.NameEquals("repeat_penalty") ||
                         opt.NameEquals("repeat_last_n") || opt.NameEquals("mirostat") ||
                         opt.NameEquals("mirostat_tau") || opt.NameEquals("mirostat_eta") ||
                         opt.NameEquals("penalize_newline") ||
                         opt.NameEquals("tfs_z") || opt.NameEquals("typical_p") ||
                         opt.NameEquals("use_mmap") || opt.NameEquals("use_mlock") ||
                         opt.NameEquals("num_thread") || opt.NameEquals("num_gpu") ||
                         opt.NameEquals("num_batch") ||
                         opt.NameEquals("num_keep") || opt.NameEquals("f16_kv"))
                {
                    // Skip Ollama-specific options that have no OpenAI equivalent
                    continue;
                }
                else
                {
                    opt.WriteTo(writer);
                }
            }
        }
        else
        {
            // No options block — check for top-level Ollama params (model, stream, messages, etc. already handled)
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                string name = prop.Name;
                if (name == "model" || name == "stream" || name == "messages" ||
                    name == "tools" || name == "options" || name == "keep_alive" ||
                    name == "format" || name == "raw")
                {
                    continue;
                }
                prop.WriteTo(writer);
            }
        }

        if (root.TryGetProperty("format", out var format))
        {
            writer.WritePropertyName("response_format");
            if (format.ValueKind == JsonValueKind.String)
                JsonSerializer.Serialize(writer, new { type = "json_object" });
            else
                JsonSerializer.Serialize(writer, new { type = "json_schema", json_schema = new { name = "response", schema = format } });
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Reasoning models on Ollama Cloud may return `thinking` with empty `content`.
    /// This copies the `thinking` field into `content` when content is empty or missing.
    /// </summary>
    private static string EnsureOllamaContentFromThinking(string responseBody)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("message", out JsonElement msg) || msg.ValueKind != JsonValueKind.Object)
                return responseBody;

            JsonElement contentElem = msg.TryGetProperty("content", out JsonElement ce) ? ce : default;
            bool contentMissing = contentElem.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
            bool contentEmpty = !contentMissing && contentElem.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(contentElem.GetString());
            if (!contentMissing && !contentEmpty)
                return responseBody;

            if (!msg.TryGetProperty("thinking", out JsonElement thinkingElem) || thinkingElem.ValueKind != JsonValueKind.String)
                return responseBody;

            string thinking = thinkingElem.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(thinking))
                return responseBody;

            using MemoryStream ms = new();
            using Utf8JsonWriter writer = new(ms);
            writer.WriteStartObject();
            bool wroteContent = false;

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (prop.NameEquals("message") && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("message");
                    writer.WriteStartObject();
                    foreach (JsonProperty mp in prop.Value.EnumerateObject())
                    {
                        if (mp.NameEquals("content"))
                        {
                            writer.WriteString("content", thinking);
                            wroteContent = true;
                        }
                        else
                        {
                            mp.WriteTo(writer);
                        }
                    }

                    if (!wroteContent)
                    {
                        writer.WriteString("content", thinking);
                    }

                    writer.WriteEndObject();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return responseBody;
        }
    }
}
