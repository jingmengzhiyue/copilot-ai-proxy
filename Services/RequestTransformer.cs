using System.Text;
using System.Text.Json;

internal sealed class RequestTransformer
{
    private readonly ModelCatalogService _modelCatalogService;

    public RequestTransformer(ModelCatalogService modelCatalogService, ReasoningCacheService reasoningCacheService)
    {
        _modelCatalogService = modelCatalogService;
    }

    internal string ApplyExecutionDefaults(
        string rawBody,
        string model,
        ProviderCapabilities capabilities = default,
        string? providerName = null)
    {
        ModelExecutionConfig exec = string.IsNullOrWhiteSpace(providerName)
            ? _modelCatalogService.GetExecutionConfigForModel(model)
            : _modelCatalogService.GetExecutionConfigForModel(model, providerName);
        // Parameter support is driven by the provider's declared capabilities.
        // For unknown providers (default capabilities), all feature flags are false
        // (no reasoning_effort, no top_k) — a safe, conservative default.
        bool supportsReasoningEffort = capabilities.SupportsReasoningEffort;

        // DeepSeek & OpenAI o-series: sending both temperature and top_p simultaneously
        // causes undefined behaviour per official docs. When reasoning_effort is active
        // (i.e. the model is a native reasoner) only emit temperature, not top_p.
        bool isNativeReasoner = capabilities.SupportsReasoningEffort && !string.IsNullOrWhiteSpace(exec.ReasoningEffort);

        // Providers that DO support top_k: NVIDIA, Groq, OpenRouter (passthrough).
        // Everyone else (including unknown) gets top_k stripped.
        bool supportsTopK = capabilities.SupportsTopK;

        // OverrideClientParams=true means the configured value is non-negotiable for this
        // model (e.g. Kimi K2.x requires temperature=1.0). In that mode we overwrite the
        // client-supplied field instead of only injecting defaults.
        bool force = exec.OverrideClientParams;

        try
        {
            using JsonDocument original = JsonDocument.Parse(rawBody);
            JsonElement root = original.RootElement;
            using MemoryStream ms = new();
            using Utf8JsonWriter writer = new(ms);

            writer.WriteStartObject();

            bool hasTemperature = false;
            bool hasTopP = false;
            bool hasOutputLimit = false;
            bool hasReasoningEffort = false;

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (prop.NameEquals("temperature"))
                {
                    if (force && exec.Temperature.HasValue)
                    {
                        writer.WriteNumber("temperature", exec.Temperature.Value);
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                    hasTemperature = true;
                }
                else if (prop.NameEquals("top_p"))
                {
                    if (force && exec.TopP.HasValue)
                    {
                        writer.WriteNumber("top_p", exec.TopP.Value);
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                    hasTopP = true;
                }
                else if (prop.NameEquals("max_tokens") || prop.NameEquals("max_completion_tokens"))
                {
                    if (hasOutputLimit)
                    {
                        // OpenAI-compatible APIs treat these as alternative names for the
                        // same limit. Preserve only the first client value if both are sent.
                        continue;
                    }

                    if (force && exec.MaxCompletionTokensPreferred.HasValue)
                    {
                        writer.WriteNumber("max_completion_tokens", exec.MaxCompletionTokensPreferred.Value);
                    }
                    else if (force && exec.MaxTokensPreferred.HasValue)
                    {
                        writer.WriteNumber("max_tokens", exec.MaxTokensPreferred.Value);
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                    hasOutputLimit = true;
                }
                else if (prop.NameEquals("reasoning_effort"))
                {
                    if (force && !string.IsNullOrWhiteSpace(exec.ReasoningEffort))
                    {
                        writer.WriteString("reasoning_effort", exec.ReasoningEffort);
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                    hasReasoningEffort = true;
                }
                else if (prop.NameEquals("top_k") && !supportsTopK)
                {
                    // Skip top_k for providers that don't support it
                    continue;
                }
                else if ((string.Equals(providerName, "kimicode", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(providerName, "kimi", StringComparison.OrdinalIgnoreCase))
                    && prop.NameEquals("messages") && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName("messages");
                    WriteKimiMessages(writer, prop.Value);
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            if (!hasTemperature && exec.Temperature.HasValue)
                writer.WriteNumber("temperature", exec.Temperature.Value);
            // Skip top_p injection for native reasoners to avoid temperature+top_p conflict.
            if (!hasTopP && exec.TopP.HasValue && !isNativeReasoner)
                writer.WriteNumber("top_p", exec.TopP.Value);
            if (!hasOutputLimit && exec.MaxCompletionTokensPreferred.HasValue)
                writer.WriteNumber("max_completion_tokens", exec.MaxCompletionTokensPreferred.Value);
            else if (!hasOutputLimit && exec.MaxTokensPreferred.HasValue)
                writer.WriteNumber("max_tokens", exec.MaxTokensPreferred.Value);
            // Only inject reasoning_effort for providers/models that natively support it.
            if (!hasReasoningEffort && !string.IsNullOrWhiteSpace(exec.ReasoningEffort) && supportsReasoningEffort)
                writer.WriteString("reasoning_effort", exec.ReasoningEffort);

            writer.WriteEndObject();
            writer.Flush();

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return rawBody;
        }
    }

    private static void WriteKimiMessages(Utf8JsonWriter writer, JsonElement messages)
    {
        writer.WriteStartArray();
        foreach (JsonElement message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("role", out JsonElement role)
                || role.ValueKind != JsonValueKind.String || role.GetString() != "assistant"
                || !message.TryGetProperty("reasoning_details", out JsonElement details)
                || details.ValueKind != JsonValueKind.Array)
            {
                message.WriteTo(writer);
                continue;
            }

            writer.WriteStartObject();
            foreach (JsonProperty property in message.EnumerateObject())
            {
                if (!property.NameEquals("reasoning_details"))
                {
                    property.WriteTo(writer);
                    continue;
                }

                writer.WritePropertyName("reasoning_details");
                writer.WriteStartArray();
                foreach (JsonElement detail in details.EnumerateArray())
                {
                    if (detail.ValueKind != JsonValueKind.Object)
                    {
                        detail.WriteTo(writer);
                        continue;
                    }

                    // Kimi emits index in streaming deltas but rejects it when the
                    // same reasoning detail is replayed in assistant history.
                    writer.WriteStartObject();
                    foreach (JsonProperty field in detail.EnumerateObject())
                        if (!field.NameEquals("index")) field.WriteTo(writer);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    internal string ReplaceModelInRequestBody(string rawBody, string upstreamModel)
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

    internal string? ModifyRequest(JsonDocument doc)
    {
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("messages", out JsonElement msgs))
        {
            return null;
        }

        bool modified = false;
        using MemoryStream ms = new();
        using Utf8JsonWriter w = new(ms);

        w.WriteStartObject();
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (!prop.NameEquals("messages"))
            {
                prop.WriteTo(w);
                continue;
            }

            w.WritePropertyName("messages");
            w.WriteStartArray();
            foreach (JsonElement msg in msgs.EnumerateArray())
            {
                string? role = msg.TryGetProperty("role", out JsonElement r) ? r.GetString() : null;
                if (role == "assistant")
                {
                    bool hasTc = msg.TryGetProperty("tool_calls", out JsonElement tcArr) && tcArr.GetArrayLength() > 0;
                    bool hasFunctionCall = msg.TryGetProperty("function_call", out JsonElement fc)
                        && fc.ValueKind == JsonValueKind.Object;
                    // Some providers (e.g. Moonshot/Kimi) reject assistant messages whose
                    // content is empty when there are no tool/function calls associated.
                    // Drop those invalid placeholders before forwarding upstream, but only
                    // when the client did not supply reasoning to preserve for thinking models.
                    if (!hasTc && !hasFunctionCall && IsAssistantContentEmpty(msg)
                        && !(msg.TryGetProperty("reasoning_content", out JsonElement reasoning)
                            && reasoning.ValueKind == JsonValueKind.String
                            && !string.IsNullOrEmpty(reasoning.GetString()))
                        && !(msg.TryGetProperty("reasoning_details", out JsonElement details)
                            && details.ValueKind == JsonValueKind.Array && details.GetArrayLength() > 0))
                    {
                        modified = true;
                        continue;
                    }
                }

                msg.WriteTo(w);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
        w.Flush();

        return modified ? Encoding.UTF8.GetString(ms.ToArray()) : null;
    }

    private static bool IsAssistantContentEmpty(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out JsonElement content))
            return true;

        return content.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.Undefined => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(content.GetString()),
            JsonValueKind.Array => content.GetArrayLength() == 0,
            _ => false
        };
    }
}
