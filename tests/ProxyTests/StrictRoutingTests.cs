using ProxyTests.FakeProviders;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProxyTests;

/// <summary>
/// Tests for the central route resolver (<see cref="ModelCatalogService.ResolveRoute"/>),
/// which powers both /v1/chat/completions and /api/chat.
///
/// Semantics under test:
///   - "model@provider" is STRICT: exactly one candidate, never a fallback to
///     another provider or to the default model. Unknown provider → ProviderNotFound,
///     unknown model at a known provider → ModelNotFound.
///   - Strict routing works even when provider model discovery failed, via the
///     config-declared exact matches in config/model-selection/*.json.
///   - Bare models auto-route (catalog → config exact → default model, flagged).
///   - Prefix look-alikes ("k3" vs "k3-256k", "kimi" vs "kimicode") never cross.
/// </summary>
[Collection("Proxy")]
public class StrictRoutingTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_ActualTagsRoundTripPreservesEveryProviderAndUpstream(bool discovered)
    {
        string[] providers = ["deepseek", "kimicode", "kimi", "qwen", "zhipu", "hunyuan", "openrouter", "customopenai"];
        var store = new ModelSelectionStore();
        var (_, registry, _) = BuildCatalog(providers.ToDictionary(p => p,
            p => discovered ? store.ProviderModelSelections[p].Where(e => e.Enabled)
                .SelectMany(e => p == "qwen" ? new[] { e.Match + "-snapshot", e.Match } : new[] { e.Match }).ToArray() : []));
        // Exercise delimiters in real upstream IDs, not just today's config.
        store.ProviderModelSelections["customopenai"] = [.. store.ProviderModelSelections["customopenai"],
            new("vendor/model:batch", 90, true, new()),
            new("model@revision", 91, true, new()),
            new("model with spaces - v1.2", 92, true, new()),
            new("real:latest", 93, true, new())];
        var catalog = new ModelCatalogService(registry, store);
        if (discovered) await catalog.RefreshAvailableModels(CancellationToken.None);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton<ReasoningCacheService>();
        builder.Services.AddSingleton<RequestTransformer>();
        builder.Services.AddSingleton<ChatStreamingService>();
        builder.Services.AddSingleton<OllamaResponseBuilder>();
        await using var app = builder.Build();
        app.MapOllamaEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var tags = JsonDocument.Parse(await client.GetStringAsync("/api/tags"));
        int count = 0;
        foreach (var entry in tags.RootElement.GetProperty("models").EnumerateArray())
        {
            string upstream = entry.GetProperty("model_info").GetProperty("general.basename").GetString()!;
            string qualified = entry.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()!)
                .First(a => a.StartsWith(upstream + "@", StringComparison.Ordinal));
            string expectedProvider = qualified[(upstream.Length + 1)..^7];
            foreach (string field in new[] { "name", "model" })
            {
                string alias = entry.GetProperty(field).GetString()!;
                var route = catalog.ResolveRoute(alias);
                var candidate = Assert.Single(route.Candidates);
                Assert.False(route.FallbackUsed);
                Assert.Equal(expectedProvider, candidate.Provider.Name);
                Assert.Equal(upstream, candidate.UpstreamModel);
                count++;
            }
        }
        Assert.True(count > 40);
    }

    [Theory]
    [InlineData("k3:batch@kimicode")]
    [InlineData("k3:free@kimicode")]
    [InlineData("k3@kimicode:batch")]
    [InlineData("wrong-vendor/k3@kimicode")]
    public void Audit_UnknownVariantMustNotBecomeAnotherModel(string requested)
    {
        var (catalog, _, _) = BuildCatalog(new Dictionary<string, string[]> { ["kimicode"] = [] });
        ResolvedRoute route = catalog.ResolveRoute(requested);
        Assert.NotEqual(RouteErrorKind.None, route.Error);
        Assert.Empty(route.Candidates);
        Assert.False(route.FallbackUsed);
    }

    [Fact]
    public void Audit_ConfigLiteralTagBeatsDiscoveredBaseModel()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]> { ["kimicode"] = [] });
        ProviderInfo provider = registry.FindConfiguredProvider("kimicode")!.Value;
        registry.UpdateModelMappings(new(StringComparer.OrdinalIgnoreCase) { ["k3"] = provider },
            new(StringComparer.OrdinalIgnoreCase) { ["k3"] = "k3" });
        // Use the same mutable configuration store as the catalog to represent a
        // legitimate upstream ID that includes ':latest'.
        var store = new ModelSelectionStore();
        store.ProviderModelSelections["kimicode"] = [new("k3:latest", 1, true, new())];
        catalog = new ModelCatalogService(registry, store);
        foreach (string requested in new[] { "k3:latest", "k3:latest@kimicode" })
            Assert.Equal("k3:latest", Assert.Single(catalog.ResolveRoute(requested).Candidates).UpstreamModel);
    }

    private readonly Dictionary<string, string?> _envSnapshot;

    public StrictRoutingTests()
    {
        string[] keys =
        [
            "PROVIDER_DEEPSEEK_API_KEY", "PROVIDER_DEEPSEEK_BASE_URL",
            "PROVIDER_OPENAI_API_KEY", "PROVIDER_OPENAI_BASE_URL",
            "PROVIDER_NVIDIA_API_KEY", "PROVIDER_NVIDIA_BASE_URL",
            "PROVIDER_OPENROUTER_API_KEY", "PROVIDER_OPENROUTER_BASE_URL",
            "PROVIDER_GROQ_API_KEY", "PROVIDER_GROQ_BASE_URL",
            "PROVIDER_OLLAMACLOUD_API_KEY", "PROVIDER_OLLAMA_API_KEY", "PROVIDER_OLLAMA_BASE_URL",
            "PROVIDER_GOOGLE_API_KEY", "PROVIDER_GOOGLE_BASE_URL",
            "PROVIDER_MOONSHOT_API_KEY", "PROVIDER_MOONSHOT_BASE_URL",
            "PROVIDER_KIMI_API_KEY", "PROVIDER_KIMI_BASE_URL",
            "PROVIDER_KIMICODE_API_KEY", "PROVIDER_KIMICODE_BASE_URL",
            "PROVIDER_QWEN_API_KEY", "PROVIDER_QWEN_BASE_URL",
            "PROVIDER_HUNYUAN_API_KEY", "PROVIDER_HUNYUAN_BASE_URL",
            "PROVIDER_CEREBRAS_API_KEY", "PROVIDER_CEREBRAS_BASE_URL",
            "PROVIDER_ZENMUX_API_KEY", "PROVIDER_ZENMUX_BASE_URL",
            "DEEPSEEK_API_KEY", "DEEPSEEK_BASE_URL",
            "DEEPSEEK_MODEL"
        ];
        _envSnapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string k in keys)
        {
            _envSnapshot[k] = Environment.GetEnvironmentVariable(k);
            Environment.SetEnvironmentVariable(k, null);
        }
    }

    public void Dispose()
    {
        foreach (KeyValuePair<string, string?> kv in _envSnapshot)
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);
    }

    private const string AnyKey = "test-key";

    // ── Explicit provider: discovered catalog ─────────────────────────────

    [Fact]
    public async Task StrictRoute_KimiForCodingAtKimicode_Discovered_RoutesToKimicode()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["deepseek"] = ["deepseek-v4-pro"],
            ["kimicode"] = ["k3", "k3-256k", "kimi-for-coding", "kimi-for-coding-highspeed"]
        });
        await catalog.RefreshAvailableModels(CancellationToken.None);

        ResolvedRoute route = catalog.ResolveRoute("kimi-for-coding@kimicode");

        Assert.Equal(RouteErrorKind.None, route.Error);
        Assert.False(route.FallbackUsed);
        Assert.Equal("kimi-for-coding", route.Model);
        Assert.Equal("kimicode", route.ProviderHint);
        var candidate = Assert.Single(route.Candidates);
        Assert.Equal("kimicode", candidate.Provider.Name);
        Assert.Equal("kimi-for-coding", candidate.UpstreamModel);
        // The upstream id must never carry the "@provider" router suffix.
        Assert.DoesNotContain('@', candidate.UpstreamModel);
    }

    [Theory]
    [InlineData("k3@kimicode", "k3")]
    [InlineData("k3-256k@kimicode", "k3-256k")]
    public async Task StrictRoute_K3Variants_ExactMatchNotPrefix(string requested, string expectedUpstream)
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["kimicode"] = ["k3", "k3-256k"]
        });
        await catalog.RefreshAvailableModels(CancellationToken.None);

        ResolvedRoute route = catalog.ResolveRoute(requested);

        Assert.Equal(RouteErrorKind.None, route.Error);
        var candidate = Assert.Single(route.Candidates);
        Assert.Equal("kimicode", candidate.Provider.Name);
        Assert.Equal(expectedUpstream, candidate.UpstreamModel);
    }

    // ── Explicit provider: discovery failed, config-declared match ────────

    [Fact]
    public void StrictRoute_KimiForCodingAtKimicode_DiscoveryFailed_StillRoutesViaConfig()
    {
        // kimicode discovery returns nothing (e.g. /v1/models unreachable): the
        // config-declared match in kimicode.json must still make the route work.
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["deepseek"] = ["deepseek-v4-pro"],
            ["kimicode"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("kimi-for-coding@kimicode");

        Assert.Equal(RouteErrorKind.None, route.Error);
        Assert.False(route.FallbackUsed);
        var candidate = Assert.Single(route.Candidates);
        Assert.Equal("kimicode", candidate.Provider.Name);
        Assert.Equal("kimi-for-coding", candidate.UpstreamModel);
        Assert.Equal("https://api.kimi.com/coding", candidate.Provider.Capabilities.DefaultBaseUrl);
    }

    [Fact]
    public void StrictRoute_Qwen37MaxAtQwen_RoutesViaConfig()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["qwen"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("qwen3.7-max@qwen");

        Assert.Equal(RouteErrorKind.None, route.Error);
        var candidate = Assert.Single(route.Candidates);
        Assert.Equal("qwen", candidate.Provider.Name);
        Assert.Equal("qwen3.7-max", candidate.UpstreamModel);
    }

    [Fact]
    public void StrictRoute_OpenAiGptSolAtOpenRouter_KeepsSlashInUpstreamId()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["openrouter"] = []
        });

        // The "openai/" prefix is part of the upstream id, NOT a provider hint —
        // the "@openrouter" suffix already pinned the provider.
        ResolvedRoute route = catalog.ResolveRoute("openai/gpt-5.6-sol@openrouter");

        Assert.Equal(RouteErrorKind.None, route.Error);
        var candidate = Assert.Single(route.Candidates);
        Assert.Equal("openrouter", candidate.Provider.Name);
        Assert.Equal("openai/gpt-5.6-sol", candidate.UpstreamModel);
    }

    [Fact]
    public async Task StrictRoute_NvidiaStyleSuffixMatch_WithinHintedProvider()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["nvidia"] = ["qwen/qwen3.5-397b-a17b"]
        });
        await catalog.RefreshAvailableModels(CancellationToken.None);

        ResolvedRoute route = catalog.ResolveRoute("qwen3.5-397b-a17b@nvidia");

        Assert.Equal(RouteErrorKind.None, route.Error);
        var candidate = Assert.Single(route.Candidates);
        Assert.Equal("nvidia", candidate.Provider.Name);
        Assert.Equal("qwen/qwen3.5-397b-a17b", candidate.UpstreamModel);
    }

    // ── Explicit provider: error cases (no fallback allowed) ──────────────

    [Fact]
    public void StrictRoute_UnknownProvider_ReturnsProviderNotFound()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["deepseek"] = ["deepseek-v4-pro"],
            ["kimicode"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("kimi-for-coding@does-not-exist");

        Assert.Equal(RouteErrorKind.ProviderNotFound, route.Error);
        Assert.Empty(route.Candidates);
        Assert.False(route.FallbackUsed);
        Assert.Contains("does-not-exist", route.ErrorMessage);
    }

    [Fact]
    public void StrictRoute_UnknownModelAtKnownProvider_ReturnsModelNotFound()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["deepseek"] = ["deepseek-v4-pro"],
            ["kimicode"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("does-not-exist@kimicode");

        Assert.Equal(RouteErrorKind.ModelNotFound, route.Error);
        Assert.Empty(route.Candidates);
        Assert.False(route.FallbackUsed);
        Assert.Contains("does-not-exist", route.ErrorMessage);
        Assert.Contains("kimicode", route.ErrorMessage);
    }

    [Fact]
    public void StrictRoute_KimiHint_DoesNotResolveKimicodeProvider()
    {
        // "kimi" and "kimicode" are different providers with different keys and
        // base URLs; prefix similarity must never cross them.
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["kimicode"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("kimi-for-coding@kimi");

        Assert.Equal(RouteErrorKind.ProviderNotFound, route.Error);
        Assert.Empty(route.Candidates);
    }

    // ── Bare model: automatic routing ─────────────────────────────────────

    [Fact]
    public async Task AutoRoute_BareKimiForCoding_Discovered_RoutesToKimicode()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["deepseek"] = ["deepseek-v4-pro"],
            ["kimicode"] = ["kimi-for-coding"]
        });
        await catalog.RefreshAvailableModels(CancellationToken.None);

        ResolvedRoute route = catalog.ResolveRoute("kimi-for-coding");

        Assert.Equal(RouteErrorKind.None, route.Error);
        Assert.False(route.FallbackUsed);
        Assert.Null(route.ProviderHint);
        Assert.Equal("kimicode", route.Candidates[0].Provider.Name);
        Assert.Equal("kimi-for-coding", route.Candidates[0].UpstreamModel);
    }

    [Fact]
    public void AutoRoute_BareKimiForCoding_DiscoveryFailed_RoutesViaConfig()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["deepseek"] = [],
            ["kimicode"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("kimi-for-coding");

        Assert.Equal(RouteErrorKind.None, route.Error);
        Assert.False(route.FallbackUsed);
        Assert.Equal("kimicode", route.Candidates[0].Provider.Name);
        Assert.Equal("kimi-for-coding", route.Candidates[0].UpstreamModel);
    }

    [Fact]
    public void AutoRoute_UnknownBareModel_FallsBackToDefaultModel_Flagged()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["deepseek"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("totally-unknown-model");

        Assert.Equal(RouteErrorKind.None, route.Error);
        Assert.True(route.FallbackUsed);
        Assert.Equal(registry.DefaultModel, route.Model);
        Assert.NotEmpty(route.Candidates);
    }

    [Fact]
    public void AutoRoute_EmptyModel_UsesDefaultWithoutFallbackFlag()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["deepseek"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute(null);

        Assert.Equal(RouteErrorKind.None, route.Error);
        Assert.False(route.FallbackUsed);
        Assert.Equal(registry.DefaultModel, route.Model);
    }

    // ── Normalization ─────────────────────────────────────────────────────

    [Fact]
    public void StrictRoute_CaseInsensitive_AndTagSuffixStripped()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["kimicode"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("Kimi-For-Coding:latest@KIMICODE");

        Assert.Equal(RouteErrorKind.None, route.Error);
        var candidate = Assert.Single(route.Candidates);
        Assert.Equal("kimicode", candidate.Provider.Name);
        // Canonical casing comes from the config-declared match.
        Assert.Equal("kimi-for-coding", candidate.UpstreamModel);
    }

    // ── Visual Studio / Ollama display aliases ("PROVIDER - model:latest") ──
    // /api/tags emits name="{PROVIDER} - {model}:latest"; Visual Studio sends that
    // name back verbatim. It must round-trip to the exact provider, strictly.

    [Theory]
    [InlineData("KIMICODE - k3:latest", "kimicode", "k3")]
    [InlineData("KIMICODE - k3-256k:latest", "kimicode", "k3-256k")]
    [InlineData("KIMICODE - kimi-for-coding:latest", "kimicode", "kimi-for-coding")]
    [InlineData("KIMICODE - kimi-for-coding-highspeed:latest", "kimicode", "kimi-for-coding-highspeed")]
    [InlineData("QWEN - qwen3.7-max:latest", "qwen", "qwen3.7-max")]
    [InlineData("ZHIPU - glm-5.3:latest", "zhipu", "glm-5.3")]
    [InlineData("HUNYUAN - hy3:latest", "hunyuan", "hy3")]
    [InlineData("DEEPSEEK - deepseek-v4-pro:latest", "deepseek", "deepseek-v4-pro")]
    public void StrictRoute_ProviderDisplayAlias_RoundTrips(string requested, string expectedProvider, string expectedUpstream)
    {
        var (catalog, _, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["deepseek"] = [], ["kimicode"] = [], ["qwen"] = [], ["zhipu"] = [], ["hunyuan"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute(requested);

        Assert.Equal(RouteErrorKind.None, route.Error);
        Assert.False(route.FallbackUsed);
        Assert.Equal(expectedProvider, route.ProviderHint);
        Assert.Equal(expectedUpstream, route.NormalizedModel);
        var candidate = Assert.Single(route.Candidates);
        Assert.Equal(expectedProvider, candidate.Provider.Name);
        Assert.Equal(expectedUpstream, candidate.UpstreamModel);
        Assert.DoesNotContain(":latest", candidate.UpstreamModel);
    }

    [Fact]
    public void StrictRoute_ProviderDisplayAlias_UnknownModel_ReturnsModelNotFound()
    {
        var (catalog, _, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["deepseek"] = [], ["kimicode"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("KIMICODE - does-not-exist:latest");

        Assert.Equal(RouteErrorKind.ModelNotFound, route.Error);
        Assert.Empty(route.Candidates);
        Assert.False(route.FallbackUsed);
        Assert.Equal("kimicode", route.ProviderHint);
    }

    [Fact]
    public void StrictRoute_DisplayAlias_LowercaseProviderPrefix_Works()
    {
        var (catalog, _, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["kimicode"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("kimicode - k3:latest");

        Assert.Equal(RouteErrorKind.None, route.Error);
        Assert.Equal("kimicode", route.ProviderHint);
        Assert.Equal("k3", route.NormalizedModel);
    }

    // ── Tag and display-name variants ─────────────────────────────────────

    [Fact]
    public void StrictRoute_TaggedQualifiedAlias_StripsTagFromHint()
    {
        var (catalog, _, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["kimicode"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("k3@kimicode:latest");

        Assert.Equal(RouteErrorKind.None, route.Error);
        var candidate = Assert.Single(route.Candidates);
        Assert.Equal("kimicode", candidate.Provider.Name);
        Assert.Equal("k3", candidate.UpstreamModel);
    }

    [Fact]
    public void AutoRoute_BareTaggedModel_StripsOllamaTag()
    {
        var (catalog, _, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["deepseek"] = [], ["kimicode"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("k3:latest");

        Assert.Equal(RouteErrorKind.None, route.Error);
        Assert.False(route.FallbackUsed);
        Assert.Equal("k3", route.NormalizedModel);
        Assert.Equal("kimicode", route.Candidates[0].Provider.Name);
        Assert.Equal("k3", route.Candidates[0].UpstreamModel);
    }

    [Fact]
    public void AutoRoute_CustomDisplayName_RoundTripsToCanonicalModel()
    {
        // qwen.json declares display_name "Qwen3.7 Max" for match "qwen3.7-max";
        // /api/tags emits it as both name and model. It must round-trip.
        var (catalog, _, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["qwen"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("Qwen3.7 Max:latest");

        Assert.Equal(RouteErrorKind.None, route.Error);
        Assert.False(route.FallbackUsed);
        Assert.Equal("qwen3.7-max", route.NormalizedModel);
        Assert.Equal("qwen", route.Candidates[0].Provider.Name);
        Assert.Equal("qwen3.7-max", route.Candidates[0].UpstreamModel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictRoute_SlashStrippedDisplayName_FindsConfigSuffixMatch(bool discovered)
    {
        // /api/tags strips the vendor prefix for display: "CUSTOMOPENAI - muse-spark-1.1"
        // is generated from the configured match "meta/muse-spark-1.1".
        var (catalog, _, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["customopenai"] = ["meta/muse-spark-1.1"]
        });

        if (discovered) await catalog.RefreshAvailableModels(CancellationToken.None);

        ResolvedRoute route = catalog.ResolveRoute("CUSTOMOPENAI - muse-spark-1.1:latest");

        Assert.Equal(RouteErrorKind.None, route.Error);
        var candidate = Assert.Single(route.Candidates);
        Assert.Equal("customopenai", candidate.Provider.Name);
        Assert.Equal("meta/muse-spark-1.1", candidate.UpstreamModel);
        Assert.Equal(candidate.UpstreamModel, route.NormalizedModel);
    }

    [Fact]
    public void AutoRoute_UnknownBareModel_KeepsRequestedIdAsNormalizedModel()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["deepseek"] = []
        });

        ResolvedRoute route = catalog.ResolveRoute("totally-unknown-model");

        // NormalizedModel reflects alias normalization (the request itself when
        // nothing resolves); the default model only appears as the effective model.
        Assert.Equal("totally-unknown-model", route.NormalizedModel);
        Assert.Equal(registry.DefaultModel, route.Model);
        Assert.True(route.FallbackUsed);
    }

    // ── Base URL normalization ────────────────────────────────────────────

    [Fact]
    public void BaseUrl_WithRedundantVersionSuffix_IsNormalized()
    {
        // Users paste the full versioned endpoint ("…/v1"); ChatPath already
        // carries "v1/…", so the registry must collapse the duplicate segment,
        // otherwise upstream calls go to "…/v1/v1/chat/completions" and 404.
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["kimicode"] = []
        }, baseUrlSuffix: "/v1");

        ProviderInfo kimicode = Assert.Single(registry.Providers,
            p => p.Name.Equals("kimicode", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("http://kimicode.test", kimicode.BaseUrl);

        // The effective upstream URL must contain exactly one "v1" segment.
        Uri chatUri = new(kimicode.Client.BaseAddress!, kimicode.Capabilities.ChatPath);
        Assert.Equal("http://kimicode.test/v1/chat/completions", chatUri.ToString());
    }

    [Fact]
    public void BaseUrl_WithoutVersionSuffix_StaysUnchanged()
    {
        var (catalog, registry, _) = BuildCatalog(new Dictionary<string, string[]>
        {
            ["kimicode"] = []
        });

        ProviderInfo kimicode = Assert.Single(registry.Providers,
            p => p.Name.Equals("kimicode", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("http://kimicode.test", kimicode.BaseUrl);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static (ModelCatalogService catalog, ProviderRegistry registry, FakeProviderHandler handler)
        BuildCatalog(IDictionary<string, string[]> perProviderModels, string? baseUrlSuffix = null)
    {
        FakeProviderHandler handler = new(perProviderModels);
        ProviderHttpClientFactory factory = new(handler);

        HashSet<string> requested = new(perProviderModels.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string name in requested)
        {
            string prefix = ProviderCapabilitiesRegistry.TryGet(name, out ProviderCapabilities caps)
                ? caps.EnvPrefix
                : name.ToUpperInvariant();
            Environment.SetEnvironmentVariable($"PROVIDER_{prefix}_API_KEY", AnyKey);
            Environment.SetEnvironmentVariable($"PROVIDER_{prefix}_BASE_URL", $"http://{name}.test{baseUrlSuffix}");
        }

        foreach (string provName in ProviderCapabilitiesRegistry.KnownProviders)
        {
            ProviderCapabilities caps = ProviderCapabilitiesRegistry.Get(provName);
            if (!requested.Contains(provName))
                Environment.SetEnvironmentVariable($"PROVIDER_{caps.EnvPrefix}_API_KEY", null);
        }
        Environment.SetEnvironmentVariable("PROVIDER_OLLAMACLOUD_API_KEY", null);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);

        ProviderRegistry registry = new(factory);
        ModelSelectionStore store = new();
        ModelCatalogService catalog = new(registry, store);
        return (catalog, registry, handler);
    }
}
