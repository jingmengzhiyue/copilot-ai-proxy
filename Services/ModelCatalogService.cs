using System.Text.Json;

internal sealed class ModelCatalogService
{
    private readonly ProviderRegistry _providerRegistry;
    private readonly ModelSelectionStore _modelSelectionStore;
    private readonly TimeSpan _modelsRefreshInterval = TimeSpan.FromMinutes(5);

    public ModelCatalogService(ProviderRegistry providerRegistry, ModelSelectionStore modelSelectionStore)
    {
        _providerRegistry = providerRegistry;
        _modelSelectionStore = modelSelectionStore;
        AvailableModels = [_providerRegistry.DefaultModel];
        ModelsLastRefreshUtc = DateTime.MinValue;
    }

    internal string[] AvailableModels { get; private set; }

    internal DateTime ModelsLastRefreshUtc { get; private set; }

    internal async Task RefreshAvailableModelsIfNeeded(CancellationToken ct)
    {
        if (DateTime.UtcNow - ModelsLastRefreshUtc < _modelsRefreshInterval)
        {
            return;
        }

        await RefreshAvailableModels(ct);
    }

    internal async Task RefreshAvailableModels(CancellationToken ct)
    {
        try
        {
            Dictionary<string, ProviderInfo> newMap = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> newUpstream = new(StringComparer.OrdinalIgnoreCase);
            List<string> allModels = [];

            foreach (ProviderInfo prov in _providerRegistry.Providers)
            {
                string[] discovered = await TryGetModelsFromProvider(prov, ct);
                foreach (string m in discovered)
                {
                    if (string.IsNullOrWhiteSpace(m))
                    {
                        continue;
                    }

                    if (!_modelSelectionStore.IsPreferredModel(m, prov.Name))
                    {
                        continue;
                    }

                    (int ContextLength, _, _, _, _, _) = GetModelProfile(m);
                    if (ContextLength == 0)
                    {
                        continue;
                    }

                    // Bare name: claimed by the first provider that offers it (back-compat).
                    if (!newMap.ContainsKey(m))
                    {
                        newMap[m] = prov;
                        newUpstream[m] = m;
                        allModels.Add(m);
                    }

                    // Provider-qualified alias: always exposed so every provider's model is addressable.
                    string qualified = $"{m}@{prov.Name}";
                    if (!newMap.ContainsKey(qualified))
                    {
                        newMap[qualified] = prov;
                        newUpstream[qualified] = m;
                        allModels.Add(qualified);
                    }
                }
            }

            if (allModels.Count > 0)
            {
                AvailableModels = allModels
                    .OrderBy(model => _modelSelectionStore.GetPreferredModelPriority(newUpstream[model], newMap[model].Name))
                    .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                _providerRegistry.UpdateModelMappings(newMap, newUpstream);
                ModelsLastRefreshUtc = DateTime.UtcNow;
            }
        }
        catch
        {
            // keep current fallback list when discovery fails
        }
    }

    internal (int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) GetModelProfile(string model)
    {
        ModelExecutionConfig configured = _modelSelectionStore.GetExecutionConfigForModel(model, _providerRegistry.ModelToProvider);
        string m = model.ToLowerInvariant();
        bool tools = configured.SupportsTools ?? true;
        bool vision = configured.SupportsVision ?? (m.Contains("vision") || m.Contains("-vl") || m.Contains("neva") || m.Contains("vila") || m.Contains("fuyu") || m.Contains("kosmos"));
        int ctx;
        int maxOut;

        if (m.Contains("guard") || m.Contains("safety") || m.Contains("embed") || m.Contains("retriever") || m.Contains("reranker") || m.Contains("reward") || m.Contains("parse") || m.Contains("detector") || m.Contains("clip") || m.Contains("nv-embed") || m.Contains("embedqa") || m.Contains("cached-model") || m.Contains("rerank") || m.Contains("classification") || m.Contains("riva-translate") || m.Contains("synthetic-video"))
        { ctx = 0; maxOut = 0; tools = false; }
        else if (m.Contains("deepseek"))
        { ctx = 1_000_000; maxOut = 384_000; }
        else if (m.Contains("nemotron-3-super"))
        { ctx = 1_000_000; maxOut = 16384; }
        else if (m.Contains("nemotron") && m.Contains("ultra"))
        { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("nemotron") || m.Contains("nvidia-nemotron"))
        { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("llama-4") || m.Contains("llama-3.3") || m.Contains("llama-3.2") || m.Contains("llama-3.1"))
        { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("llama-2") || m.Contains("codellama"))
        { ctx = 4096; maxOut = 4096; }
        else if (m.Contains("mistral-large-3") || m.Contains("mistral-large-2") || m.Contains("mistral-large"))
        { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("mistral") && (m.Contains("medium") || m.Contains("small")))
        { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("mixtral-8x22b"))
        { ctx = 65536; maxOut = 4096; }
        else if (m.Contains("mixtral") || m.Contains("mistral") || m.Contains("codestral") || m.Contains("ministral") || m.Contains("mistral-nemo"))
        { ctx = 32768; maxOut = 4096; }
        else if (m.Contains("qwen3-coder"))
        { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("qwen"))
        { ctx = 128_000; maxOut = 8192; }
        else if (m.Contains("gemma-4"))
        { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("gemma-3"))
        { ctx = 32768; maxOut = 8192; }
        else if (m.Contains("gemma-2") || m.Contains("gemma-2b") || m.Contains("codegemma"))
        { ctx = 8192; maxOut = 4096; }
        else if (m.Contains("phi-4"))
        { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("phi-3"))
        { ctx = 128_000; maxOut = 4096; }
        else if (m.Contains("granite-34b-code"))
        { ctx = 128_000; maxOut = 4096; }
        else if (m.Contains("granite"))
        { ctx = 128_000; maxOut = 4096; }
        else if (m.Contains("starcoder2"))
        { ctx = 16384; maxOut = 4096; }
        else if (m.Contains("gpt-oss"))
        { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("dbrx") || m.Contains("jamba"))
        { ctx = 32768; maxOut = 4096; }
        else if (m.Contains("yi-large") || m.Contains("seed-oss"))
        { ctx = 32768; maxOut = 4096; }
        else if (m.Contains("kimi"))
        { ctx = 128_000; maxOut = 8192; }
        else if (m.Contains("step-3"))
        { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("glm-5"))
        { ctx = 128_000; maxOut = 8192; }
        else if (m.Contains("solar") || m.Contains("zamba"))
        { ctx = 4096; maxOut = 4096; }
        else if (m.Contains("palmyra"))
        { ctx = 32768; maxOut = 4096; }
        else
        { ctx = 128_000; maxOut = 8192; }

        ctx = configured.ContextLength ?? ctx;
        maxOut = configured.MaxOutputTokens ?? maxOut;

        string[] capabilities = vision ? ["completion", "tools", "vision"] : ["completion", "tools"];

        string family = configured.Family
            ?? (m.Contains("deepseek") ? "deepseek"
            : m.Contains("nemotron") || m.Contains("llama-3.1-nemotron") || m.Contains("llama-3.3-nemotron") || m.Contains("nvidia-nemotron") || m.Contains("cosmos-reason") ? "nvidia"
            : m.Contains("llama") || m.Contains("codellama") ? "meta"
            : m.Contains("mistral") || m.Contains("mixtral") || m.Contains("codestral") || m.Contains("ministral") ? "mistralai"
            : m.Contains("qwen") ? "qwen"
            : m.Contains("gemma") || m.Contains("codegemma") ? "google"
            : m.Contains("phi-") ? "microsoft"
            : m.Contains("granite") ? "ibm"
            : m.Contains("gpt-oss") ? "openai"
            : m.Contains("nemotron") ? "nvidia"
            : _providerRegistry.ModelToProvider.TryGetValue(model, out ProviderInfo prov) ? prov.Name
            : "api");

        return (ctx, maxOut, tools, vision, capabilities, family);
    }

    internal CancellationTokenSource? CreateModelTimeoutCts(string model, CancellationToken outer)
    {
        ModelExecutionConfig exec = _modelSelectionStore.GetExecutionConfigForModel(model, _providerRegistry.ModelToProvider);
        if (!exec.TimeoutSeconds.HasValue || exec.TimeoutSeconds.Value <= 0)
        {
            return null;
        }

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        linked.CancelAfter(TimeSpan.FromSeconds(exec.TimeoutSeconds.Value));
        return linked;
    }

    internal ModelExecutionConfig GetExecutionConfigForModel(string model) =>
        _modelSelectionStore.GetExecutionConfigForModel(model, _providerRegistry.ModelToProvider);

    private static async Task<string[]> TryGetModelsFromProvider(ProviderInfo provider, CancellationToken ct)
    {
        try
        {
            string modelsPath = provider.Name.Equals("ollama", StringComparison.OrdinalIgnoreCase)
                ? "/api/tags"
                : "v1/models";

            using HttpResponseMessage resp = await provider.Client.GetAsync(modelsPath, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return [];
            }

            string body = await resp.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(body);
            return ExtractModels(doc.RootElement);
        }
        catch
        {
            return [];
        }
    }

    private static string[] ExtractModels(JsonElement root)
    {
        IEnumerable<JsonElement> items = [];

        if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
        {
            items = data.EnumerateArray();
        }
        else if (root.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
        {
            items = models.EnumerateArray();
        }

        return items
            .Select(item =>
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    return item.GetString();
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                {
                    return id.GetString();
                }

                if (item.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                {
                    return name.GetString();
                }

                if (item.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.String)
                {
                    return model.GetString();
                }

                return null;
            })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
