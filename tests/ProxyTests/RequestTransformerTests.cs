using System.Text.Json;

namespace ProxyTests;

public class RequestTransformerTests
{
    [Fact]
    public void ReplaceModelInRequestBody_ReplacesExistingModel()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"model":"old-model","messages":[]}""";

        string result = sut.ReplaceModelInRequestBody(raw, "new-model");

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.Equal("new-model", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void ReplaceModelInRequestBody_AddsModelWhenMissing()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[]}""";

        string result = sut.ReplaceModelInRequestBody(raw, "added-model");

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.Equal("added-model", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void ModifyRequest_InjectsCachedReasoningContentForAssistantMessage()
    {
        RequestTransformer sut = CreateTransformer(out ReasoningCacheService cache);
        cache.Set("assistant:0", "cached reasoning");

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                { "role": "assistant", "content": "hola" }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.NotNull(result);
        using JsonDocument modified = JsonDocument.Parse(result!);
        JsonElement msg = modified.RootElement.GetProperty("messages")[0];
        Assert.Equal("cached reasoning", msg.GetProperty("reasoning_content").GetString());
    }

    [Fact]
    public void ModifyRequest_RemovesEmptyAssistantMessagesWithoutToolCalls()
    {
        RequestTransformer sut = CreateTransformer();

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                { "role": "user", "content": "hola" },
                { "role": "assistant", "content": "" },
                { "role": "assistant", "content": "ok" }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.NotNull(result);
        using JsonDocument modified = JsonDocument.Parse(result!);
        JsonElement messages = modified.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("ok", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public void ModifyRequest_KeepsEmptyAssistantWhenToolCallsPresent()
    {
        RequestTransformer sut = CreateTransformer();

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                {
                  "role": "assistant",
                  "content": "",
                  "tool_calls": [
                    {
                      "id": "call_1",
                      "type": "function",
                      "function": { "name": "ping", "arguments": "{}" }
                    }
                  ]
                }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.Null(result);
    }

    [Fact]
    public void ApplyExecutionDefaults_KeepsBodyWhenNoConfiguredDefaults()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[],"stream":false}""";

        string result = sut.ApplyExecutionDefaults(raw, "unknown-model-without-config");

        Assert.Equal(raw, result);
    }

    private static RequestTransformer CreateTransformer()
    {
        return CreateTransformer(out _);
    }

    private static RequestTransformer CreateTransformer(out ReasoningCacheService cache)
    {
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", "unit-test-key");
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_BASE_URL", "http://127.0.0.1:12345");

        ProviderHttpClientFactory httpClientFactory = new();
        ProviderRegistry providerRegistry = new(httpClientFactory);
        ModelSelectionStore modelSelectionStore = new();
        ModelCatalogService modelCatalog = new(providerRegistry, modelSelectionStore);
        cache = new ReasoningCacheService();

        return new RequestTransformer(modelCatalog, cache);
    }
}
