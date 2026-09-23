using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class ChatStreamingService
{
    // Kept for existing DI registrations. Requests have no trustworthy session key,
    // so forwarding must never persist reasoning for another request to recover.
    public ChatStreamingService(ReasoningCacheService reasoningCacheService) { }

    internal async Task StreamAndCache(HttpResponseMessage upstream, HttpResponse downstream, CancellationToken ct)
    {
        using Stream stream = await upstream.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream);
        bool completed = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:"))
            {
                string json = line[5..].TrimStart();
                if (json == "[DONE]") completed = true;
                else if (json.Length > 0)
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out _)) throw new IOException("Upstream stream reported an error.");
                    if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                        completed |= choices.EnumerateArray().Any(c => c.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String);
                }
            }
            await downstream.WriteAsync(line + "\n", ct);
            await downstream.Body.FlushAsync(ct);
            if (line.StartsWith("data:") && line[5..].Trim() == "[DONE]") return;
        }
        if (!completed) throw new IOException("Upstream stream ended before completion.");
    }

    internal async Task StreamNdjsonPassthrough(HttpResponseMessage upstream, HttpResponse downstream, CancellationToken ct)
    {
        using Stream stream = await upstream.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream);
        bool completed = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("error", out _)) throw new IOException("Upstream stream reported an error.");
            completed |= doc.RootElement.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True;
            await downstream.WriteAsync(line + "\n", ct);
            await downstream.Body.FlushAsync(ct);
            if (completed) return;
        }
        if (!completed) throw new IOException("Upstream stream ended before completion.");
    }

    internal async Task StreamOllamaAndCache(HttpResponseMessage upstream, HttpResponse downstream, string model, CancellationToken ct)
    {
        using Stream stream = await upstream.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream);
        bool completed = false;
        string finishReason = "stop";
        // OpenAI sends arguments as arbitrary fragments, potentially interleaved by
        // index. Ollama clients may execute calls immediately, so emit only complete objects.
        var calls = new SortedDictionary<int, (StringBuilder Name, StringBuilder Arguments)>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            string json = line[5..].TrimStart();
            if (json.Length == 0) continue;
            if (json == "[DONE]") { completed = true; break; }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _)) throw new IOException("Upstream stream reported an error.");
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                throw new JsonException("Missing upstream choices.");
            if (choices.GetArrayLength() == 0) continue; // Usage-only chunk.
            var choice = choices[0];
            var delta = choice.TryGetProperty("delta", out var d) ? d : choice.TryGetProperty("message", out var m) ? m : default;
            if (delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("tool_calls", out var tools))
                foreach (var call in tools.EnumerateArray())
                {
                    int index = 0;
                    if (call.TryGetProperty("index", out var idx)
                        && (idx.ValueKind != JsonValueKind.Number || !idx.TryGetInt32(out index) || index < 0))
                        throw new JsonException("Invalid upstream tool call index.");
                    if (!calls.TryGetValue(index, out var pending))
                    {
                        pending = (new StringBuilder(), new StringBuilder());
                        calls.Add(index, pending);
                    }
                    if (!call.TryGetProperty("function", out var function)) continue;
                    if (function.TryGetProperty("name", out var name)) pending.Name.Append(name.GetString());
                    if (function.TryGetProperty("arguments", out var args)) pending.Arguments.Append(args.GetString());
                }
                var message = new JsonObject { ["role"] = "assistant", ["content"] = "" };
                bool hasOutput = false;
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                { message["content"] = content.GetString(); hasOutput = true; }
                if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                { message["thinking"] = reasoning.GetString(); hasOutput = true; }
                if (hasOutput) await WriteOllamaChunk(downstream, model, message, false, null, ct);
            }
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            {
                finishReason = finish.GetString() ?? "stop";
                completed = true;
            }
        }
        if (!completed) throw new IOException("Upstream stream ended before completion.");
        var finalMessage = new JsonObject { ["role"] = "assistant", ["content"] = "" };
        if (calls.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var call in calls.Values)
            {
                JsonNode? arguments = JsonNode.Parse(call.Arguments.ToString());
                if (arguments is not JsonObject || call.Name.Length == 0) throw new JsonException("Incomplete upstream tool call.");
                tools.Add(new JsonObject { ["function"] = new JsonObject { ["name"] = call.Name.ToString(), ["arguments"] = arguments } });
            }
            finalMessage["tool_calls"] = tools;
        }
        await WriteOllamaChunk(downstream, model, finalMessage, true, finishReason, ct);
    }

    private static async Task WriteOllamaChunk(HttpResponse downstream, string model, JsonObject message, bool done, string? finishReason, CancellationToken ct)
    {
        var chunk = new JsonObject { ["model"] = model, ["created_at"] = DateTime.UtcNow.ToString("o"), ["message"] = message, ["done"] = done };
        if (finishReason != null) chunk["done_reason"] = finishReason;
        await downstream.WriteAsync(chunk.ToJsonString() + "\n", ct);
        await downstream.Body.FlushAsync(ct);
    }
}
