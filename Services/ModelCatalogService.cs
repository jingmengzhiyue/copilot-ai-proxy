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
            // upstream-id (case-insensitive) -> ordered list of claimants by (priority, providerOrder)
            Dictionary<string, List<Claimant>> claimsByUpstream =
                new(StringComparer.OrdinalIgnoreCase);

            foreach (ProviderInfo prov in _providerRegistry.Providers)
            {
                string[] discovered = await TryGetModelsFromProvider(prov, ct);
                foreach (string m in discovered)
                {
                    if (string.IsNullOrWhiteSpace(m))
                    {
                        continue;
                    }

                    ModelSelectionEntry? selection = _modelSelectionStore.FindModelSelectionEntry(m, prov.Name);
                    if (!selection.HasValue)
                    {
                        continue;
                    }

                    (int ContextLength, _, _, _, _, _) = GetModelProfile(m);
                    if (ContextLength == 0)
                    {
                        continue;
                    }

                    int prio = selection.Value.Priority;
                    if (!claimsByUpstream.TryGetValue(m, out List<Claimant>? list))
                    {
                        list = [];
                        claimsByUpstream[m] = list;
                    }
                    // Substring selection may admit dated variants, but a configured
                    // display name identifies its exact model, not the first variant
                    // returned by discovery. Otherwise /api/tags changes identity.
                    string? displayName = string.Equals(selection.Value.Match, m, StringComparison.OrdinalIgnoreCase)
                        ? NormalizeDisplayName(selection.Value.DisplayName) : null;
                    list.Add(new Claimant(prov, m, prio, displayName));
                }
            }

            if (claimsByUpstream.Count == 0)
            {
                return;
            }

            // Tie-break: configured provider order in ProviderRegistry.Providers.
            Dictionary<string, int> providerOrder = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _providerRegistry.Providers.Count; i++)
            {
                providerOrder[_providerRegistry.Providers[i].Name] = i;
            }

            Dictionary<string, ProviderInfo> newMap = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> newUpstream = new(StringComparer.OrdinalIgnoreCase);
            List<string> allModels = [];

            foreach (KeyValuePair<string, List<Claimant>> kv in claimsByUpstream)
            {
                List<Claimant> ordered = kv.Value
                    .OrderBy(c => c.Priority)
                    .ThenBy(c => providerOrder.TryGetValue(c.Provider.Name, out int o) ? o : int.MaxValue)
                    .ToList();

                // Every claimant also gets a qualified alias.
                foreach (Claimant c in ordered)
                {
                    string qualified = $"{c.UpstreamId}@{c.Provider.Name}";
                    AddModelAlias(qualified, c.Provider, c.UpstreamId, newMap, newUpstream, allModels);

                    if (HasDisplayAlias(c))
                    {
                        AddModelAlias($"{c.DisplayName}@{c.Provider.Name}", c.Provider, c.UpstreamId, newMap, newUpstream, allModels);
                    }
                }

                // Bare name: lowest priority (then configured order) wins.
                Claimant winner = ordered[0];
                string bare = winner.UpstreamId;
                AddModelAlias(bare, winner.Provider, bare, newMap, newUpstream, allModels);

                if (HasDisplayAlias(winner))
                {
                    AddModelAlias(winner.DisplayName!, winner.Provider, winner.UpstreamId, newMap, newUpstream, allModels);
                }
            }

            if (allModels.Count > 0)
            {
                AvailableModels = allModels
                    .OrderBy(model => _modelSelectionStore.GetPreferredModelPriority(newUpstream[model], newMap[model].Name))
                    .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // Build upstream→providers map in priority order (so failover follows
                // configured priority, not the iteration order of newMap which is "first
                // qualified alias wins, then the bare name").
                Dictionary<string, List<ProviderInfo>> upstreamToProviders = new(StringComparer.OrdinalIgnoreCase);
                foreach (string upstreamId in claimsByUpstream.Keys)
                {
                    List<Claimant> ordered = claimsByUpstream[upstreamId]
                        .OrderBy(c => c.Priority)
                        .ThenBy(c => providerOrder.TryGetValue(c.Provider.Name, out int o) ? o : int.MaxValue)
                        .ToList();
                    upstreamToProviders[upstreamId] = [.. ordered.Select(c => c.Provider).Distinct(ProviderInfoNameComparer.Instance)];
                }
                _providerRegistry.UpdateModelMappings(newMap, newUpstream, upstreamToProviders);
                ModelsLastRefreshUtc = DateTime.UtcNow;
            }
        }
        catch
        {
            // keep current fallback list when discovery fails
        }
    }

    private readonly record struct Claimant(ProviderInfo Provider, string UpstreamId, int Priority, string? DisplayName);

    private static void AddModelAlias(
        string alias,
        ProviderInfo provider,
        string upstreamId,
        Dictionary<string, ProviderInfo> modelToProvider,
        Dictionary<string, string> modelToUpstream,
        List<string> allModels)
    {
        if (string.IsNullOrWhiteSpace(alias) || modelToProvider.ContainsKey(alias))
        {
            return;
        }

        modelToProvider[alias] = provider;
        modelToUpstream[alias] = upstreamId;
        allModels.Add(alias);
    }

    private static string? NormalizeDisplayName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

    private static bool HasDisplayAlias(Claimant claimant) =>
        !string.IsNullOrWhiteSpace(claimant.DisplayName)
        && !string.Equals(claimant.DisplayName, claimant.UpstreamId, StringComparison.OrdinalIgnoreCase);

    private sealed class ProviderInfoNameComparer : IEqualityComparer<ProviderInfo>
    {
        public static readonly ProviderInfoNameComparer Instance = new();
        public bool Equals(ProviderInfo x, ProviderInfo y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(ProviderInfo obj) =>
            obj.Name is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
    }

    internal (int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) GetModelProfile(string model)
    {
        string configModel = ResolveConfigModel(model);
        ModelExecutionConfig configured = _modelSelectionStore.GetExecutionConfigForModel(configModel, _providerRegistry.ModelToProvider);
        string m = configModel.ToLowerInvariant();
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
        else if (m.Contains("zai-glm"))
        { ctx = 128_000; maxOut = 32768; }
        else if (m.Contains("glm"))
        { ctx = 128_000; maxOut = 32768; }
        else if (m.Contains("minimax"))
        { ctx = 128_000; maxOut = 32768; }
        else if (m.Contains("cogito"))
        { ctx = 128_000; maxOut = 32768; }
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

    internal CancellationTokenSource? CreateModelTimeoutCts(
        string model,
        string providerName,
        CancellationToken outer)
    {
        ModelExecutionConfig exec = GetExecutionConfigForModel(model, providerName);
        if (!exec.TimeoutSeconds.HasValue || exec.TimeoutSeconds.Value <= 0)
        {
            return null;
        }

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        linked.CancelAfter(TimeSpan.FromSeconds(exec.TimeoutSeconds.Value));
        return linked;
    }

    internal ModelExecutionConfig GetExecutionConfigForModel(string model) =>
        _modelSelectionStore.GetExecutionConfigForModel(ResolveConfigModel(model), _providerRegistry.ModelToProvider);

    internal ModelExecutionConfig GetExecutionConfigForModel(string model, string providerName) =>
        _modelSelectionStore.FindModelSelectionEntry(ResolveConfigModel(model), providerName)?.Execution
        ?? new ModelExecutionConfig();

    private string ResolveConfigModel(string model) =>
        _providerRegistry.ModelToUpstream.TryGetValue(model, out string? upstream) ? upstream : model;

    /// <summary>
    /// The single, shared model-identity resolver used by every endpoint
    /// (/v1/chat/completions, /api/chat, /api/show). It normalizes every alias form
    /// this proxy itself can emit — plus the OpenAI/Ollama idioms clients use — into
    /// a canonical (model, provider) identity:
    ///
    ///   - "model@provider"        → STRICT: only that provider is consulted. Unknown
    ///     provider or model yields a <see cref="RouteErrorKind"/> error and never
    ///     falls back to another provider or to the default model.
    ///   - "PROVIDER - model"      → the display alias emitted as "name" by /api/tags
    ///     (e.g. "KIMICODE - k3:latest"). Also STRICT — it carries explicit provider
    ///     information, so it must round-trip to that exact provider.
    ///   - "provider/model"        → strict when the prefix names a configured
    ///     provider; otherwise the slash is part of the model id (OpenRouter/NVIDIA
    ///     upstream ids like "openai/gpt-oss-120b").
    ///   - "model"                 → automatic routing: discovered catalog first, then
    ///     config-declared exact matches, then config display names, and only then the
    ///     default model (flagged via <see cref="ResolvedRoute.FallbackUsed"/>).
    ///
    /// Ollama ":latest" suffixes are stripped from the model and from the provider hint,
    /// but only as a fallback tier — upstream ids that legitimately contain ':'
    /// (OpenRouter ":free" / ":batch") are tried verbatim first.
    /// </summary>
    internal ResolvedRoute ResolveRoute(string? requestedModel)
    {
        bool isDefaultRequest = string.IsNullOrWhiteSpace(requestedModel);
        string requested = isDefaultRequest ? _providerRegistry.DefaultModel : requestedModel!.Trim();

        // A registered literal ID wins over compatibility syntax, including '@'
        // and vendor prefixes that happen to be configured provider names.
        bool IsLiteralId(string id) =>
            (_providerRegistry.ModelToProvider.ContainsKey(id)
                && (!_providerRegistry.ModelToUpstream.TryGetValue(id, out string? upstreamId)
                    || string.Equals(id, upstreamId, StringComparison.OrdinalIgnoreCase)))
            || ConfigExactClaims(id) is not null;
        bool literalModel = IsLiteralId(requested) || IsLiteralId(ProviderRegistry.StripTagSuffix(requested));

        string modelPart = requested;
        string? providerHint = null;
        int at = requested.LastIndexOf('@');
        bool qualifiedLiteral = at > 0 && IsLiteralId(requested[..at]);
        // Parse our display prefix first, so '@' inside a model/display name is
        // preserved. Unknown display providers remain strict instead of defaulting.
        int displaySeparator = requested.IndexOf(" - ", StringComparison.Ordinal);
        if (!literalModel && !qualifiedLiteral && displaySeparator > 0)
        {
            providerHint = requested[..displaySeparator];
            modelPart = requested[(displaySeparator + 3)..];
        }
        if (!literalModel && providerHint is null && at > 0 && at < requested.Length - 1)
        {
            string hintCandidate = ProviderRegistry.StripTagSuffix(requested[(at + 1)..].Trim());
            if (hintCandidate.Length > 0)
            {
                modelPart = requested[..at];
                providerHint = hintCandidate;
            }
        }

        // ── 3. OpenAI-style "provider/model" prefix ──────────────────────
        // Treated as a hint only when the prefix names a CONFIGURED provider;
        // otherwise the slash belongs to the model id itself.
        string? slashRest = null;
        if (!literalModel && providerHint is null)
        {
            int slash = modelPart.IndexOf('/');
            if (slash > 0 && slash < modelPart.Length - 1)
            {
                string prefix = modelPart[..slash];
                if (_providerRegistry.FindConfiguredProvider(prefix) is not null)
                {
                    providerHint = prefix;
                    slashRest = modelPart[(slash + 1)..];
                }
            }
        }

        // Model-id tiers, most literal first: upstream ids may legitimately contain
        // ':' (OpenRouter ":free"/":batch"). Only one terminal ':latest' is
        // compatibility syntax; literal IDs are tested before the stripped tier.
        modelPart = modelPart.Trim();
        List<string> modelTiers = [];
        if (modelPart.Length > 0)
        {
            modelTiers.Add(modelPart);
        }

        string lastTagStripped = ProviderRegistry.StripTagSuffix(modelPart);
        if (lastTagStripped.Length > 0 && !modelTiers.Contains(lastTagStripped, StringComparer.OrdinalIgnoreCase))
        {
            modelTiers.Add(lastTagStripped);
        }

        if (modelTiers.Count == 0)
        {
            modelTiers.Add(_providerRegistry.DefaultModel);
        }

        // ── 4. Strict provider routing ───────────────────────────────────
        if (providerHint is not null)
        {
            ProviderInfo? pinned = _providerRegistry.FindConfiguredProvider(providerHint);
            if (pinned is null)
            {
                return new ResolvedRoute(requested, modelTiers[0], providerHint, modelTiers[0], [], false,
                    RouteErrorKind.ProviderNotFound,
                    $"Provider '{providerHint}' is not configured. Configured providers: {string.Join(", ", _providerRegistry.Providers.Select(p => p.Name))}.");
            }

            ProviderInfo provider = pinned.Value;
            (string Canonical, string Upstream)? match = ResolveWithinProvider(provider, modelTiers, slashRest);
            if (match is null)
            {
                return new ResolvedRoute(requested, modelTiers[0], provider.Name, modelTiers[0], [], false,
                    RouteErrorKind.ModelNotFound,
                    $"Model '{modelTiers[0]}' is not available from provider '{provider.Name}'.");
            }

            return new ResolvedRoute(requested, match.Value.Canonical, provider.Name, match.Value.Canonical,
                [(provider, match.Value.Upstream)], false, RouteErrorKind.None, null);
        }

        // Check catalog and configuration at each literal tier before considering
        // ':latest' removal. Discovery may be incomplete while config is valid.
        foreach (string tier in modelTiers)
        {
            if (_providerRegistry.ModelToProvider.ContainsKey(tier))
            {
                string canonical = _providerRegistry.ModelToUpstream.TryGetValue(tier, out string? up) ? up : tier;
                return new ResolvedRoute(requested, canonical, null, canonical,
                    _providerRegistry.ResolveCandidates(tier), false, RouteErrorKind.None, null);
            }
            (ProviderInfo Provider, string UpstreamModel)[]? claims = ConfigExactClaims(tier);
            if (claims is not null)
            {
                return new ResolvedRoute(requested, claims[0].UpstreamModel, null, claims[0].UpstreamModel,
                    claims, false, RouteErrorKind.None, null);
            }
        }

        // ── 5c. Bare model, config display-name match ────────────────────
        // Custom display names emitted by /api/tags (e.g. "Qwen3.7 Max:latest")
        // round-trip to their canonical configured model.
        foreach (string tier in modelTiers)
        {
            List<(ProviderInfo Provider, ModelSelectionEntry Entry)> displayClaims = [];
            foreach (ProviderInfo prov in _providerRegistry.Providers)
            {
                ModelSelectionEntry? entry = _modelSelectionStore.FindExactDisplayNameEntry(tier, prov.Name);
                if (entry.HasValue)
                {
                    displayClaims.Add((prov, entry.Value));
                }
            }

            if (displayClaims.Count > 0)
            {
                (ProviderInfo Provider, string UpstreamModel)[] ordered = displayClaims
                    .OrderBy(c => c.Entry.Priority)
                    .ThenBy(c => ConfiguredProviderOrder(c.Provider.Name))
                    .Select(c => (c.Provider, c.Entry.Match))
                    .ToArray();
                return new ResolvedRoute(requested, ordered[0].UpstreamModel, null, ordered[0].UpstreamModel,
                    ordered, false, RouteErrorKind.None, null);
            }
        }

        // ── 5d. Unknown bare model → configured default (flagged) ────────
        // Legacy behaviour for BYOM clients that send arbitrary ids. NormalizedModel
        // keeps the unresolved request id; the fallback is a separate, visible step.
        string fallback = _providerRegistry.DefaultModel;
        return new ResolvedRoute(requested, modelTiers[0], null, fallback,
            _providerRegistry.ResolveCandidates(fallback), !isDefaultRequest, RouteErrorKind.None, null);
    }

    /// <summary>
    /// Providers whose config declares an exact match for <paramref name="model"/>,
    /// ordered by (priority, configured provider order). Null when none claim it.
    /// </summary>
    private (ProviderInfo Provider, string UpstreamModel)[]? ConfigExactClaims(string model)
    {
        List<(ProviderInfo Provider, ModelSelectionEntry Entry)> claims = [];
        foreach (ProviderInfo prov in _providerRegistry.Providers)
        {
            ModelSelectionEntry? entry = _modelSelectionStore.FindExactModelSelectionEntry(model, prov.Name);
            if (entry.HasValue)
            {
                claims.Add((prov, entry.Value));
            }
        }

        if (claims.Count == 0)
        {
            return null;
        }

        return claims
            .OrderBy(c => c.Entry.Priority)
            .ThenBy(c => ConfiguredProviderOrder(c.Provider.Name))
            .Select(c => (c.Provider, c.Entry.Match))
            .ToArray();
    }

    /// <summary>
    /// Resolves the upstream model id for <paramref name="modelPart"/> within one
    /// specific provider. Order of evidence: catalog-qualified alias → bare/display
    /// alias owned by this provider → discovered upstream id owned by this provider →
    /// NVIDIA-style last-slash-segment suffix match → config-declared exact match.
    /// Returns null when the provider cannot serve the model.
    /// </summary>
    private (string Canonical, string Upstream)? ResolveWithinProvider(
        ProviderInfo provider,
        IReadOnlyList<string> modelTiers,
        string? slashRest)
    {
        List<string> ids = [.. modelTiers];
        if (!string.IsNullOrEmpty(slashRest))
        {
            if (!ids.Contains(slashRest, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(slashRest);
            }

            string restStripped = ProviderRegistry.StripTagSuffix(slashRest);
            if (!ids.Contains(restStripped, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(restStripped);
            }

        }

        foreach (string id in ids)
        {
            // Qualified alias registered by catalog discovery ("upstream@provider").
            string qualified = $"{id}@{provider.Name}";
            if (_providerRegistry.ModelToUpstream.TryGetValue(qualified, out string? qualifiedUpstream))
            {
                return (qualifiedUpstream, qualifiedUpstream);
            }

            // Bare/display alias owned by this provider.
            if (_providerRegistry.ModelToProvider.TryGetValue(id, out ProviderInfo owner)
                && string.Equals(owner.Name, provider.Name, StringComparison.OrdinalIgnoreCase))
            {
                string canonical = _providerRegistry.ModelToUpstream.TryGetValue(id, out string? bareUpstream) ? bareUpstream : id;
                return (canonical, canonical);
            }

            // Discovered upstream id claimed by this provider even when another
            // provider won the bare name.
            foreach (KeyValuePair<string, ProviderInfo> kv in _providerRegistry.ModelToProvider)
            {
                if (!string.Equals(kv.Value.Name, provider.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string up = _providerRegistry.ModelToUpstream.TryGetValue(kv.Key, out string? u) ? u : kv.Key;
                if (string.Equals(up, id, StringComparison.OrdinalIgnoreCase))
                {
                    return (id, up);
                }
            }

            ModelSelectionEntry? exact = _modelSelectionStore.FindExactModelSelectionEntry(id, provider.Name);
            if (exact.HasValue)
            {
                return (exact.Value.Match, exact.Value.Match);
            }
        }

        // NVIDIA-style suffix: "qwen3.5-397b-a17b" → upstream "qwen/qwen3.5-397b-a17b".
        foreach (string id in ids)
        {
            if (id.Contains('/')) continue;
            string wanted = LastSlashSegment(id);
            foreach (KeyValuePair<string, ProviderInfo> kv in _providerRegistry.ModelToProvider)
            {
                if (!string.Equals(kv.Value.Name, provider.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string up = _providerRegistry.ModelToUpstream.TryGetValue(kv.Key, out string? u) ? u : kv.Key;
                if (string.Equals(LastSlashSegment(up), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return (up, up);
                }
            }
        }

        // Config-declared exact match (covers providers whose /models discovery failed).
        // Returns the configured match string so the upstream id keeps its canonical casing.
        foreach (string id in ids)
        {
            ModelSelectionEntry? entry = _modelSelectionStore.FindExactModelSelectionEntry(id, provider.Name);
            if (entry.HasValue)
            {
                return (entry.Value.Match, entry.Value.Match);
            }
        }

        // Config-declared suffix match: /api/tags strips the vendor prefix from
        // display names, so "CUSTOMOPENAI - muse-spark-1.1" must find the configured
        // match "meta/muse-spark-1.1".
        foreach (string id in ids)
        {
            ModelSelectionEntry? entry = _modelSelectionStore.FindSuffixModelSelectionEntry(id, provider.Name);
            if (entry.HasValue)
            {
                return (entry.Value.Match, entry.Value.Match);
            }
        }

        // Config-declared display name (covers custom /api/tags names like "Qwen3.7 Max").
        foreach (string id in ids)
        {
            ModelSelectionEntry? entry = _modelSelectionStore.FindExactDisplayNameEntry(id, provider.Name);
            if (entry.HasValue)
            {
                return (entry.Value.Match, entry.Value.Match);
            }
        }

        return null;
    }

    private int ConfiguredProviderOrder(string providerName)
    {
        for (int i = 0; i < _providerRegistry.Providers.Count; i++)
        {
            if (string.Equals(_providerRegistry.Providers[i].Name, providerName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static string LastSlashSegment(string id)
    {
        int slash = id.LastIndexOf('/');
        return slash >= 0 && slash < id.Length - 1 ? id[(slash + 1)..] : id;
    }

    /// <summary>
    /// Ensures the configured default model (from DEEPSEEK_MODEL env var) is always in
    /// <see cref="AvailableModels"/>, even when the owning provider's model discovery
    /// fails (e.g. expired API key, temporary network issue).
    /// </summary>
    internal void EnsureDefaultModelPresent(CancellationToken ct)
    {
        string defaultModel = _providerRegistry.DefaultModel;
        if (AvailableModels.Any(m => string.Equals(m, defaultModel, StringComparison.OrdinalIgnoreCase)))
        {
            return; // already present
        }

        // The default model is not in the discovered list. Try to find its provider
        // and add a bare entry so it's always selectable in Copilot BYOM.
        foreach (ProviderInfo prov in _providerRegistry.Providers)
        {
            if (!_modelSelectionStore.IsPreferredModel(defaultModel, prov.Name))
            {
                continue;
            }

            // Register the default model with this provider.
            Dictionary<string, ProviderInfo> newMap = new(_providerRegistry.ModelToProvider, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> newUpstream = new(_providerRegistry.ModelToUpstream, StringComparer.OrdinalIgnoreCase);

            string bare = defaultModel;
            if (!newMap.ContainsKey(bare))
            {
                newMap[bare] = prov;
                newUpstream[bare] = bare;
            }

            string qualified = $"{bare}@{prov.Name}";
            if (!newMap.ContainsKey(qualified))
            {
                newMap[qualified] = prov;
                newUpstream[qualified] = bare;
            }

            _providerRegistry.UpdateModelMappings(newMap, newUpstream);

            // Prepend the default model to AvailableModels, keeping all existing discovered models.
            AvailableModels = [bare, .. AvailableModels.Where(m => !string.Equals(m, bare, StringComparison.OrdinalIgnoreCase))];
            return;
        }

        // Fallback: no configured provider matches the default model, add it anyway
        // with the first available provider.
        if (_providerRegistry.Providers.Count > 0)
        {
            ProviderInfo first = _providerRegistry.Providers[0];
            Dictionary<string, ProviderInfo> newMap = new(_providerRegistry.ModelToProvider, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> newUpstream = new(_providerRegistry.ModelToUpstream, StringComparer.OrdinalIgnoreCase);
            newMap[defaultModel] = first;
            newUpstream[defaultModel] = defaultModel;
            _providerRegistry.UpdateModelMappings(newMap, newUpstream);
            AvailableModels = [defaultModel, .. AvailableModels.Where(m => !string.Equals(m, defaultModel, StringComparison.OrdinalIgnoreCase))];
        }
    }

    private static async Task<string[]> TryGetModelsFromProvider(ProviderInfo provider, CancellationToken ct)
    {
        try
        {
            string modelsPath = provider.Capabilities.ModelsPath;

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
