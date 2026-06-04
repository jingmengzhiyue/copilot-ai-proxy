using System.Net.Http.Headers;

namespace ProxyTests;

public class ProviderHttpClientFactoryTests
{
    [Fact]
    public void CreateProviderClient_SetsBaseAddress()
    {
        ProviderHttpClientFactory factory = new();
        string baseUrl = "http://localhost:8080";

        HttpClient client = factory.CreateProviderClient("deepseek", baseUrl, "test-key");

        Assert.NotNull(client.BaseAddress);
        Assert.Equal(baseUrl, client.BaseAddress!.ToString().TrimEnd('/'));
    }

    [Fact]
    public void CreateProviderClient_SetsAuthorizationHeader()
    {
        ProviderHttpClientFactory factory = new();
        string apiKey = "my-secret-key";

        HttpClient client = factory.CreateProviderClient("deepseek", "http://localhost:8080", apiKey);

        Assert.NotNull(client.DefaultRequestHeaders.Authorization);
        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization.Scheme);
        Assert.Equal(apiKey, client.DefaultRequestHeaders.Authorization.Parameter);
    }

    [Fact]
    public void CreateProviderClient_SetsAcceptJsonHeader()
    {
        ProviderHttpClientFactory factory = new();

        HttpClient client = factory.CreateProviderClient("deepseek", "http://localhost:8080", "key");

        Assert.Contains(client.DefaultRequestHeaders.Accept, a => a.MediaType == "application/json");
    }

    [Fact]
    public void CreateProviderClient_ForOpenRouter_AddsRefererHeader()
    {
        Environment.SetEnvironmentVariable("PROVIDER_OPENROUTER_REFERER", "https://example.com");
        ProviderHttpClientFactory factory = new();

        HttpClient client = factory.CreateProviderClient("openrouter", "http://localhost:8080", "key");

        Assert.True(client.DefaultRequestHeaders.TryGetValues("HTTP-Referer", out IEnumerable<string>? refererValues));
        Assert.Contains("https://example.com", refererValues!);
        Environment.SetEnvironmentVariable("PROVIDER_OPENROUTER_REFERER", null);
    }

    [Fact]
    public void CreateProviderClient_ForOpenRouter_AddsTitleHeader()
    {
        Environment.SetEnvironmentVariable("PROVIDER_OPENROUTER_TITLE", "My App");
        ProviderHttpClientFactory factory = new();

        HttpClient client = factory.CreateProviderClient("openrouter", "http://localhost:8080", "key");

        Assert.True(client.DefaultRequestHeaders.TryGetValues("X-Title", out IEnumerable<string>? titleValues));
        Assert.Contains("My App", titleValues!);
        Environment.SetEnvironmentVariable("PROVIDER_OPENROUTER_TITLE", null);
    }

    [Fact]
    public void CreateProviderClient_ForOpenRouter_WithoutEnvVars_DoesNotAddExtraHeaders()
    {
        Environment.SetEnvironmentVariable("PROVIDER_OPENROUTER_REFERER", null);
        Environment.SetEnvironmentVariable("PROVIDER_OPENROUTER_TITLE", null);
        Environment.SetEnvironmentVariable("OPENROUTER_HTTP_REFERER", null);
        Environment.SetEnvironmentVariable("OPENROUTER_X_TITLE", null);
        ProviderHttpClientFactory factory = new();

        HttpClient client = factory.CreateProviderClient("openrouter", "http://localhost:8080", "key");

        Assert.False(client.DefaultRequestHeaders.Contains("HTTP-Referer"));
        Assert.False(client.DefaultRequestHeaders.Contains("X-Title"));
    }

    [Fact]
    public void CreateProviderClient_ForNonOpenRouter_DoesNotAddRefererHeader()
    {
        Environment.SetEnvironmentVariable("PROVIDER_OPENROUTER_REFERER", "https://example.com");
        ProviderHttpClientFactory factory = new();

        HttpClient client = factory.CreateProviderClient("deepseek", "http://localhost:8080", "key");

        Assert.False(client.DefaultRequestHeaders.Contains("HTTP-Referer"));
        Environment.SetEnvironmentVariable("PROVIDER_OPENROUTER_REFERER", null);
    }

    [Fact]
    public void CreateProviderClient_ForOpenRouter_FallbackToOpenRouterEnvVars()
    {
        Environment.SetEnvironmentVariable("PROVIDER_OPENROUTER_REFERER", null);
        Environment.SetEnvironmentVariable("OPENROUTER_HTTP_REFERER", "https://fallback.com");
        Environment.SetEnvironmentVariable("PROVIDER_OPENROUTER_TITLE", null);
        Environment.SetEnvironmentVariable("OPENROUTER_X_TITLE", "Fallback Title");
        ProviderHttpClientFactory factory = new();

        HttpClient client = factory.CreateProviderClient("openrouter", "http://localhost:8080", "key");

        Assert.True(client.DefaultRequestHeaders.TryGetValues("HTTP-Referer", out IEnumerable<string>? refererValues));
        Assert.Contains("https://fallback.com", refererValues!);
        Assert.True(client.DefaultRequestHeaders.TryGetValues("X-Title", out IEnumerable<string>? titleValues));
        Assert.Contains("Fallback Title", titleValues!);
        Environment.SetEnvironmentVariable("OPENROUTER_HTTP_REFERER", null);
        Environment.SetEnvironmentVariable("OPENROUTER_X_TITLE", null);
    }

    [Fact]
    public void CreateProviderClient_SetsTimeout()
    {
        ProviderHttpClientFactory factory = new();

        HttpClient client = factory.CreateProviderClient("deepseek", "http://localhost:8080", "key");

        Assert.Equal(TimeSpan.FromMinutes(5), client.Timeout);
    }
}
