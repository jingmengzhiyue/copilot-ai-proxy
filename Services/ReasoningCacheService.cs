using System.Text.Json;

internal sealed class ReasoningCacheService
{
    private readonly Dictionary<string, (string Value, DateTimeOffset Expires, long Bytes)> _reasoningCache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private long _bytes;
    internal const int MaximumEntries = 256;
    internal const long MaximumBytes = 4 * 1024 * 1024;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private long _assistantMsgCounter;

    public ReasoningCacheService() : this(TimeProvider.System) { }

    internal ReasoningCacheService(TimeProvider clock) => _clock = clock;

    internal bool TryGet(string key, out string? value)
    {
        lock (_gate)
        {
            RemoveExpired();
            if (_reasoningCache.TryGetValue(key, out var entry))
            {
                value = entry.Value;
                return true;
            }
            value = null;
            return false;
        }
    }

    internal string NextAssistantKey() => $"assistant:{Interlocked.Increment(ref _assistantMsgCounter) - 1}";

    internal void Set(string key, string value)
    {
        // Both strings consume UTF-16 storage. Bound oversized entries before retaining them.
        long bytes = ((long)key.Length + value.Length) * sizeof(char);
        lock (_gate)
        {
            RemoveExpired();
            Remove(key);
            if (bytes > MaximumBytes / 4)
                return;
            while (_reasoningCache.Count >= MaximumEntries || _bytes + bytes > MaximumBytes)
                Remove(_reasoningCache.MinBy(pair => pair.Value.Expires).Key);
            _reasoningCache[key] = (value, _clock.GetUtcNow() + Lifetime, bytes);
            _bytes += bytes;
        }
    }

    private void RemoveExpired()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        foreach (string key in _reasoningCache.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
            Remove(key);
    }

    private void Remove(string key)
    {
        if (_reasoningCache.Remove(key, out var entry))
            _bytes -= entry.Bytes;
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

            Set(key, rc.GetString()!);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // cache errors are non-critical
        }
    }
}
