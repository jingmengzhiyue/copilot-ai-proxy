using System.Text.Json;

internal sealed class ModelSelectionStore
{
    private readonly Dictionary<string, ModelSelectionEntry[]> _providerModelSelections;

    public ModelSelectionStore(ILogger<ModelSelectionStore>? logger = null)
    {
        _providerModelSelections = LoadProviderModelSelections(logger: logger);
    }

    private static string SelectionProvider(string providerName) =>
        providerName.Equals("ollama-local", StringComparison.OrdinalIgnoreCase) ? "ollama" : providerName;

    internal Dictionary<string, ModelSelectionEntry[]> ProviderModelSelections => _providerModelSelections;

    internal ModelExecutionConfig GetExecutionConfigForModel(string model, IReadOnlyDictionary<string, ProviderInfo> modelToProvider)
    {
        // The resolved provider owns execution settings. Only ownerless lookups
        // search across configurations for the most specific compatible entry.
        ModelSelectionEntry best = default;
        bool hasBest = false;

        if (modelToProvider.TryGetValue(model, out ProviderInfo provider))
        {
            return FindModelSelectionEntry(model, provider.Name)?.Execution ?? new ModelExecutionConfig();
        }

        foreach (KeyValuePair<string, ModelSelectionEntry[]> kv in _providerModelSelections)
        {
            ModelSelectionEntry? entry = FindModelSelectionEntry(model, kv.Key);
            if (!entry.HasValue)
            {
                continue;
            }

            if (!hasBest || entry.Value.Match.Length > best.Match.Length)
            {
                best = entry.Value;

                hasBest = true;
            }
        }

        return hasBest ? best.Execution : new ModelExecutionConfig();
    }

    internal int GetPreferredModelPriority(string model, string providerName)
    {
        ModelSelectionEntry? entry = FindModelSelectionEntry(model, providerName);
        return entry?.Priority ?? int.MaxValue;
    }

    internal bool IsPreferredModel(string model, string providerName) =>
        FindModelSelectionEntry(model, providerName) != null;

    internal ModelSelectionEntry[] GetProviderModelSelections(string providerName)
    {
        if (_providerModelSelections.TryGetValue(SelectionProvider(providerName), out ModelSelectionEntry[]? configured) && configured.Length > 0)
        {
            return configured;
        }

        return GetDefaultPreferredModelSelections();
    }

    internal ModelSelectionEntry? FindModelSelectionEntry(string model, string providerName)
    {
        string m = model.ToLowerInvariant();

        if (IsExcludedFromSelection(m))
        {
            return null;
        }

        ModelSelectionEntry[] entries = GetProviderModelSelections(providerName);
        // An explicit disabled ID must veto broader enabled substring rules.
        if (entries.Any(entry => !entry.Enabled && m.Equals(entry.Match, StringComparison.OrdinalIgnoreCase)))
            return null;
        ModelSelectionEntry? fallback = null;
        foreach (ModelSelectionEntry entry in entries.OrderBy(x => x.Priority))
        {
            if (!entry.Enabled)
            {
                continue;
            }

            // Prefer an exact ID so a base model cannot shadow a longer variant such as "-highspeed".
            if (m.Equals(entry.Match, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }

            if (!fallback.HasValue && m.Contains(entry.Match, StringComparison.OrdinalIgnoreCase))
            {
                fallback = entry;
            }
        }

        return fallback;
    }

    /// <summary>
    /// Exact-match-only variant of <see cref="FindModelSelectionEntry"/> used for
    /// routing decisions. Two differences from the discovery-time matcher:
    /// 1. No substring fallback: "k3" must never claim "k3-256k" and vice versa.
    /// 2. Only real configured entries qualify — the synthetic defaults returned by
    ///    <see cref="GetProviderModelSelections"/> for providers without a config
    ///    file must not make a provider claim models it never declared.
    /// </summary>
    internal ModelSelectionEntry? FindExactModelSelectionEntry(string model, string providerName)
    {
        string m = model.ToLowerInvariant();

        if (IsExcludedFromSelection(m))
        {
            return null;
        }

        if (!_providerModelSelections.TryGetValue(SelectionProvider(providerName), out ModelSelectionEntry[]? entries))
        {
            return null;
        }

        foreach (ModelSelectionEntry entry in entries.OrderBy(x => x.Priority))
        {
            if (entry.Enabled && m.Equals(entry.Match, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Last-slash-segment variant of <see cref="FindExactModelSelectionEntry"/>:
    /// "muse-spark-1.1" matches the configured entry "meta/muse-spark-1.1". Used to
    /// round-trip /api/tags display names, which strip the vendor prefix from
    /// slash-qualified matches. Exact matches always win elsewhere first.
    /// </summary>
    internal ModelSelectionEntry? FindSuffixModelSelectionEntry(string model, string providerName)
    {
        // A supplied vendor is part of the upstream identity, never discard it.
        if (model.Contains('/')) return null;
        string m = model.ToLowerInvariant();

        if (IsExcludedFromSelection(m))
        {
            return null;
        }

        if (!_providerModelSelections.TryGetValue(SelectionProvider(providerName), out ModelSelectionEntry[]? entries))
        {
            return null;
        }

        int slash = m.LastIndexOf('/');
        string wanted = slash >= 0 && slash < m.Length - 1 ? m[(slash + 1)..] : m;

        foreach (ModelSelectionEntry entry in entries.OrderBy(x => x.Priority))
        {
            if (!entry.Enabled)
            {
                continue;
            }

            string matchLower = entry.Match.ToLowerInvariant();
            int matchSlash = matchLower.LastIndexOf('/');
            string matchSegment = matchSlash >= 0 && matchSlash < matchLower.Length - 1
                ? matchLower[(matchSlash + 1)..]
                : matchLower;

            if (matchSegment.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Display-name counterpart of <see cref="FindExactModelSelectionEntry"/>: matches
    /// a configured entry by its exact <c>display_name</c> (e.g. "Qwen3.7 Max"), so the
    /// custom display names emitted by /api/tags round-trip back to their canonical
    /// model/provider. Returns the entry (its <c>Match</c> is the canonical model id).
    /// </summary>
    internal ModelSelectionEntry? FindExactDisplayNameEntry(string displayName, string providerName)
    {
        string m = displayName.Trim();

        if (IsExcludedFromSelection(m.ToLowerInvariant()))
        {
            return null;
        }

        if (!_providerModelSelections.TryGetValue(SelectionProvider(providerName), out ModelSelectionEntry[]? entries))
        {
            return null;
        }

        foreach (ModelSelectionEntry entry in entries.OrderBy(x => x.Priority))
        {
            if (entry.Enabled
                && !string.IsNullOrWhiteSpace(entry.DisplayName)
                && m.Equals(entry.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Model categories that are never selectable for chat routing (guardrails,
    /// embeddings, rerankers, etc.).
    /// </summary>
    private static bool IsExcludedFromSelection(string lowercaseModel) =>
        lowercaseModel.Contains("guard") || lowercaseModel.Contains("safety") || lowercaseModel.Contains("embed")
        || lowercaseModel.Contains("retriever") || lowercaseModel.Contains("reranker") || lowercaseModel.Contains("reward")
        || lowercaseModel.Contains("parse") || lowercaseModel.Contains("detector") || lowercaseModel.Contains("clip")
        || lowercaseModel.Contains("riva-translate");

    internal static Dictionary<string, ModelSelectionEntry[]> LoadProviderModelSelections(IEnumerable<string>? directories = null, ILogger? logger = null)
    {
        Dictionary<string, ModelSelectionEntry[]> selections = new(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> candidateDirs = directories ?? new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "config", "model-selection"),
            Path.Combine(AppContext.BaseDirectory, "config", "model-selection")
        };

        foreach (string dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                    JsonElement root = doc.RootElement;
                    ValidateSelectionFile(root);

                    if (!root.TryGetProperty("provider", out JsonElement provE) || provE.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string provider = provE.GetString()!.Trim().ToLowerInvariant();

                    if (!root.TryGetProperty("models", out JsonElement modelsE) || modelsE.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    List<ModelSelectionEntry> entries = [];
                    int idx = 0;
                    foreach (JsonElement item in modelsE.EnumerateArray())
                    {
                        idx++;
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string? match = item.GetString();
                            if (!string.IsNullOrWhiteSpace(match))
                            {
                                entries.Add(new ModelSelectionEntry(match!, idx, true, new ModelExecutionConfig()));
                            }

                            continue;
                        }

                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        string? matchValue = null;
                        if (item.TryGetProperty("match", out JsonElement matchE) && matchE.ValueKind == JsonValueKind.String)
                        {
                            matchValue = matchE.GetString();
                        }
                        else if (item.TryGetProperty("model", out JsonElement modelE) && modelE.ValueKind == JsonValueKind.String)
                        {
                            matchValue = modelE.GetString();
                        }
                        else if (item.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                        {
                            matchValue = idE.GetString();
                        }

                        if (string.IsNullOrWhiteSpace(matchValue))
                        {
                            continue;
                        }

                        int priority = item.TryGetProperty("priority", out JsonElement priE) && priE.ValueKind == JsonValueKind.Number
                            ? priE.GetInt32() : idx;
                        bool enabled = !item.TryGetProperty("enabled", out JsonElement enE) || enE.ValueKind != JsonValueKind.False;
                        string? displayName = item.TryGetProperty("display_name", out JsonElement displayE) && displayE.ValueKind == JsonValueKind.String
                            ? displayE.GetString()
                            : null;

                        ModelExecutionConfig exec = new();
                        if (item.TryGetProperty("execution", out JsonElement execE) && execE.ValueKind == JsonValueKind.Object)
                        {
                            exec = new ModelExecutionConfig(
                                ContextLength: execE.TryGetProperty("context_length", out JsonElement ctxE) && ctxE.ValueKind == JsonValueKind.Number ? ctxE.GetInt32() : null,
                                MaxOutputTokens: execE.TryGetProperty("max_output_tokens", out JsonElement outE) && outE.ValueKind == JsonValueKind.Number ? outE.GetInt32() : null,
                                SupportsTools: execE.TryGetProperty("supports_tools", out JsonElement stE) && stE.ValueKind is JsonValueKind.True or JsonValueKind.False ? stE.GetBoolean() : null,
                                SupportsVision: execE.TryGetProperty("supports_vision", out JsonElement svE) && svE.ValueKind is JsonValueKind.True or JsonValueKind.False ? svE.GetBoolean() : null,
                                Family: execE.TryGetProperty("family", out JsonElement famE) && famE.ValueKind == JsonValueKind.String ? famE.GetString() : null,
                                Temperature: execE.TryGetProperty("temperature", out JsonElement tempE) && tempE.ValueKind == JsonValueKind.Number ? tempE.GetDouble() : null,
                                TopP: execE.TryGetProperty("top_p", out JsonElement topPE) && topPE.ValueKind == JsonValueKind.Number ? topPE.GetDouble() : null,
                                MaxTokensPreferred: execE.TryGetProperty("max_tokens", out JsonElement maxTokE) && maxTokE.ValueKind == JsonValueKind.Number ? maxTokE.GetInt32() : null,
                                MaxCompletionTokensPreferred: execE.TryGetProperty("max_completion_tokens", out JsonElement maxCompletionTokE) && maxCompletionTokE.ValueKind == JsonValueKind.Number ? maxCompletionTokE.GetInt32() : null,
                                ReasoningEffort: execE.TryGetProperty("reasoning_effort", out JsonElement reE) && reE.ValueKind == JsonValueKind.String ? reE.GetString() : null,
                                TimeoutSeconds: execE.TryGetProperty("timeout_seconds", out JsonElement timeoutE) && timeoutE.ValueKind == JsonValueKind.Number ? timeoutE.GetInt32() : null,
                                OverrideClientParams: execE.TryGetProperty("override_client_params", out JsonElement ovE) && ovE.ValueKind is JsonValueKind.True or JsonValueKind.False && ovE.GetBoolean(),
                                SupportsReasoning: execE.TryGetProperty("supports_reasoning", out JsonElement srE) && srE.ValueKind is JsonValueKind.True or JsonValueKind.False ? srE.GetBoolean() : null
                            );
                        }

                        entries.Add(new ModelSelectionEntry(matchValue!, priority, enabled, exec, displayName));
                    }

                    if (entries.Count > 0)
                    {
                        // Merge with existing entries for the same provider (multiple files may
                        // declare the same provider, e.g. ollama.json + ollamacloud.json both use
                        // "provider": "ollama"). Previously loaded entries are preserved.
                        if (selections.TryGetValue(provider, out ModelSelectionEntry[]? existing))
                        {
                            // Start with existing entries, then add new ones (no duplicate match strings).
                            HashSet<string> existingMatches = new(existing.Select(e => e.Match), StringComparer.OrdinalIgnoreCase);
                            List<ModelSelectionEntry> merged = [..existing];
                            foreach (ModelSelectionEntry entry in entries)
                            {
                                if (existingMatches.Add(entry.Match))
                                {
                                    merged.Add(entry);
                                }
                            }
                            entries = merged;
                        }

                        // Order entries by (priority asc, match length desc). A longer match
                        // substring is more specific than a shorter one, so it wins when two
                        // entries (e.g. "nemotron" vs "nemotron-3-super") both match the
                        // same upstream id. Without this, a generic match would shadow a
                        // specific one whenever the generic file is loaded first.
                        selections[provider] = entries
                            .OrderBy(x => x.Priority)
                            .ThenByDescending(x => x.Match.Length)
                            .ToArray();
                    }
                }
                catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
                {
                    // Report file and field identities without echoing configuration values.
                    logger?.LogWarning("Ignoring invalid model selection file {File}; {Reason}", Path.GetFileName(file),
                        ex is FormatException ? ex.Message : ex.GetType().Name);
                }
            }
        }

        return selections;
    }

    private static void ValidateSelectionFile(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("provider", out JsonElement provider) || provider.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(provider.GetString()))
            throw new FormatException("provider must be a non-empty string");
        if (!root.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            throw new FormatException("models must be an array");
        int index = 0;
        foreach (JsonElement entry in models.EnumerateArray())
        {
            index++;
            if (entry.ValueKind == JsonValueKind.String) continue;
            if (entry.ValueKind != JsonValueKind.Object) throw new FormatException($"models[{index}] must be an object or string");
            if (entry.TryGetProperty("enabled", out JsonElement enabled) && enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException($"models[{index}].enabled must be a boolean");
            if (entry.TryGetProperty("priority", out JsonElement priority)
                && (priority.ValueKind != JsonValueKind.Number || !priority.TryGetInt32(out _)))
                throw new FormatException($"models[{index}].priority must be an integer");
            if (!entry.TryGetProperty("execution", out JsonElement execution)) continue;
            if (execution.ValueKind != JsonValueKind.Object) throw new FormatException($"models[{index}].execution must be an object");
            foreach (JsonProperty field in execution.EnumerateObject())
            {
                bool valid = field.Name switch
                {
                    "context_length" or "max_output_tokens" or "max_tokens" or "max_completion_tokens" or "timeout_seconds"
                        => field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out int value) && value > 0,
                    "temperature" or "top_p" => field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetDouble(out double value) && double.IsFinite(value),
                    "supports_tools" or "supports_vision" or "supports_reasoning" or "override_client_params"
                        => field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "family" or "reasoning_effort" => field.Value.ValueKind == JsonValueKind.String,
                    _ => true
                };
                if (!valid) throw new FormatException($"models[{index}].execution.{field.Name}: invalid type or range");
            }
        }
    }

    private static ModelSelectionEntry[] GetDefaultPreferredModelSelections() =>
    [
        new("deepseek-v4-pro", 1, true, new()),
        new("qwen3-coder-480b-a35b", 2, true, new()),
        new("qwen3.5-397b-a17b", 3, true, new()),
        new("mistral-large-3-675b-instruct-2512", 4, true, new()),
        new("llama-4-maverick-17b-128e-instruct", 5, true, new()),
        new("llama-3.1-nemotron-ultra-253b-v1", 6, true, new()),
        new("nemotron-4-340b-instruct", 7, true, new()),
        new("gpt-oss-120b", 8, true, new()),
        new("kimi-k2.6", 9, true, new()),
        new("llama-3.3-nemotron-super-49b-v1.5", 10, true, new())
    ];
}
