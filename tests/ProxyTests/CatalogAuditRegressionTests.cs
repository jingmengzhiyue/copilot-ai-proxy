using System.Net;
using System.Text.Json;

namespace ProxyTests;

[Collection("Proxy")]
public sealed class CatalogAuditRegressionTests : IDisposable
{
    private readonly Dictionary<string, string?> _environment = new();
    public CatalogAuditRegressionTests()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string name = (string)entry.Key;
            if (name.StartsWith("PROVIDER_") || name.StartsWith("DEEPSEEK_"))
            {
                _environment[name] = (string?)entry.Value;
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }
    public void Dispose()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string name = (string)entry.Key;
            if (name.StartsWith("PROVIDER_") || name.StartsWith("DEEPSEEK_")) Environment.SetEnvironmentVariable(name, null);
        }
        foreach (var entry in _environment) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
    }
    private static ProviderRegistry Registry(HttpMessageHandler handler)
    {
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", "test");
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_BASE_URL", "http://deepseek.test");
        return new(new ProviderHttpClientFactory(handler));
    }
    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block;
        public Func<HttpRequestMessage, string> Body = _ => "{\"data\":[]}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(Body(request)) };
        }
    }
    [Fact]
    public async Task EmptyDiscovery_IsThrottledAndConcurrentRequestsShareRefresh()
    {
        using DiscoveryHandler handler = new() { Block = true };
        ModelCatalogService catalog = new(Registry(handler), new());
        Task first = catalog.RefreshAvailableModelsIfNeeded(default);
        await handler.Entered.Task;
        Task[] others = Enumerable.Range(0, 12).Select(_ => catalog.RefreshAvailableModelsIfNeeded(default)).ToArray();
        handler.Release.SetResult();
        await Task.WhenAll(others.Append(first));
        await catalog.RefreshAvailableModelsIfNeeded(default);
        Assert.Equal(1, handler.Calls);
        Assert.NotEqual(DateTime.MinValue, catalog.ModelsLastAttemptUtc);
        Assert.Equal(DateTime.MinValue, catalog.ModelsLastRefreshUtc);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not-json")]
    public async Task MalformedDiscovery_IsThrottledWithoutPublishingSuccess(string body)
    {
        using DiscoveryHandler handler = new() { Body = _ => body };
        ModelCatalogService catalog = new(Registry(handler), new());
        await catalog.RefreshAvailableModelsIfNeeded(default);
        await catalog.RefreshAvailableModelsIfNeeded(default);
        Assert.Equal(1, handler.Calls);
        Assert.NotEqual(DateTime.MinValue, catalog.ModelsLastAttemptUtc);
        Assert.Equal(DateTime.MinValue, catalog.ModelsLastRefreshUtc);
    }
    [Fact]
    public async Task CanceledRefresh_DoesNotCacheCancellationAndCanRetry()
    {
        using DiscoveryHandler handler = new() { Block = true };
        ModelCatalogService catalog = new(Registry(handler), new());
        using CancellationTokenSource cancel = new();
        Task first = catalog.RefreshAvailableModelsIfNeeded(cancel.Token);
        await handler.Entered.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(DateTime.MinValue, catalog.ModelsLastRefreshUtc);
        handler.Release.SetResult();
        await catalog.RefreshAvailableModelsIfNeeded(default);
        Assert.Equal(2, handler.Calls);
    }
    [Fact]
    public void ExactDisabledEntry_VetoesBroadEnabledRule()
    {
        ModelSelectionStore store = new();
        store.ProviderModelSelections["deepseek"] = [new("model", 1, true, new()), new("model-private", 2, false, new())];
        Assert.Null(store.FindModelSelectionEntry("model-private", "deepseek"));
        Assert.NotNull(store.FindModelSelectionEntry("model-public", "deepseek"));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Profile_UsesExplicitOwnerAndConsistentCapabilities(bool tools, bool vision)
    {
        using DiscoveryHandler handler = new();
        ProviderRegistry registry = Registry(handler);
        ModelSelectionStore store = new();
        store.ProviderModelSelections["deepseek"] = [new("model", 1, true, new(ContextLength: 111, SupportsTools: tools, SupportsVision: vision))];
        store.ProviderModelSelections["openai"] = [new("model-long", 1, true, new(ContextLength: 999))];
        ModelCatalogService catalog = new(registry, store);
        var profile = catalog.GetModelProfile("model-long", "deepseek");
        Assert.Equal(111, profile.ContextLength);
        Assert.Equal(tools, profile.Capabilities.Contains("tools"));
        Assert.Equal(vision, profile.Capabilities.Contains("vision"));
        var show = new OllamaResponseBuilder(catalog).BuildOllamaShowResponse("model-long", "deepseek");
        Assert.Equal(111, show["context_length"]);
    }
    [Fact]
    public void StaticListing_ContainsOnlyActiveEnabledConfiguredModels()
    {
        using DiscoveryHandler handler = new();
        ProviderRegistry registry = Registry(handler);
        ModelSelectionStore store = new();
        store.ProviderModelSelections["deepseek"] = [new("enabled", 1, true, new()), new("disabled", 2, false, new())];
        store.ProviderModelSelections["openai"] = [new("inactive", 1, true, new())];
        var models = new ModelCatalogService(registry, store).GetConfiguredModels().ToArray();
        Assert.Contains(("deepseek", "enabled@deepseek"), models);
        Assert.DoesNotContain(models, x => x.Model.Contains("disabled") || x.Model.Contains("inactive"));
    }
    [Theory]
    [InlineData("https://google.test/v1beta/openai", "https://google.test")]
    [InlineData("https://google.test/v1beta", "https://google.test")]
    [InlineData("https://google.test/custom", "https://google.test/custom")]
    public void GoogleBase_RemovesFullApiPrefix(string configured, string expected)
    {
        Environment.SetEnvironmentVariable("PROVIDER_GOOGLE_API_KEY", "test");
        Environment.SetEnvironmentVariable("PROVIDER_GOOGLE_BASE_URL", configured);
        using DiscoveryHandler handler = new();
        var registry = new ProviderRegistry(new ProviderHttpClientFactory(handler));
        Assert.Equal(expected, registry.FindConfiguredProvider("google")!.Value.BaseUrl);
    }
    [Fact]
    public async Task LocalAndCloudOllama_HaveDistinctStrictRoutesAndCandidates()
    {
        Environment.SetEnvironmentVariable("PROVIDER_OLLAMACLOUD_API_KEY", "test");
        Environment.SetEnvironmentVariable("PROVIDER_OLLAMACLOUD_BASE_URL", "http://cloud.test");
        Environment.SetEnvironmentVariable("PROVIDER_OLLAMA_BASE_URL", "http://localhost:11434");
        using DiscoveryHandler handler = new() { Body = _ => "{\"models\":[{\"name\":\"shared\"}]}" };
        ProviderRegistry registry = new(new ProviderHttpClientFactory(handler));
        ModelSelectionStore store = new();
        store.ProviderModelSelections["ollama"] = [new("shared", 1, true, new())];
        ModelCatalogService catalog = new(registry, store);
        await catalog.RefreshAvailableModels(default);
        var local = catalog.ResolveRoute("shared@ollama-local");
        Assert.Equal("http://localhost:11434", Assert.Single(local.Candidates).Provider.BaseUrl);
        Assert.Equal("http://cloud.test", Assert.Single(catalog.ResolveRoute("shared@ollama").Candidates).Provider.BaseUrl);
        Assert.Equal(2, catalog.ResolveRoute("shared").Candidates.Count);
    }
    [Fact]
    public async Task ConcurrentPublication_RouteAndCandidateUseSameVersion()
    {
        using DiscoveryHandler handler = new();
        ProviderRegistry registry = Registry(handler);
        ModelCatalogService catalog = new(registry, new());
        ProviderInfo provider = registry.Providers[0];
        void Publish(string version) => registry.UpdateModelMappings(
            new(StringComparer.OrdinalIgnoreCase) { ["alias"] = provider },
            new(StringComparer.OrdinalIgnoreCase) { ["alias"] = version });
        Publish("v0");
        using CancellationTokenSource stop = new();
        Task writer = Task.Run(() =>
        {
            int version = 0;
            while (!stop.IsCancellationRequested) Publish("v" + version++);
        });
        try
        {
            for (int i = 0; i < 20000; i++)
            {
                ResolvedRoute route = catalog.ResolveRoute("alias");
                Assert.Equal(route.Model, Assert.Single(route.Candidates).UpstreamModel);
            }
        }
        finally { stop.Cancel(); await writer; }
    }
    [Fact]
    public void InvalidConfiguration_IsRejectedAndFirstDirectoryWins()
    {
        string root = Path.Combine(Path.GetTempPath(), "proxy-config-" + Guid.NewGuid());
        string first = Path.Combine(root, "first"), second = Path.Combine(root, "second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        try
        {
            File.WriteAllText(Path.Combine(first, "a.json"), "{\"provider\":\"test\",\"models\":[{\"match\":\"model\",\"priority\":1}]}");
            File.WriteAllText(Path.Combine(second, "a.json"), "{\"provider\":\"test\",\"models\":[{\"match\":\"model\",\"priority\":9}]}");
            File.WriteAllText(Path.Combine(first, "invalid.json"), "{\"provider\":\"bad\",\"models\":[{\"match\":\"model\",\"enabled\":\"false\"}]}");
            var result = ModelSelectionStore.LoadProviderModelSelections([first, second]);
            Assert.Equal(1, Assert.Single(result["test"]).Priority);
            Assert.False(result.ContainsKey("bad"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
