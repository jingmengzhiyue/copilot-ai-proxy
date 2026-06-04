using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

internal static class OllamaEndpoints
{
    internal static IEndpointRouteBuilder MapOllamaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/version", () => Results.Json(new { version = "0.5.7" }, JsonDefaults.SnakeCase));

        app.MapGet("/api/tags", async (HttpContext ctx, ModelCatalogService modelCatalog) =>
        {
            await modelCatalog.RefreshAvailableModelsIfNeeded(ctx.RequestAborted);
            return Results.Json(new
            {
                models = modelCatalog.AvailableModels.Select(m =>
                {
                    (int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) p = modelCatalog.GetModelProfile(m);
                    return new
                    {
                        name = m,
                        model = m,
                        modified_at = DateTime.UtcNow.ToString("o"),
                        size = 0L,
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
                        supports_images = p.SupportsVision
                    };
                }).ToArray()
            }, JsonDefaults.SnakeCase);
        });

        app.MapGet("/api/show", async (HttpContext ctx, string? model, ModelCatalogService modelCatalog, ProviderRegistry providerRegistry, OllamaResponseBuilder ollamaResponseBuilder) =>
        {
            await modelCatalog.RefreshAvailableModelsIfNeeded(ctx.RequestAborted);
            string resolved = providerRegistry.ResolveModel(model);
            return Results.Json(ollamaResponseBuilder.BuildOllamaShowResponse(resolved), JsonDefaults.SnakeCase);
        });

        app.MapPost("/api/show", async (HttpContext ctx, ModelCatalogService modelCatalog, ProviderRegistry providerRegistry, OllamaResponseBuilder ollamaResponseBuilder) =>
        {
            await modelCatalog.RefreshAvailableModelsIfNeeded(ctx.RequestAborted);
            using StreamReader reader = new(ctx.Request.Body);
            string body = await reader.ReadToEndAsync(ctx.RequestAborted);
            string? model = null;
            try
            {
                using JsonDocument d = JsonDocument.Parse(body);
                if (d.RootElement.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String)
                    model = m.GetString();
            }
            catch { }

            string resolved = providerRegistry.ResolveModel(model);
            return Results.Json(ollamaResponseBuilder.BuildOllamaShowResponse(resolved), JsonDefaults.SnakeCase);
        });

        app.MapPost("/api/chat", async (
            HttpContext ctx,
            ProviderRegistry providerRegistry,
            ModelCatalogService modelCatalog,
            ChatStreamingService chatStreaming,
            ReasoningCacheService reasoningCache) =>
        {
            CancellationToken ct = ctx.RequestAborted;
            await modelCatalog.RefreshAvailableModelsIfNeeded(ct);
            using StreamReader reader = new(ctx.Request.Body);
            string body = await reader.ReadToEndAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            bool isStream = root.TryGetProperty("stream", out JsonElement sp) && sp.GetBoolean();

            List<object> messages = [];
            if (root.TryGetProperty("messages", out JsonElement omsgs))
            {
                foreach (JsonElement msg in omsgs.EnumerateArray())
                {
                    string role = msg.GetProperty("role").GetString()!;
                    string text = msg.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";

                    object content;
                    if (msg.TryGetProperty("images", out JsonElement imgs) && imgs.GetArrayLength() > 0)
                    {
                        List<object> parts = [new { type = "text", text }];
                        foreach (JsonElement img in imgs.EnumerateArray())
                        {
                            string url = img.GetString()!;
                            if (!url.StartsWith("data:") && !url.StartsWith("http"))
                                url = $"data:image/png;base64,{url}";
                            parts.Add(new { type = "image_url", image_url = new { url } });
                        }

                        content = parts;
                    }
                    else
                    {
                        content = text;
                    }

                    messages.Add(new { role, content });
                }
            }

            string ollamaRequestedModel = root.TryGetProperty("model", out JsonElement om) && om.ValueKind == JsonValueKind.String
                ? om.GetString()! : providerRegistry.DefaultModel;
            string ollamaEffectiveModel = providerRegistry.ResolveModel(ollamaRequestedModel);
            string ollamaUpstreamModel = providerRegistry.ResolveUpstreamModel(ollamaEffectiveModel);
            ProviderInfo ollamaProvider = providerRegistry.ResolveProvider(ollamaEffectiveModel);
            ModelExecutionConfig ollamaExec = modelCatalog.GetExecutionConfigForModel(ollamaEffectiveModel);

            Dictionary<string, object?> reqObj = new()
            {
                ["model"] = ollamaUpstreamModel,
                ["messages"] = messages,
                ["stream"] = isStream,
                ["max_tokens"] = ollamaExec.MaxTokensPreferred ?? 8192
            };
            if (root.TryGetProperty("tools", out JsonElement tools))
                reqObj["tools"] = tools;

            if (ollamaExec.Temperature.HasValue)
                reqObj["temperature"] = ollamaExec.Temperature.Value;
            if (ollamaExec.TopP.HasValue)
                reqObj["top_p"] = ollamaExec.TopP.Value;
            if (!string.IsNullOrWhiteSpace(ollamaExec.ReasoningEffort))
                reqObj["reasoning_effort"] = ollamaExec.ReasoningEffort;

            string reqJson = JsonSerializer.Serialize(reqObj, JsonDefaults.SnakeCase);
            using StringContent reqContent = new(reqJson, Encoding.UTF8, "application/json");
            using CancellationTokenSource? ollamaTimeoutCts = modelCatalog.CreateModelTimeoutCts(ollamaEffectiveModel, ct);
            CancellationToken ollamaCt = ollamaTimeoutCts?.Token ?? ct;

            if (ollamaProvider.Name.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            {
                string upstreamBody = ReplaceModelInOllamaRequestBody(body, ollamaUpstreamModel);

                if (!isStream)
                {
                    using StringContent ollamaContent = new(upstreamBody, Encoding.UTF8, "application/json");
                    using HttpResponseMessage ollamaResp = await ollamaProvider.Client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = ollamaContent }, ollamaCt);
                    string ollamaRespBody = await ollamaResp.Content.ReadAsStringAsync(ct);
                    ctx.Response.StatusCode = (int)ollamaResp.StatusCode;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(ollamaRespBody, ct);
                    return;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-ndjson";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                using StringContent ollamaStreamContent = new(upstreamBody, Encoding.UTF8, "application/json");
                using HttpRequestMessage ollamaStreamReq = new(HttpMethod.Post, "/api/chat") { Content = ollamaStreamContent };
                using HttpResponseMessage ollamaStreamResp = await ollamaProvider.Client.SendAsync(
                    ollamaStreamReq, HttpCompletionOption.ResponseHeadersRead, ollamaCt);

                if (!ollamaStreamResp.IsSuccessStatusCode)
                {
                    string errBody = await ollamaStreamResp.Content.ReadAsStringAsync(ct);
                    ctx.Response.StatusCode = (int)ollamaStreamResp.StatusCode;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(errBody, ct);
                    return;
                }

                await chatStreaming.StreamNdjsonPassthrough(ollamaStreamResp, ctx.Response, ct);
                return;
            }

            if (!isStream)
            {
                using HttpResponseMessage resp = await ollamaProvider.Client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = reqContent }, ollamaCt);
                string respBody = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    ctx.Response.StatusCode = (int)resp.StatusCode;
                    await ctx.Response.WriteAsync(respBody, ct);
                    return;
                }

                reasoningCache.CacheReasoningFromResponse(respBody);

                using JsonDocument odoc = JsonDocument.Parse(respBody);
                JsonElement msg = odoc.RootElement.GetProperty("choices")[0].GetProperty("message");
                Dictionary<string, object?> ollamaResp = new()
                {
                    ["model"] = ollamaEffectiveModel,
                    ["created_at"] = DateTime.UtcNow.ToString("o"),
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = msg.GetProperty("content").GetString() ?? ""
                    },
                    ["done"] = true,
                    ["done_reason"] = "stop"
                };
                if (msg.TryGetProperty("tool_calls", out JsonElement tcs))
                    ((Dictionary<string, object?>)ollamaResp["message"]!)["tool_calls"] = tcs;

                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(ollamaResp, JsonDefaults.SnakeCase), ct);
            }
            else
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-ndjson";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                using HttpRequestMessage upstreamReq = new(HttpMethod.Post, "/v1/chat/completions")
                {
                    Content = reqContent
                };
                upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                using HttpResponseMessage upstreamResp = await ollamaProvider.Client.SendAsync(
                    upstreamReq, HttpCompletionOption.ResponseHeadersRead, ollamaCt);

                if (!upstreamResp.IsSuccessStatusCode)
                {
                    string errBody = await upstreamResp.Content.ReadAsStringAsync(ct);
                    ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(errBody, ct);
                    return;
                }

                await chatStreaming.StreamOllamaAndCache(upstreamResp, ctx.Response, ollamaEffectiveModel, ct);
            }
        });

        return app;
    }

    private static string ReplaceModelInOllamaRequestBody(string rawBody, string upstreamModel)
    {
        try
        {
            using JsonDocument original = JsonDocument.Parse(rawBody);
            JsonElement root = original.RootElement;
            using MemoryStream ms = new();
            using Utf8JsonWriter writer = new(ms);

            writer.WriteStartObject();
            bool hasModel = false;

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (prop.NameEquals("model"))
                {
                    writer.WriteString("model", upstreamModel);
                    hasModel = true;
                    continue;
                }

                prop.WriteTo(writer);
            }

            if (!hasModel)
            {
                writer.WriteString("model", upstreamModel);
            }

            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return rawBody;
        }
    }
}
