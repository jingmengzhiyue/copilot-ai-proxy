using System.Text.Json;
using Xunit;

namespace ProxyTests;

/// <summary>
/// Validates that ApplyExecutionDefaults injects the correct parameters
/// for every enabled model/provider declared in config/model-selection/*.json.
/// These tests are fully offline – no live API calls are made.
/// </summary>
public class ParameterValidationTests
{
    /// <summary>
    /// Creates a RequestTransformer with the current real environment variables.
    /// No snapshot or restore is needed since these are purely offline tests.
    /// </summary>
    private static RequestTransformer CreateTransformer()
    {
        ProviderHttpClientFactory httpClientFactory = new();
        ProviderRegistry providerRegistry           = new(httpClientFactory);
        ModelSelectionStore modelSelectionStore     = new();
        ModelCatalogService modelCatalog            = new(providerRegistry, modelSelectionStore);
        ReasoningCacheService cache                 = new();
        return new(modelCatalog, cache);
    }

    private static JsonElement Transform(RequestTransformer sut, string model, string provider = "")
    {
        string raw    = """{"model":"x","messages":[{"role":"user","content":"hi"}]}""";
        string result = sut.ApplyExecutionDefaults(raw, model, provider);
        return JsonDocument.Parse(result).RootElement;
    }

    private static JsonElement TransformWithBody(RequestTransformer sut, string body, string model, string provider = "")
    {
        string result = sut.ApplyExecutionDefaults(body, model, provider);
        return JsonDocument.Parse(result).RootElement;
    }

    // ---------- DeepSeek ----------

    [Theory]
    [InlineData("deepseek-v4-pro",              "deepseek", true,  false)]
    [InlineData("deepseek-v4-flash",            "deepseek", true,  false)]
    [InlineData("deepseek-coder-6.7b-instruct", "deepseek", false, true)]
    public void DeepSeek_ReasoningEffortPresenceMatchesModel(
        string model, string provider, bool expectReasoningEffort, bool expectTopP)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, provider);

        if (expectReasoningEffort)
            Assert.True(result.TryGetProperty("reasoning_effort", out _),
                $"{model}: reasoning_effort should be injected for DeepSeek reasoning models");
        else
            Assert.False(result.TryGetProperty("reasoning_effort", out _),
                $"{model}: reasoning_effort should NOT be injected for non-reasoning models");

        if (expectTopP)
            Assert.True(result.TryGetProperty("top_p", out _),
                $"{model}: top_p should be present for non-reasoning models");
        else
            Assert.False(result.TryGetProperty("top_p", out _),
                $"{model}: top_p must NOT be present alongside reasoning_effort (DeepSeek docs)");
    }

    [Theory]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-v4-flash")]
    public void DeepSeek_ReasoningModels_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "deepseek");

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"{model}: max_tokens should be injected");
        Assert.True(maxTok.GetInt32() > 0,
            $"{model}: max_tokens must be a positive integer");
    }

    [Fact]
    public void DeepSeek_Coder_HasTemperatureAndTopP()
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, "deepseek-coder-6.7b-instruct", "deepseek");

        Assert.True(result.TryGetProperty("temperature", out _),
            "deepseek-coder: temperature should be injected");
        Assert.True(result.TryGetProperty("top_p", out _),
            "deepseek-coder: top_p should be injected");
    }

    // ---------- NVIDIA ----------

    [Theory]
    [InlineData("deepseek-ai/deepseek-v4-pro")]
    [InlineData("qwen/qwen3-coder-480b-a35b-instruct")]
    [InlineData("qwen/qwen3.5-397b-a17b")]
    [InlineData("qwen/qwen3-next-80b-a3b-instruct")]
    [InlineData("nvidia/nemotron-3-super-120b-a12b")]
    [InlineData("openai/gpt-oss-120b")]
    [InlineData("nvidia/llama-3.3-nemotron-super-49b-v1.5")]
    public void Nvidia_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "nvidia");

        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"NVIDIA/{model}: reasoning_effort must NOT be sent (not supported by NVIDIA NIM API)");
    }

    [Theory]
    [InlineData("deepseek-ai/deepseek-v4-pro")]
    [InlineData("qwen/qwen3-coder-480b-a35b-instruct")]
    [InlineData("qwen/qwen3.5-397b-a17b")]
    [InlineData("qwen/qwen3-next-80b-a3b-instruct")]
    [InlineData("nvidia/nemotron-3-super-120b-a12b")]
    [InlineData("openai/gpt-oss-120b")]
    [InlineData("nvidia/llama-3.3-nemotron-super-49b-v1.5")]
    public void Nvidia_Models_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "nvidia");

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"NVIDIA/{model}: max_tokens should be injected from config");
        Assert.True(maxTok.GetInt32() > 0,
            $"NVIDIA/{model}: max_tokens must be positive");
    }

    // ---------- OpenAI ----------

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5-mini")]
    [InlineData("gpt-4.1")]
    [InlineData("gpt-4o")]
    public void OpenAI_Models_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openai");

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"OpenAI/{model}: max_tokens should be injected");
        Assert.True(maxTok.GetInt32() > 0,
            $"OpenAI/{model}: max_tokens must be positive");
    }

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5-mini")]
    public void OpenAI_ReasoningCapableModels_ReasoningEffortInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openai");

        Assert.True(result.TryGetProperty("reasoning_effort", out _),
            $"OpenAI/{model}: reasoning_effort should be injected (supported by OpenAI API)");
    }

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5-mini")]
    public void OpenAI_ReasoningModels_NoTopPAlongReasoningEffort(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openai");

        bool hasRE = result.TryGetProperty("reasoning_effort", out _);
        bool hasTP = result.TryGetProperty("top_p", out _);

        if (hasRE)
            Assert.False(hasTP,
                $"OpenAI/{model}: top_p must NOT be sent when reasoning_effort is active");
    }

    // ---------- Groq ----------

    [Theory]
    [InlineData("llama-3.3-70b-versatile")]
    [InlineData("qwen/qwen3-32b")]
    [InlineData("meta-llama/llama-4-scout-17b-16e-instruct")]
    [InlineData("llama-3.1-8b-instant")]
    public void Groq_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "groq");

        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"Groq/{model}: reasoning_effort must NOT be sent (not supported by Groq API)");
    }

    [Theory]
    [InlineData("llama-3.3-70b-versatile")]
    [InlineData("qwen/qwen3-32b")]
    [InlineData("meta-llama/llama-4-scout-17b-16e-instruct")]
    [InlineData("llama-3.1-8b-instant")]
    public void Groq_Models_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "groq");

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"Groq/{model}: max_tokens should be injected from config");
        Assert.True(maxTok.GetInt32() > 0,
            $"Groq/{model}: max_tokens must be positive");
    }

    // ---------- OpenRouter ----------

    [Theory]
    [InlineData("qwen/qwen3-coder:free")]
    [InlineData("moonshotai/kimi-k2.6:free")]
    [InlineData("qwen/qwen3-next-80b-a3b-instruct:free")]
    [InlineData("google/gemma-4-31b-it:free")]
    [InlineData("nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free")]
    public void OpenRouter_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openrouter");

        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"OpenRouter/{model}: reasoning_effort must NOT be sent");
    }

    // ---------- Provider-agnostic: client-supplied values are never overridden ----------

    [Theory]
    [InlineData("deepseek-v4-pro",   "deepseek")]
    [InlineData("deepseek-v4-flash", "deepseek")]
    [InlineData("gpt-5",             "openai")]
    [InlineData("llama-3.3-70b-versatile", "groq")]
    public void ClientSupplied_MaxTokens_IsNotOverridden(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[],"max_tokens":99}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"{provider}/{model}: max_tokens field must be present");
        Assert.Equal(99, maxTok.GetInt32());
    }

    [Theory]
    [InlineData("deepseek-v4-pro",   "deepseek")]
    [InlineData("gpt-5",             "openai")]
    public void ClientSupplied_ReasoningEffort_IsNotOverridden(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[],"reasoning_effort":"low"}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("reasoning_effort", out JsonElement re),
            $"{provider}/{model}: reasoning_effort must be present");
        Assert.Equal("low", re.GetString());
    }

    [Theory]
    [InlineData("deepseek-coder-6.7b-instruct", "deepseek")]
    [InlineData("qwen/qwen3.5-397b-a17b",        "nvidia")]
    [InlineData("llama-3.3-70b-versatile",        "groq")]
    public void ClientSupplied_Temperature_IsNotOverridden(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[],"temperature":0.99}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("temperature", out JsonElement temp),
            $"{provider}/{model}: temperature must be present");
        Assert.Equal(0.99, temp.GetDouble(), precision: 5);
    }

    // ---------- Context-window config completeness ----------

    [Theory]
    // DeepSeek
    [InlineData("deepseek-v4-pro",              1_048_576, 384_000)]
    [InlineData("deepseek-v4-flash",            1_048_576, 131_072)]
    [InlineData("deepseek-coder-6.7b-instruct",   128_000,   8_192)]
    // NVIDIA
    [InlineData("deepseek-ai/deepseek-v4-pro",  1_048_576, 384_000)]
    [InlineData("qwen/qwen3-coder-480b-a35b-instruct", 1_048_576, 65_536)]
    [InlineData("qwen/qwen3.5-397b-a17b",          262_144,  16_384)]
    [InlineData("qwen/qwen3-next-80b-a3b-instruct", 262_144,  16_384)]
    [InlineData("nvidia/nemotron-3-super-120b-a12b", 1_000_000, 262_144)]
    [InlineData("openai/gpt-oss-120b",             131_072, 131_072)]
    [InlineData("nvidia/llama-3.3-nemotron-super-49b-v1.5", 131_072, 16_384)]
    // OpenAI
    [InlineData("gpt-5",      400_000, 128_000)]
    [InlineData("gpt-5-mini", 400_000, 128_000)]
    [InlineData("gpt-4.1",  1_048_576,  32_768)]
    [InlineData("gpt-4o",     128_000,   8_192)]
    // Groq
    [InlineData("meta-llama/llama-4-scout-17b-16e-instruct", 10_000_000, 0)]
    public void AllModels_HaveCorrectContextWindowConfig(
        string model, int expectedContextLength, int minMaxOutput)
    {
        ModelSelectionStore store  = new();
        ModelExecutionConfig exec  = store.GetExecutionConfigForModel(model, new Dictionary<string, ProviderInfo>());

        Assert.True(exec.ContextLength.HasValue,
            $"{model}: context_length must be configured");
        Assert.Equal(expectedContextLength, exec.ContextLength!.Value);

        if (minMaxOutput > 0)
        {
            Assert.True(exec.MaxOutputTokens.HasValue,
                $"{model}: max_output_tokens must be configured");
            Assert.True(exec.MaxOutputTokens!.Value >= minMaxOutput,
                $"{model}: max_output_tokens {exec.MaxOutputTokens} < expected {minMaxOutput}");
        }
    }

    // ---------- DeepSeek reasoning_effort value is valid ----------

    [Theory]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-v4-flash")]
    public void DeepSeek_ReasoningEffort_IsValidValue(string model)
    {
        ModelSelectionStore store  = new();
        ModelExecutionConfig exec  = store.GetExecutionConfigForModel(model, new Dictionary<string, ProviderInfo>());

        Assert.False(string.IsNullOrWhiteSpace(exec.ReasoningEffort),
            $"{model}: reasoning_effort should be configured in deepseek.json");

        string[] valid = ["low", "medium", "high", "default"];
        Assert.Contains(exec.ReasoningEffort, valid);
    }

    // ---------- Temperature sanity bounds ----------

    [Theory]
    [InlineData("deepseek-coder-6.7b-instruct", "deepseek")]
    [InlineData("qwen/qwen3-coder-480b-a35b-instruct", "nvidia")]
    [InlineData("llama-3.3-70b-versatile", "groq")]
    [InlineData("qwen/qwen3-coder:free", "openrouter")]
    public void ConfiguredTemperature_IsWithinValidRange(string model, string provider)
    {
        _ = provider; // documented for readability
        ModelSelectionStore  store = new();
        ModelExecutionConfig exec  = store.GetExecutionConfigForModel(model, new Dictionary<string, ProviderInfo>());

        if (!exec.Temperature.HasValue) return;

        double t = exec.Temperature.Value;
        Assert.True(t is >= 0.0 and <= 2.0,
            $"{model}: temperature {t} is outside [0, 2.0]");
    }
}
