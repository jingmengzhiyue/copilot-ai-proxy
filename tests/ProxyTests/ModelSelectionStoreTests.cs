namespace ProxyTests;

public class ModelSelectionStoreTests
{
    [Fact]
    public void GetExecutionConfigForModel_DeepSeekV4Pro_HasContextLength()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);

        Assert.True(config.ContextLength.HasValue);
        Assert.Equal(1_048_576, config.ContextLength.Value);
    }

    [Fact]
    public void GetExecutionConfigForModel_DeepSeekV4Pro_HasMaxOutputTokens()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);

        Assert.True(config.MaxOutputTokens.HasValue);
    }

    [Fact]
    public void GetExecutionConfigForModel_UnknownModel_ReturnsDefaults()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("nonexistent-model-xyz", registry.ModelToProvider);

        Assert.NotNull(config);
    }

    [Fact]
    public void GetProviderModelSelections_DeepSeek_ReturnsEntries()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry[] entries = store.GetProviderModelSelections("deepseek");

        Assert.NotEmpty(entries);
    }

    [Fact]
    public void GetProviderModelSelections_OpenAI_ReturnsEntries()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry[] entries = store.GetProviderModelSelections("openai");

        Assert.NotEmpty(entries);
    }

    [Fact]
    public void FindModelSelectionEntry_DeepSeekV4Pro_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("deepseek-v4-pro", "deepseek");

        Assert.NotNull(entry);
        Assert.Equal("deepseek-v4-pro", entry.Id);
    }

    [Fact]
    public void FindModelSelectionEntry_NonExistent_ReturnsNull()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("nonexistent", "deepseek");

        Assert.Null(entry);
    }

    [Fact]
    public void IsPreferredModel_DeepSeekV4Pro_ReturnsTrue()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("deepseek-v4-pro", "deepseek");

        Assert.True(isPreferred);
    }

    [Fact]
    public void IsPreferredModel_NonExistent_ReturnsFalse()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("nonexistent-model", "deepseek");

        Assert.False(isPreferred);
    }

    [Fact]
    public void GetPreferredModelPriority_DeepSeekV4Pro_ReturnsPriority()
    {
        ModelSelectionStore store = new();

        int priority = store.GetPreferredModelPriority("deepseek-v4-pro", "deepseek");

        Assert.True(priority >= 0);
    }

    [Fact]
    public void ProviderModelSelections_HasAllProviders()
    {
        ModelSelectionStore store = new();

        Assert.True(store.ProviderModelSelections.ContainsKey("deepseek"));
        Assert.True(store.ProviderModelSelections.ContainsKey("openai"));
        Assert.True(store.ProviderModelSelections.ContainsKey("nvidia"));
        Assert.True(store.ProviderModelSelections.ContainsKey("groq"));
        Assert.True(store.ProviderModelSelections.ContainsKey("moonshot"));
        Assert.True(store.ProviderModelSelections.ContainsKey("openrouter"));
    }

    [Fact]
    public void GetExecutionConfigForModel_HasTemperature()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);

        Assert.True(config.Temperature.HasValue);
        Assert.True(config.Temperature.Value >= 0);
    }

    [Fact]
    public void GetExecutionConfigForModel_HasMaxTokensPreferred()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);

        Assert.True(config.MaxTokensPreferred.HasValue);
    }

    [Fact]
    public void GetExecutionConfigForModel_DeepSeekCoder_HasReasoningEffort()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-coder-6.7b-instruct", registry.ModelToProvider);

        Assert.False(string.IsNullOrWhiteSpace(config.ReasoningEffort));
    }
}