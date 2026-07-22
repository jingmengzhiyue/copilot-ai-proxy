using System.Text.Json;

namespace ProxyTests;

// Share the "Proxy" collection so ProxyFixture's environment-variable setup
// runs before any of these tests, and the registry constructor can find a
// configured PROVIDER_DEEPSEEK_API_KEY.
[Collection("Proxy")]
public class ProviderRegistryTests
{
    [Fact]
    public void ResolveProvider_WithNullModel_ReturnsDefaultProvider()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ProviderInfo result = registry.ResolveProvider(null);

        
        Assert.Equal("deepseek", result.Name);
    }

    [Fact]
    public void ResolveProvider_WithEmptyModel_ReturnsDefaultProvider()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ProviderInfo result = registry.ResolveProvider("");

        
        Assert.Equal("deepseek", result.Name);
    }

    [Fact]
    public void ResolveModel_WithNullModel_ReturnsDefaultModel()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        string result = registry.ResolveModel(null);

        Assert.Equal("deepseek-v4-pro", result);
    }

    [Fact]
    public void ResolveModel_WithEmptyModel_ReturnsDefaultModel()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        string result = registry.ResolveModel("");

        Assert.Equal("deepseek-v4-pro", result);
    }

    [Fact]
    public void ResolveUpstreamModel_WithNullModel_ReturnsDefaultModel()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        string result = registry.ResolveUpstreamModel(null);

        Assert.Equal("deepseek-v4-pro", result);
    }

    [Fact]
    public void DefaultModel_IsDeepSeekV4Pro()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        Assert.Equal("deepseek-v4-pro", registry.DefaultModel);
    }

    [Fact]
    public void UpdateModelMappings_UpdatesModelToProvider()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        Dictionary<string, ProviderInfo> newMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["custom-model"] = new ProviderInfo("groq", "key", "http://localhost", new System.Net.Http.HttpClient(), ProviderCapabilitiesRegistry.Get("groq"))
        };
        Dictionary<string, string> newUpstream = new(StringComparer.OrdinalIgnoreCase)
        {
            ["custom-model"] = "llama-3.3-70b-versatile"
        };

        registry.UpdateModelMappings(newMap, newUpstream);

        ProviderInfo result = registry.ResolveProvider("custom-model");
        
        Assert.Equal("groq", result.Name);
    }

    [Fact]
    public void ResolveCandidates_WithNullModel_ReturnsDefaultProvider()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        var candidates = registry.ResolveCandidates(null);

        Assert.Single(candidates);
        Assert.Equal("deepseek", candidates[0].Provider.Name);
    }

    [Fact]
    public void Providers_AtLeastOneProviderExists()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        Assert.NotEmpty(registry.Providers);
    }

    [Fact]
    public void DiscoverProviders_CustomOpenAiWithoutBaseUrl_IsSkipped()
    {
        string? oldCustomKey = Environment.GetEnvironmentVariable("PROVIDER_CUSTOMOPENAI_API_KEY");
        string? oldCustomBase = Environment.GetEnvironmentVariable("PROVIDER_CUSTOMOPENAI_BASE_URL");
        string? oldDeepseekKey = Environment.GetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY");
        string? oldLegacyDeepseekKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");

        try
        {
            Environment.SetEnvironmentVariable("PROVIDER_CUSTOMOPENAI_API_KEY", "test-key");
            Environment.SetEnvironmentVariable("PROVIDER_CUSTOMOPENAI_BASE_URL", null);
            Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", null);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);

            ProviderHttpClientFactory factory = new();
            ProviderRegistry registry = new(factory);

            Assert.DoesNotContain(registry.Providers, p => p.Name.Equals("customopenai", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROVIDER_CUSTOMOPENAI_API_KEY", oldCustomKey);
            Environment.SetEnvironmentVariable("PROVIDER_CUSTOMOPENAI_BASE_URL", oldCustomBase);
            Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", oldDeepseekKey);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", oldLegacyDeepseekKey);
        }
    }

    [Fact]
    public void DiscoverProviders_KimiApiKey_RegistersChinaEndpointWithoutMoonshot()
    {
        string? oldKimiKey = Environment.GetEnvironmentVariable("PROVIDER_KIMI_API_KEY");
        string? oldKimiBase = Environment.GetEnvironmentVariable("PROVIDER_KIMI_BASE_URL");
        string? oldMoonshotKey = Environment.GetEnvironmentVariable("PROVIDER_MOONSHOT_API_KEY");

        try
        {
            Environment.SetEnvironmentVariable("PROVIDER_KIMI_API_KEY", "test-key");
            Environment.SetEnvironmentVariable("PROVIDER_KIMI_BASE_URL", null);
            Environment.SetEnvironmentVariable("PROVIDER_MOONSHOT_API_KEY", null);

            ProviderHttpClientFactory factory = new();
            ProviderRegistry registry = new(factory);

            ProviderInfo kimi = Assert.Single(registry.Providers, p => p.Name.Equals("kimi", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("https://api.moonshot.cn", kimi.BaseUrl);
            Assert.DoesNotContain(registry.Providers, p => p.Name.Equals("moonshot", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROVIDER_KIMI_API_KEY", oldKimiKey);
            Environment.SetEnvironmentVariable("PROVIDER_KIMI_BASE_URL", oldKimiBase);
            Environment.SetEnvironmentVariable("PROVIDER_MOONSHOT_API_KEY", oldMoonshotKey);
        }
    }

    [Theory]
    [InlineData("kimi", "KIMI", "https://api.moonshot.cn", "v1/chat/completions", "v1/models", true)]
    [InlineData("zhipu", "ZHIPU", "https://open.bigmodel.cn/api/paas", "v4/chat/completions", "v4/models", true)]
    [InlineData("qwen", "QWEN", "https://dashscope.aliyuncs.com/compatible-mode", "v1/chat/completions", "v1/models", false)]
    [InlineData("minimax", "MINIMAX", "https://api.minimax.io", "v1/chat/completions", "v1/models", false)]
    [InlineData("customopenai", "CUSTOMOPENAI", "", "v1/chat/completions", "v1/models", false)]
    public void ProviderCapabilitiesRegistry_OpenAiCompatibleProviders_AreRegistered(
        string providerName,
        string envPrefix,
        string defaultBaseUrl,
        string chatPath,
        string modelsPath,
        bool supportsReasoningEffort)
    {
        ProviderCapabilities caps = ProviderCapabilitiesRegistry.Get(providerName);

        Assert.Equal(ApiFormat.OpenAi, caps.ApiFormat);
        Assert.Equal(ProviderCategory.Direct, caps.Category);
        Assert.Equal(supportsReasoningEffort, caps.SupportsReasoningEffort);
        Assert.False(caps.SupportsTopK);
        Assert.Equal(envPrefix, caps.EnvPrefix);
        Assert.Equal(defaultBaseUrl, caps.DefaultBaseUrl);
        Assert.Equal(chatPath, caps.ChatPath);
        Assert.Equal(modelsPath, caps.ModelsPath);
    }

    [Fact]
    public void ProviderCapabilitiesRegistry_Hunyuan_IsRegisteredForTokenHub()
    {
        ProviderCapabilities caps = ProviderCapabilitiesRegistry.Get("hunyuan");

        Assert.Equal(ApiFormat.OpenAi, caps.ApiFormat);
        Assert.Equal(ProviderCategory.Direct, caps.Category);
        Assert.True(caps.SupportsReasoningEffort);
        Assert.False(caps.SupportsTopK);
        Assert.Equal("HUNYUAN", caps.EnvPrefix);
        Assert.Equal("https://tokenhub.tencentmaas.com", caps.DefaultBaseUrl);
        Assert.Equal("v1/chat/completions", caps.ChatPath);
        Assert.Equal("v1/models", caps.ModelsPath);
    }

    [Fact]
    public void ModelToProvider_IsNotNull()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        Assert.NotNull(registry.ModelToProvider);
    }
}
