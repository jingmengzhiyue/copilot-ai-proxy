using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace ProxyTests;

[Collection("Proxy")]
public class ProviderBenchmarkServiceTests
{
    [Fact]
    public async Task Cycle_DeduplicatesAliasesAndRetainsOnlyNewest100Reports()
    {
        string? previous = Environment.GetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY");
        string directory = Path.Combine(Path.GetTempPath(), "benchmark-test-" + Guid.NewGuid());
        try
        {
            Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", "synthetic");
            using ProviderHttpClientFactory factory = new(new OfflineHandler());
            ProviderRegistry registry = new(factory);
            ModelCatalogService catalog = new(registry, new ModelSelectionStore());
            await catalog.RefreshAvailableModels(default);
            ProviderBenchmarkService service = new(registry, catalog, NullLogger<ProviderBenchmarkService>.Instance);
            Directory.CreateDirectory(directory);
            for (int i = 0; i < 101; i++)
                await File.WriteAllTextAsync(Path.Combine(directory, $"provider-benchmark-20000101-{i:D7}.json"), "{}");
            string unrelated = Path.Combine(directory, "keep.json");
            await File.WriteAllTextAsync(unrelated, "{}");
            await service.RunBenchmarkCycle(directory, default);
            string[] reports = Directory.GetFiles(directory, "provider-benchmark-*.json");
            Assert.Equal(100, reports.Length);
            Assert.True(File.Exists(unrelated));
            using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(reports.Order().Last()));
            var identities = report.RootElement.GetProperty("results").EnumerateArray()
                .Select(r => (r.GetProperty("provider").GetString(), r.GetProperty("upstreamModel").GetString())).ToArray();
            Assert.NotEmpty(identities);
            Assert.Equal(identities.Length, identities.Distinct().Count());
            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunBenchmarkCycle(directory, cancelled.Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", previous);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Get
                    ? """{"data":[{"id":"deepseek-v4-pro"},{"id":"deepseek-v4-flash"}]}"""
                    : """{"choices":[{"message":{"content":"pong"}}]}""")
            });
    }
}
