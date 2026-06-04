using System.Collections.Concurrent;
using System.Text.Json;

internal sealed class ReasoningCacheService
{
    private readonly ConcurrentDictionary<string, string> _reasoningCache = new(StringComparer.Ordinal);
    private long _assistantMsgCounter;

    internal bool TryGet(string key, out string? value) => _reasoningCache.TryGetValue(key, out value);

    internal string NextAssistantKey() => $"assistant:{Interlocked.Increment(ref _assistantMsgCounter) - 1}";

    internal void Set(string key, string value)
    {
        _reasoningCache[key] = value;
    }

    internal void CacheReasoningFromResponse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0)
            {
                return;
            }

            JsonElement msg = choices[0].TryGetProperty("message", out JsonElement m) ? m : choices[0].TryGetProperty("delta", out JsonElement d) ? d : default;
            if (msg.ValueKind == JsonValueKind.Undefined)
            {
                return;
            }

            if (!msg.TryGetProperty("reasoning_content", out JsonElement rc) || string.IsNullOrEmpty(rc.GetString()))
            {
                return;
            }

            string key;
            if (msg.TryGetProperty("tool_calls", out JsonElement tcs) && tcs.GetArrayLength() > 0)
            {
                List<string> ids = [];
                foreach (JsonElement tc in tcs.EnumerateArray())
                {
                    if (tc.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                    {
                        ids.Add(idE.GetString()!);
                    }
                }

                key = ids.Count > 0 ? $"toolcall:{string.Join("|", ids)}" : NextAssistantKey();
            }
            else
            {
                key = NextAssistantKey();
            }

            _reasoningCache[key] = rc.GetString()!;
        }
        catch
        {
            // cache errors are non-critical
        }
    }
}
