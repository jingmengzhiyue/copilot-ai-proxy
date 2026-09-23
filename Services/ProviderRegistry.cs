internal sealed class ProviderRegistry
{
    // Publication and a complete routing decision share this short-lived gate.
    internal object CatalogSync { get; } = new();

    private readonly List<ProviderInfo> _providers = [];
    private Dictionary<string, ProviderInfo> _modelToProvider = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _modelToUpstream = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<ProviderInfo>> _upstreamToProviders = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry(ProviderHttpClientFactory httpClientFactory)
    {
        DefaultModel = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-v4-pro";
        DiscoverProviders(httpClientFactory);

        if (_providers.Count == 0)
        {
            Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  ⚠️  No se encontraron API keys configuradas.                    ║");
            Console.WriteLine("║                                                                  ║");
            Console.WriteLine("║  Edita el archivo .env en la raíz del proyecto y descomenta     ║");
            Console.WriteLine("║  al menos un proveedor, por ejemplo:                            ║");
            Console.WriteLine("║                                                                  ║");
            Console.WriteLine("║    PROVIDER_DEEPSEEK_API_KEY=sk-tu-key-aqui                      ║");
            Console.WriteLine("║                                                                  ║");
            Console.WriteLine("║  Luego reinicia la aplicación.                                   ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
        }
    }

    internal string DefaultModel { get; }

    internal IReadOnlyList<ProviderInfo> Providers => _providers;

    internal IReadOnlyDictionary<string, ProviderInfo> ModelToProvider => _modelToProvider;

    internal IReadOnlyDictionary<string, string> ModelToUpstream => _modelToUpstream;

    internal void UpdateModelMappings(
        Dictionary<string, ProviderInfo> modelToProvider,
        Dictionary<string, string> modelToUpstream,
        Dictionary<string, List<ProviderInfo>>? upstreamToProviders = null)
    {
        lock (CatalogSync)
            UpdateModelMappingsCore(modelToProvider, modelToUpstream, upstreamToProviders);
    }

    private void UpdateModelMappingsCore(
        Dictionary<string, ProviderInfo> modelToProvider,
        Dictionary<string, string> modelToUpstream,
        Dictionary<string, List<ProviderInfo>>? upstreamToProviders)
    {
        _modelToProvider = modelToProvider;
        _modelToUpstream = modelToUpstream;

        if (upstreamToProviders is not null)
        {
            // Caller (ModelCatalogService) already ordered providers by configured priority.
            _upstreamToProviders = upstreamToProviders;
            return;
        }

        // Fallback: build from modelToProvider, preserving discovery order as a tie-break.
        Dictionary<string, List<ProviderInfo>> upstream = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string modelId, ProviderInfo prov) in modelToProvider)
        {
            string up = modelToUpstream.TryGetValue(modelId, out string? u) ? u : modelId;
            if (!upstream.TryGetValue(up, out List<ProviderInfo>? list))
            {
                list = [];
                upstream[up] = list;
            }

            if (!list.Any(p => p.Name.Equals(prov.Name, StringComparison.OrdinalIgnoreCase)))
            {
                int order = _providers.FindIndex(p => p.Name.Equals(prov.Name, StringComparison.OrdinalIgnoreCase));
                int insertAt = list.FindIndex(p => _providers.FindIndex(q => q.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) > order);
                if (insertAt < 0)
                {
                    list.Add(prov);
                }
                else
                {
                    list.Insert(insertAt, prov);
                }
            }
        }

        _upstreamToProviders = upstream;
    }

    internal ProviderInfo ResolveProvider(string? requestedModel)
    {
        if (_providers.Count == 0)
            throw new InvalidOperationException("ProviderRegistry: no providers are registered (check PROVIDER_*_API_KEY env vars).");
        if (!string.IsNullOrWhiteSpace(requestedModel) && _modelToProvider.TryGetValue(requestedModel, out ProviderInfo provider))
            return provider;
        return _providers[0];
    }

    internal string ResolveModel(string? requestedModel) =>
        TryResolveCatalogModel(requestedModel, out string resolved) ? resolved : DefaultModel;

    /// <summary>
    /// Attempts to resolve a requested model id against the discovered catalog.
    /// Returns false (instead of degrading to <see cref="DefaultModel"/>) when the id
    /// is unknown, so callers can distinguish "resolved" from "fell back to default".
    /// </summary>
    internal bool TryResolveCatalogModel(string? requestedModel, out string resolved)
    {
        lock (CatalogSync)
            return TryResolveCatalogModelCore(requestedModel, out resolved);
    }

    private bool TryResolveCatalogModelCore(string? requestedModel, out string resolved)
    {
        resolved = DefaultModel;
        if (_providers.Count == 0)
            return false;

        if (string.IsNullOrWhiteSpace(requestedModel))
            return false;

        // Verbatim hit first: some upstream ids legitimately contain ':' (e.g.
        // OpenRouter "model:free" / "model:batch") and must not lose that suffix.
        if (_modelToProvider.ContainsKey(requestedModel))
        {
            resolved = requestedModel;
            return true;
        }

        // Strip Ollama-style tag suffix (e.g., "deepseek-v4-pro:latest" → "deepseek-v4-pro")
        string cleanModel = StripTagSuffix(requestedModel);
        if (_modelToProvider.ContainsKey(cleanModel))
        {
            resolved = cleanModel;
            return true;
        }

        // Accept the OpenAI-style "provider/model" form (e.g. "groq/llama-3.3-70b-versatile"
        // or "nvidia/qwen3.5-397b-a17b"). Some providers (NVIDIA in particular) expose
        // upstream ids with a slash prefix ("qwen/qwen3.5-397b-a17b"), so first try the
        // full id verbatim, then fall back to stripping the provider prefix, and finally
        // try matching the requested bare against any upstream id owned by the hinted
        // provider that ends with that bare.
        int slash = cleanModel.IndexOf('/');
        if (slash > 0 && slash < cleanModel.Length - 1)
        {
            string bare = cleanModel[(slash + 1)..];
            if (_modelToProvider.ContainsKey(bare))
            {
                resolved = bare;
                return true;
            }

            // Last-resort match: look for any upstream id in the hinted provider
            // whose suffix equals the requested bare (e.g. requested bare
            // "qwen3.5-397b-a17b" matches upstream "qwen/qwen3.5-397b-a17b").
            string? providerHint = cleanModel[..slash];
            foreach (KeyValuePair<string, ProviderInfo> kv in _modelToProvider)
            {
                if (!string.Equals(kv.Value.Name, providerHint, StringComparison.OrdinalIgnoreCase))
                    continue;
                int lastSlash = kv.Key.LastIndexOf('/');
                string suffix = lastSlash > 0 ? kv.Key[(lastSlash + 1)..] : kv.Key;
                if (string.Equals(suffix, bare, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = kv.Key;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Finds a configured provider by exact name (case-insensitive). Never uses
    /// prefix/substring matching: "kimi" must not resolve the "kimicode" provider
    /// or vice versa.
    /// </summary>
    internal ProviderInfo? FindConfiguredProvider(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (ProviderInfo provider in _providers)
        {
            if (string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase))
                return provider;
        }

        return null;
    }

    /// <summary>Removes the tag portion of an Ollama model name (e.g. "model:latest" → "model").</summary>
    internal static string StripTagSuffix(string model)
    {
        return model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? model[..^7] : model;
    }

    internal string ResolveUpstreamModel(string? requestedModel)
    {
        lock (CatalogSync)
        {
            string resolved = ResolveModel(requestedModel);
            return _modelToUpstream.TryGetValue(resolved, out string? upstream) ? upstream : resolved;
        }
    }

    /// <summary>
    /// Returns the ordered list of (provider, upstreamModel) candidates for a requested model.
    /// A provider-qualified id ("model@provider") resolves to a single candidate (no failover).
    /// A bare model id returns every provider that offers it, ordered by configured priority.
    /// </summary>
    internal IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> ResolveCandidates(string? requestedModel)
    {
        lock (CatalogSync)
            return ResolveCandidatesCore(requestedModel);
    }

    private IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> ResolveCandidatesCore(string? requestedModel)
    {
        string resolved = ResolveModel(requestedModel);
        string upstream = _modelToUpstream.TryGetValue(resolved, out string? up) ? up : resolved;

        // Explicit "model@provider" alias -> single, exact candidate.
        bool isQualified = resolved.Contains('@');
        if (isQualified && _modelToProvider.TryGetValue(resolved, out ProviderInfo exact))
        {
            return [(exact, upstream)];
        }

        if (_upstreamToProviders.TryGetValue(upstream, out List<ProviderInfo>? providers) && providers.Count > 0)
        {
            return providers.Select(p => (p, upstream)).ToArray();
        }

        // Empty registry → return no candidates; ResolveProvider would throw.
        if (_providers.Count == 0)
            return [];
        return [(ResolveProvider(resolved), upstream)];
    }

    private void DiscoverProviders(ProviderHttpClientFactory httpClientFactory)
    {
        foreach (string providerName in ProviderCapabilitiesRegistry.KnownProviders)
        {
            ProviderCapabilities caps = ProviderCapabilitiesRegistry.Get(providerName);
            string prefix = caps.EnvPrefix;

            string? apiKey = Environment.GetEnvironmentVariable($"PROVIDER_{prefix}_API_KEY");

            // Backward compatibility: Ollama Cloud also accepts PROVIDER_OLLAMA_API_KEY.
            if (string.IsNullOrWhiteSpace(apiKey) && providerName == "ollama")
            {
                apiKey = Environment.GetEnvironmentVariable("PROVIDER_OLLAMA_API_KEY");
            }

            // Skip providers that require an API key when none is configured.
            // Ollama can still be discovered later as a local instance (no API key needed).
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                continue;
            }

            string? configuredBaseUrl = Environment.GetEnvironmentVariable($"PROVIDER_{prefix}_BASE_URL");
            string baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
                ? caps.DefaultBaseUrl
                : configuredBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                Console.WriteLine($"Provider '{providerName}' is configured with an API key but no base URL. Set PROVIDER_{prefix}_BASE_URL to enable it.");
                continue;
            }

            baseUrl = NormalizeBaseUrl(baseUrl, caps);

            HttpClient provClient = httpClientFactory.CreateProviderClient(providerName, baseUrl, apiKey);
            _providers.Add(new ProviderInfo(providerName, apiKey, baseUrl, provClient, caps));
        }

        // ── Local Ollama (no API key required) ──────────────────────────
        // When PROVIDER_OLLAMA_BASE_URL points to localhost / 127.0.0.1 / ::1,
        // register a second "ollama" entry that works without an API key.
        string? ollamaBaseUrl = Environment.GetEnvironmentVariable("PROVIDER_OLLAMA_BASE_URL");
        if (!string.IsNullOrWhiteSpace(ollamaBaseUrl)
            && Uri.TryCreate(ollamaBaseUrl, UriKind.Absolute, out Uri? localUri) && localUri.IsLoopback
            && !_providers.Any(p => p.BaseUrl.Equals(ollamaBaseUrl, StringComparison.OrdinalIgnoreCase)))
        {
            ProviderCapabilities ollamaCaps = ProviderCapabilitiesRegistry.Get("ollama");
            ollamaBaseUrl = NormalizeBaseUrl(ollamaBaseUrl, ollamaCaps);
            string localName = _providers.Any(p => p.Name == "ollama") ? "ollama-local" : "ollama";
            HttpClient localClient = httpClientFactory.CreateProviderClient(localName, ollamaBaseUrl, apiKey: "");
            _providers.Add(new ProviderInfo(localName, "", ollamaBaseUrl, localClient, ollamaCaps));
        }

        // ── Legacy DEEPSEEK_API_KEY fallback ────────────────────────────
        string? legacyKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(legacyKey) && !_providers.Any(p => p.Name == "deepseek"))
        {
            ProviderCapabilities deepseekCaps = ProviderCapabilitiesRegistry.Get("deepseek");
            string legacyUrl = NormalizeBaseUrl(
                Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? deepseekCaps.DefaultBaseUrl,
                deepseekCaps);
            _providers.Add(new ProviderInfo("deepseek", legacyKey, legacyUrl,
                httpClientFactory.CreateProviderClient("deepseek", legacyUrl, legacyKey), deepseekCaps));
        }
    }

    /// <summary>
    /// Collapses a duplicated API-version segment between the configured base URL
    /// and the capability paths. Users often paste the full versioned endpoint
    /// (e.g. "https://api.kimi.com/coding/v1") while <see cref="ProviderCapabilities.ChatPath"/>
    /// already carries the version prefix ("v1/chat/completions") — joining both
    /// would produce ".../v1/v1/chat/completions" and an upstream 404.
    /// </summary>
    private static string NormalizeBaseUrl(string baseUrl, ProviderCapabilities caps)
    {
        string trimmed = baseUrl.TrimEnd('/');
        string[] segments = caps.ChatPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Match the longest API prefix first (Google uses v1beta/openai).
        for (int count = segments.Length - 1; count > 0; count--)
        {
            string prefix = string.Join('/', segments.Take(count));
            if (trimmed.EndsWith($"/{prefix}", StringComparison.OrdinalIgnoreCase))
                return trimmed[..^(prefix.Length + 1)];
        }

        return trimmed;
    }
}
