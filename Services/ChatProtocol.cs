using System.Text.Json;
using System.Text.Json.Nodes;

// Only validates fields that the proxy reads or translates. Unknown provider-specific
// fields remain available to upstream APIs; null assistant content is legal.
internal static class ChatProtocol
{
    internal static string? ValidateRequest(JsonElement root, bool ollama)
    {
        if (root.TryGetProperty("model", out var model) && model.ValueKind != JsonValueKind.String) return "model";
        if (root.TryGetProperty("stream", out var stream) && stream.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "stream";
        if (root.TryGetProperty("options", out var options) && options.ValueKind != JsonValueKind.Object) return "options";
        string? invalid = ValidateNumbers(root);
        if (invalid != null) return invalid;
        if (options.ValueKind == JsonValueKind.Object && (invalid = ValidateNumbers(options)) != null) return "options." + invalid;
        if (root.TryGetProperty("format", out var format) && format.ValueKind != JsonValueKind.Object && !(format.ValueKind == JsonValueKind.String && format.GetString() == "json")) return "format";
        if (root.TryGetProperty("tools", out var tools) && tools.ValueKind != JsonValueKind.Array) return "tools";
        if (!root.TryGetProperty("messages", out var messages)) return null;
        if (messages.ValueKind != JsonValueKind.Array) return "messages";
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object) return "messages";
            if (message.TryGetProperty("role", out var role) && role.ValueKind != JsonValueKind.String) return "messages.role";
            foreach (string field in new[] { "tool_call_id", "tool_name", "name", "reasoning_content" })
                if (message.TryGetProperty(field, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return "messages." + field;
            if (message.TryGetProperty("images", out var images))
            {
                if (images.ValueKind != JsonValueKind.Array || images.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) return "messages.images";
            }
            if (message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind is not (JsonValueKind.String or JsonValueKind.Null or JsonValueKind.Array)) return "messages.content";
                if (ollama && content.ValueKind == JsonValueKind.Array) return "messages.content";
                if (content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return "messages.content.type";
                    if (type.GetString() == "text" && (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)) return "messages.content.text";
                    if (type.GetString() == "image_url" && (!part.TryGetProperty("image_url", out var image) || image.ValueKind != JsonValueKind.Object || !image.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)) return "messages.content.image_url";
                }
            }
            if (message.TryGetProperty("tool_calls", out var calls))
            {
                if (calls.ValueKind != JsonValueKind.Array) return "messages.tool_calls";
                foreach (var call in calls.EnumerateArray())
                {
                    if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object) return "messages.tool_calls.function";
                    if (!function.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return "messages.tool_calls.function.name";
                    if (!function.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != (ollama ? JsonValueKind.Object : JsonValueKind.String)) return "messages.tool_calls.function.arguments";
                    if (call.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.String) return "messages.tool_calls.id";
                }
            }
        }
        return null;
    }

    private static string? ValidateNumbers(JsonElement obj)
    {
        foreach (string name in new[] { "num_predict", "top_k", "max_tokens", "max_completion_tokens" })
            if (obj.TryGetProperty(name, out var number) && !(name != "num_predict" && number.ValueKind == JsonValueKind.Null) && (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out _))) return name;
        if (obj.TryGetProperty("seed", out var seed) && seed.ValueKind != JsonValueKind.Null && (seed.ValueKind != JsonValueKind.Number || !seed.TryGetInt64(out _))) return "seed";
        foreach (string name in new[] { "temperature", "top_p" })
            if (obj.TryGetProperty(name, out var number) && number.ValueKind != JsonValueKind.Null && (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out double value) || !double.IsFinite(value))) return name;
        if (obj.TryGetProperty("stop", out var stop) && stop.ValueKind != JsonValueKind.Null && stop.ValueKind != JsonValueKind.String && (stop.ValueKind != JsonValueKind.Array || stop.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))) return "stop";
        return null;
    }

    internal static JsonArray ToOllamaTools(JsonElement calls)
    {
        if (calls.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid tool calls.");
        var result = new JsonArray();
        foreach (var call in calls.EnumerateArray())
        {
            var function = call.GetProperty("function");
            var arguments = function.GetProperty("arguments");
            JsonNode? parsed = arguments.ValueKind == JsonValueKind.String
                ? JsonNode.Parse(arguments.GetString() ?? "") : JsonNode.Parse(arguments.GetRawText());
            if (parsed is not JsonObject) throw new JsonException("Tool arguments must contain a JSON object.");
            result.Add(new JsonObject { ["function"] = new JsonObject { ["name"] = function.GetProperty("name").GetString(), ["arguments"] = parsed } });
        }
        return result;
    }

    internal static JsonArray ToOpenAiTools(JsonElement calls)
    {
        if (calls.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid tool calls.");
        var result = new JsonArray();
        foreach (var call in calls.EnumerateArray())
        {
            var function = call.GetProperty("function");
            var arguments = function.GetProperty("arguments");
            if (arguments.ValueKind != JsonValueKind.Object) throw new JsonException("Native tool arguments must be an object.");
            result.Add(new JsonObject
            {
                ["id"] = call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : $"call_{Guid.NewGuid():N}",
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = function.GetProperty("name").GetString(), ["arguments"] = arguments.GetRawText() }
            });
        }
        return result;
    }

    internal static string ImagePayload(string url)
    {
        int comma = url.IndexOf(',');
        if (!url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || comma < 0 || !url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ollama images require a base64 image data URL; remote image URLs are unsupported.");
        string payload = url[(comma + 1)..];
        try { _ = Convert.FromBase64String(payload); }
        catch (FormatException) { throw new ArgumentException("Invalid base64 image data."); }
        if (payload.Length == 0) throw new ArgumentException("Empty image data.");
        return payload;
    }

    internal static string ApplyNativeDefaults(string body, string model, bool stream, ModelExecutionConfig config)
    {
        var root = JsonNode.Parse(body)!.AsObject();
        root["model"] = model;
        root["stream"] = stream;
        var options = root["options"]?.AsObject() ?? new JsonObject();
        void Set(string key, JsonNode? value)
        {
            if (value != null && (config.OverrideClientParams || !options.ContainsKey(key))) options[key] = value;
        }
        Set("temperature", JsonValue.Create(config.Temperature));
        Set("top_p", JsonValue.Create(config.TopP));
        Set("num_predict", JsonValue.Create(config.MaxCompletionTokensPreferred ?? config.MaxTokensPreferred));
        if (root["options"] == null && options.Count > 0) root["options"] = options;
        return root.ToJsonString();
    }
}
