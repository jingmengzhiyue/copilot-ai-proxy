using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class OpenAiEndpoints
{
    internal static IEndpointRouteBuilder MapOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", (HttpContext ctx, ModelCatalogService modelCatalog, ProviderRegistry providerRegistry, ModelSelectionStore modelSelectionStore) =>
        {
            // Build a complete list from static config files (always available) plus
            // any models discovered from provider catalogs. The id format MUST match
            // what ProviderRegistry.ResolveModel / ResolveCandidates can actually
            // route: bare "model" and qualified "model@provider" (the internal
            // mapping built by ModelCatalogService). Listing "provider/model" would
            // not be routable on POST /v1/chat/completions.

            List<(string Provider, string Model)> allModels = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            // 1) Enumerate everything already in the live catalog (covers both bare
            //    and qualified aliases that the routing layer actually accepts).
            foreach (string modelId in modelCatalog.AvailableModels)
            {
                if (string.IsNullOrWhiteSpace(modelId))
                    continue;

                string providerName;
                string displayModel;

                int at = modelId.IndexOf('@');
                if (at > 0 && at < modelId.Length - 1)
                {
                    string upstreamPart = modelId[..at];
                    string provPart = modelId[(at + 1)..];
                    displayModel = upstreamPart;
                    providerName = provPart;
                }
                else
                {
                    displayModel = modelId;
                    providerName = providerRegistry.ModelToProvider.TryGetValue(modelId, out ProviderInfo prov)
                        ? prov.Name
                        : "unknown";
                }

                if (seen.Add(modelId))
                {
                    allModels.Add((providerName, modelId));
                }
                // Also surface the bare form when the catalog only registered the
                // qualified one (helps clients that prefer short ids).
                if (modelId.Contains('@'))
                {
                    string bare = modelId[..modelId.IndexOf('@')];
                    if (seen.Add(bare))
                    {
                        allModels.Add((providerName, bare));
                    }
                }
                _ = displayModel; // currently unused beyond the assignments above
            }

            // 2) Add any model known by its upstream id but not present yet
            //    (defensive: catalogs populated outside the discoverer).
            foreach (KeyValuePair<string, ProviderInfo> kv in providerRegistry.ModelToProvider)
            {
                if (seen.Add(kv.Key))
                {
                    allModels.Add((kv.Value.Name, kv.Key));
                }
            }

            foreach (var configured in modelCatalog.GetConfiguredModels())
                if (seen.Add(configured.Model)) allModels.Add(configured);

            // Sort by provider name then model name for stable output.
            allModels = allModels.OrderBy(m => m.Provider, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            return Results.Json(new
            {
                @object = "list",
                data = allModels.Select(m => new
                {
                    id = m.Model,
                    @object = "model",
                    created = 1700000000,
                    owned_by = m.Provider
                }).ToArray()
            }, JsonDefaults.SnakeCase);
        });

        app.MapPost("/v1/chat/completions", async (
            HttpContext ctx,
            ProviderRegistry providerRegistry,
            RequestTransformer requestTransformer,
            ModelCatalogService modelCatalog,
            ChatStreamingService chatStreaming) =>
        {
            CancellationToken ct = ctx.RequestAborted;

            using StreamReader bodyReader = new(ctx.Request.Body, Encoding.UTF8, false, 1024);
            string rawBody = await bodyReader.ReadToEndAsync(ct);

            // A malformed body is a client error (400), never a 500 with a stack trace.
            JsonDocument? parsedDoc;
            try
            {
                parsedDoc = JsonDocument.Parse(rawBody);
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
                await ctx.Response.WriteAsync(
                    "{\"error\":{\"message\":\"Invalid JSON request body.\",\"type\":\"invalid_request_error\",\"param\":null,\"code\":\"invalid_json\"}}", ct);
                return;
            }

            using JsonDocument doc = parsedDoc;
            JsonElement root = doc.RootElement;
            string? invalidParameter = ChatProtocol.ValidateRequest(root, ollama: false);
            if (invalidParameter != null)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = new { message = $"Invalid field: {invalidParameter}.", type = "invalid_request_error", code = "invalid_parameter", param = invalidParameter } }, ct);
                return;
            }
            bool isStream = root.TryGetProperty("stream", out JsonElement sp) && sp.GetBoolean();

            string reqModel = root.TryGetProperty("model", out JsonElement rm) && rm.ValueKind == JsonValueKind.String
                ? rm.GetString()! : providerRegistry.DefaultModel;

            // Central routing: "model", "model@provider" or "provider/model".
            // An explicit provider hint is STRICT — unknown provider/model is a 404
            // and never falls back to another provider or to the default model.
            ResolvedRoute route = modelCatalog.ResolveRoute(reqModel);
            string effectiveModel = route.Model;
            IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> candidates = route.Candidates;

            // ── Diagnostic headers ───────────────────────────────────────
            ctx.Response.Headers["X-Proxy-Requested-Model"] = route.RequestedModel;
            ctx.Response.Headers["X-Proxy-Normalized-Model"] = route.NormalizedModel;
            ctx.Response.Headers["X-Proxy-Resolved-Model"] = route.Model;
            ctx.Response.Headers["X-Proxy-Candidate-Count"] = candidates.Count.ToString();
            ctx.Response.Headers["X-Proxy-Fallback-Used"] = route.FallbackUsed ? "true" : "false";
            if (route.ProviderHint is not null)
            {
                ctx.Response.Headers["X-Proxy-Provider-Hint"] = route.ProviderHint;
            }

            if (candidates.Count > 0)
            {
                ctx.Response.Headers["X-Proxy-Primary-Provider"] = candidates[0].Provider.Name;
                ctx.Response.Headers["X-Proxy-Primary-Upstream"] = candidates[0].UpstreamModel;
            }

            ModelRequestLogging.SetRoute(ctx, route, isStream);

            if (route.Error != RouteErrorKind.None)
            {
                string code = route.Error == RouteErrorKind.ProviderNotFound ? "provider_not_found" : "model_not_found";
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    $"{{\"error\":{{\"message\":{JsonSerializer.Serialize(route.ErrorMessage)},\"type\":\"invalid_request_error\",\"param\":\"model\",\"code\":\"{code}\"}}}}", ct);
                return;
            }

            if (candidates.Count == 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    $"{{\"error\":{{\"message\":{JsonSerializer.Serialize($"No provider candidate available for model '{route.Model}'.")},\"type\":\"invalid_request_error\",\"param\":\"model\",\"code\":\"model_not_found\"}}}}", ct);
                return;
            }

            string? modifiedRequest = requestTransformer.ModifyRequest(doc);

            int lastStatus = StatusCodes.Status502BadGateway;
            string lastBody = "{\"error\":{\"message\":\"All provider candidates failed.\",\"type\":\"upstream_error\",\"code\":\"upstream_unavailable\"}}";
            foreach ((ProviderInfo provider, string upstreamModel) in isStream ? candidates.Take(1) : candidates)
            {
                using CancellationTokenSource? timeoutCts = modelCatalog.CreateModelTimeoutCts(effectiveModel, provider.Name, ct);
                CancellationToken requestCt = timeoutCts?.Token ?? ct;
                string candidateBody = requestTransformer.ReplaceModelInRequestBody(modifiedRequest ?? rawBody, upstreamModel);
                candidateBody = requestTransformer.ApplyExecutionDefaults(candidateBody, effectiveModel, provider.Capabilities, provider.Name);
                bool native = provider.Capabilities.ApiFormat == ApiFormat.Ollama;
                if (native)
                {
                    try { candidateBody = BuildOllamaChatRequest(candidateBody, upstreamModel, isStream: false); }
                    catch (Exception ex) when (ex is ArgumentException or JsonException)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = new { message = "Invalid tool arguments or unsupported image encoding for Ollama.", type = "invalid_request_error", code = "invalid_parameter" } }, ct);
                        return;
                    }
                }

                ModelRequestLogging.Attempt(ctx, provider, upstreamModel);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, provider.Capabilities.ChatPath)
                    {
                        Content = new StringContent(candidateBody, Encoding.UTF8, "application/json")
                    };
                    if (isStream && !native) request.Headers.Accept.ParseAdd("text/event-stream");
                    using HttpResponseMessage response = await provider.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCt);
                    if (!response.IsSuccessStatusCode)
                    {
                        lastStatus = (int)response.StatusCode;
                        lastBody = await response.Content.ReadAsStringAsync(requestCt);
                        ModelRequestLogging.Failure(ctx, lastStatus, "upstream_http");
                        continue;
                    }

                    if (isStream && !native)
                    {
                        ctx.Response.ContentType = "text/event-stream";
                        ctx.Response.Headers.CacheControl = "no-cache";
                        ctx.Response.Headers["X-Accel-Buffering"] = "no";
                        await chatStreaming.StreamAndCache(response, ctx.Response, requestCt);
                    }
                    else
                    {
                        string body = await response.Content.ReadAsStringAsync(requestCt);
                        if (native) body = ConvertOllamaChatToOpenAiCompletion(body, effectiveModel);
                        if (isStream)
                            await WriteOllamaCompletionStream(ctx, body, ct);
                        else
                        {
                            ctx.Response.StatusCode = (int)response.StatusCode;
                            ctx.Response.ContentType = "application/json";
                            await ctx.Response.WriteAsync(body, ct);
                        }
                    }
                    return;
                }
                catch (Exception ex) when ((ex is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) && !ct.IsCancellationRequested)
                {
                    string kind = ex is OperationCanceledException ? "timeout" : ex is JsonException or InvalidOperationException or KeyNotFoundException ? "invalid_upstream" : "transport";
                    ModelRequestLogging.Failure(ctx, null, kind);
                    if (ctx.Response.HasStarted)
                    {
                        // Never replay a generation after delivering any response bytes.
                        ctx.Abort();
                        return;
                    }
                    lastStatus = StatusCodes.Status502BadGateway;
                    lastBody = "{\"error\":{\"message\":\"Upstream response failed.\",\"type\":\"upstream_error\",\"code\":\"upstream_unavailable\"}}";
                }
            }
            ctx.Response.StatusCode = lastStatus;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(lastBody, ct);
        });

        return app;
    }

    private static async Task WriteOllamaCompletionStream(HttpContext ctx, string completion, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(completion);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var delta = JsonNode.Parse(choice.GetProperty("message").GetRawText())!.AsObject();
        if (delta["tool_calls"] is JsonArray calls)
            for (int i = 0; i < calls.Count; i++) calls[i]!.AsObject()["index"] = i;
        string id = root.GetProperty("id").GetString()!;
        long created = root.GetProperty("created").GetInt64();
        string model = root.GetProperty("model").GetString()!;
        var first = new { id, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta, finish_reason = (string?)null } } };
        var finish = new { id, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta = new { }, finish_reason = choice.GetProperty("finish_reason").GetString() } } };
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(first)}\n\ndata: {JsonSerializer.Serialize(finish)}\n\ndata: [DONE]\n\n", ct);
    }

    private static string BuildOllamaChatRequest(string openAiRequestBody, string model, bool isStream)
    {
        using JsonDocument openAiDoc = JsonDocument.Parse(openAiRequestBody);
        JsonElement root = openAiDoc.RootElement;

        using MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);

        writer.WriteStartObject();
        writer.WriteString("model", model);
        writer.WriteBoolean("stream", isStream);

        // ── Messages ──
        // Convert OpenAI multi-part content (text + image_url parts) into Ollama format:
        //   - text parts → "content" string
        //   - image_url parts → "images" array of base64 payloads
        if (root.TryGetProperty("messages", out JsonElement messages))
        {
            writer.WritePropertyName("messages");
            writer.WriteStartArray();

            var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonElement msg in messages.EnumerateArray())
            {
                writer.WriteStartObject();

                bool hasMultiPartContent = false;
                List<string> imageUrls = [];

                // Determine if content is multi-part array with images
                if (msg.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
                {
                    hasMultiPartContent = true;
                    StringBuilder textContent = new();

                    foreach (JsonElement part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out JsonElement type) && type.GetString() == "text")
                        {
                            if (part.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                            {
                                if (textContent.Length > 0)
                                    textContent.Append('\n');
                                textContent.Append(text.GetString());
                            }
                        }
                        else if (type.GetString() == "image_url")
                        {
                            if (part.TryGetProperty("image_url", out JsonElement imgUrl) && imgUrl.ValueKind == JsonValueKind.Object)
                            {
                                if (imgUrl.TryGetProperty("url", out JsonElement url) && url.ValueKind == JsonValueKind.String)
                                {
                                    imageUrls.Add(ChatProtocol.ImagePayload(url.GetString()!));
                                }
                            }
                        }
                    }

                    writer.WriteString("content", textContent.ToString());
                }

                // Ollama associates tool results with the function name rather than
                // OpenAI's call ID. Resolve it from preceding assistant calls.
                foreach (JsonProperty mp in msg.EnumerateObject())
                {
                    if (mp.NameEquals("content") && hasMultiPartContent) continue;
                    if (mp.NameEquals("tool_calls"))
                    {
                        writer.WritePropertyName("tool_calls");
                        ChatProtocol.ToOllamaTools(mp.Value).WriteTo(writer);
                        foreach (var call in mp.Value.EnumerateArray())
                            if (call.TryGetProperty("id", out var callId) && callId.ValueKind == JsonValueKind.String)
                                toolNames[callId.GetString()!] = call.GetProperty("function").GetProperty("name").GetString()!;
                        continue;
                    }
                    if (mp.NameEquals("tool_call_id"))
                    {
                        string? id = mp.Value.GetString();
                        if (id == null || !toolNames.TryGetValue(id, out string? name))
                            throw new ArgumentException("Tool result has no preceding matching call.");
                        if (!msg.TryGetProperty("tool_name", out _)) writer.WriteString("tool_name", name);
                        continue;
                    }

                    mp.WriteTo(writer);
                }

                // Write images array if any image_url parts were found
                if (imageUrls.Count > 0)
                {
                    writer.WritePropertyName("images");
                    writer.WriteStartArray();
                    foreach (string imgUrl in imageUrls)
                    {
                        writer.WriteStringValue(imgUrl);
                    }
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (root.TryGetProperty("tools", out JsonElement tools))
        {
            writer.WritePropertyName("tools");
            tools.WriteTo(writer);
        }

        bool hasTemperature = root.TryGetProperty("temperature", out JsonElement temp);
        bool hasTopP = root.TryGetProperty("top_p", out JsonElement topP);
        bool hasTopK = root.TryGetProperty("top_k", out JsonElement topK);
        bool hasMaxTokens = root.TryGetProperty("max_completion_tokens", out JsonElement maxTokens)
            || root.TryGetProperty("max_tokens", out maxTokens);

        if (hasTemperature || hasTopP || hasTopK || hasMaxTokens)
        {
            writer.WritePropertyName("options");
            writer.WriteStartObject();
            if (hasTemperature && temp.ValueKind == JsonValueKind.Number)
            {
                writer.WriteNumber("temperature", temp.GetDouble());
            }

            if (hasTopP && topP.ValueKind == JsonValueKind.Number)
            {
                writer.WriteNumber("top_p", topP.GetDouble());
            }

            if (hasTopK && topK.ValueKind == JsonValueKind.Number)
            {
                writer.WriteNumber("top_k", topK.GetInt32());
            }

            if (hasMaxTokens && maxTokens.ValueKind == JsonValueKind.Number)
            {
                writer.WriteNumber("num_predict", maxTokens.GetInt32());
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string ConvertOllamaChatToOpenAiCompletion(string ollamaResponseBody, string effectiveModel)
    {
        using JsonDocument ollamaDoc = JsonDocument.Parse(ollamaResponseBody);
        JsonElement root = ollamaDoc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object)
            throw new JsonException("Missing upstream message.");

        string content = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out JsonElement contentElement)
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;

        // Fallback to `thinking` when content is empty (reasoning models put text in `thinking`).
        if (string.IsNullOrWhiteSpace(content) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("thinking", out JsonElement thinkingElement))
        {
            content = thinkingElement.GetString() ?? string.Empty;
        }

        JsonArray? toolCalls = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("tool_calls", out var nativeCalls)
            ? ChatProtocol.ToOpenAiTools(nativeCalls) : null;
        object completion = new
        {
            id = $"chatcmpl-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = effectiveModel,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content,
                        tool_calls = toolCalls
                    },
                    finish_reason = toolCalls is { Count: > 0 } ? "tool_calls" : root.TryGetProperty("done_reason", out JsonElement dr) && dr.ValueKind == JsonValueKind.String
                        ? dr.GetString()
                        : "stop"
                }
            }
        };

        return JsonSerializer.Serialize(completion, JsonDefaults.SnakeCase);
    }
}
