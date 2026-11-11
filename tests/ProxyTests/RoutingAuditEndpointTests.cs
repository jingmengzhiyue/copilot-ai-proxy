using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProxyTests;

[Collection("Proxy")]
public sealed class RoutingAuditEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientCancellation_StopsUpstreamWithoutFailover(bool stream)
    {
        await using var host = await AuditHost.Start();
        host.Handler.WaitForCancellation = true;
        using var cancellation = new CancellationTokenSource();
        Task<HttpResponseMessage> request = host.Client.PostAsync("/v1/chat/completions", Body("k3@kimicode", stream), cancellation.Token);
        await host.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        await host.Handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "kimicode.test" }, host.Handler.ChatHosts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolsAndLargeBody_ReachPinnedProviderUnchanged(bool stream)
    {
        await using var host = await AuditHost.Start();
        string context = new('x', 120_000);
        string body = JsonSerializer.Serialize(new
        {
            model = "KIMICODE - k3:latest", stream,
            messages = new object[]
            {
                new { role = "system", content = context },
                new { role = "user", content = "Use echo." },
                new { role = "assistant", content = (string?)null,
                    tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "echo", arguments = "{\"text\":\"OK\"}" } } } },
                new { role = "tool", tool_call_id = "call_1", content = "OK" }
            },
            tools = new[] { new { type = "function", function = new { name = "echo", parameters = new { type = "object" } } } },
            tool_choice = "auto"
        });
        host.Handler.Stream = stream;
        using var response = await host.Client.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var captured = JsonDocument.Parse(host.Handler.Body!);
        using var original = JsonDocument.Parse(body);
        Assert.Equal("k3", captured.RootElement.GetProperty("model").GetString());
        foreach (string field in new[] { "messages", "tools", "tool_choice" })
            Assert.True(JsonElement.DeepEquals(original.RootElement.GetProperty(field), captured.RootElement.GetProperty(field)));
        Assert.Equal("kimicode.test", host.Handler.Uri!.Host);
        Assert.Equal("/v1/chat/completions", host.Handler.Uri.AbsolutePath);
        Assert.Single(host.Handler.ChatHosts);
        string output = await response.Content.ReadAsStringAsync();
        if (stream)
        {
            Assert.Contains("data: [DONE]", output);
            Assert.Contains("tool_calls", output);
            Assert.Contains("finish_reason", output);
            Assert.Contains("usage", output);
        }
    }

    [Theory]
    [InlineData("k3@kimicode", true)]
    [InlineData("k3@kimicode", false)]
    [InlineData("deepseek-v4-pro@deepseek", true)]
    public async Task ReasoningDetails_OnlyKimiDropsStreamingIndexFromAssistantHistory(string model, bool stream)
    {
        await using var host = await AuditHost.Start();
        string body = JsonSerializer.Serialize(new
        {
            model, stream,
            messages = new object[]
            {
                new { role = "system", content = "Synthetic instructions." },
                new { role = "user", content = "Use echo." },
                new { role = "assistant", content = (string?)null, reasoning_content = "thinking",
                    reasoning_details = new object[]
                    {
                        new { type = "summary", summary = "thinking", index = 0, metadata = new { index = 7 } },
                        new { type = "encrypted", encrypted = "opaque", index = 1 }
                    },
                    tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "echo", arguments = "{}" } } } },
                new { role = "tool", tool_call_id = "call_1", content = "OK" }
            }
        });
        host.Handler.Stream = stream;
        using var response = await host.Client.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var captured = JsonDocument.Parse(host.Handler.Body!);
        using var original = JsonDocument.Parse(body);
        JsonElement originalMessage = original.RootElement.GetProperty("messages")[2];
        JsonElement actualMessage = captured.RootElement.GetProperty("messages")[2];
        JsonElement details = actualMessage.GetProperty("reasoning_details");
        Assert.Equal(originalMessage.GetProperty("reasoning_content").GetString(), actualMessage.GetProperty("reasoning_content").GetString());
        Assert.True(JsonElement.DeepEquals(originalMessage.GetProperty("tool_calls"), actualMessage.GetProperty("tool_calls")));
        Assert.Equal("thinking", details[0].GetProperty("summary").GetString());
        Assert.Equal("opaque", details[1].GetProperty("encrypted").GetString());
        Assert.Equal(7, details[0].GetProperty("metadata").GetProperty("index").GetInt32());
        Assert.Equal(model.Contains("kimicode") == false, details[0].TryGetProperty("index", out _));
        Assert.Equal(model.Contains("kimicode") == false, details[1].TryGetProperty("index", out _));
    }

    [Fact]
    public async Task OlderKimiHistoryWithoutReasoningDetails_IsPreserved()
    {
        await using var host = await AuditHost.Start();
        const string body = """{"model":"k3@kimicode","messages":[{"role":"assistant","content":null,"reasoning_content":"thinking","tool_calls":[{"id":"call_1","type":"function","function":{"name":"echo","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_1","content":"OK"}]}""";
        using var response = await host.Client.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var captured = JsonDocument.Parse(host.Handler.Body!);
        using var original = JsonDocument.Parse(body);
        Assert.True(JsonElement.DeepEquals(original.RootElement.GetProperty("messages"), captured.RootElement.GetProperty("messages")));
    }

    [Theory]
    [InlineData(401, false)] [InlineData(402, false)] [InlineData(403, false)]
    [InlineData(404, false)] [InlineData(429, false)] [InlineData(500, false)] [InlineData(503, false)]
    [InlineData(401, true)] [InlineData(402, true)] [InlineData(403, true)]
    [InlineData(404, true)] [InlineData(429, true)] [InlineData(500, true)] [InlineData(503, true)]
    public async Task ExplicitProvider_UpstreamErrorsNeverCrossProviders(int status, bool stream)
    {
        await using var host = await AuditHost.Start();
        host.Handler.Status = status;
        using var response = await host.Client.PostAsync("/v1/chat/completions", Body("k3@kimicode", stream));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(new[] { "kimicode.test" }, host.Handler.ChatHosts);
        Assert.Contains("upstream test error", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task TransportFailure_Returns502WithoutProviderFallback(bool stream, bool timeout)
    {
        await using var host = await AuditHost.Start();
        host.Handler.Failure = timeout ? new OperationCanceledException("synthetic timeout") : new HttpRequestException("synthetic network failure");
        using var response = await host.Client.PostAsync("/v1/chat/completions", Body("k3@kimicode", stream));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(new[] { "kimicode.test" }, host.Handler.ChatHosts);
    }

    [Theory]
    [InlineData("/api/show", "{broken", 400)]
    [InlineData("/api/show", "[]", 400)]
    [InlineData("/api/show", "{\"model\":\"k3@missing\"}", 404)]
    [InlineData("/v1/chat/completions", "{\"stream\":\"true\"}", 400)]
    [InlineData("/api/chat", "{\"stream\":\"true\"}", 400)]
    public async Task InvalidRequests_DoNotSucceedOrBecome500(string path, string body, int status)
    {
        await using var host = await AuditHost.Start();
        using var response = await host.Client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Empty(host.Handler.ChatHosts);
    }

    private static StringContent Body(string model, bool stream) => new(JsonSerializer.Serialize(new
    {
        model, stream, messages = new[] { new { role = "user", content = "Synthetic audit request" } }
    }), Encoding.UTF8, "application/json");

    private sealed class AuditHandler : HttpMessageHandler
    {
        public int Status = 200;
        public bool Stream;
        public Exception? Failure;
        public string? Body;
        public Uri? Uri;
        public List<string> ChatHosts = [];
        public bool WaitForCancellation;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") };
            ChatHosts.Add(request.RequestUri!.Host);
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(ct);
            if (WaitForCancellation)
            {
                Started.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { Cancelled.SetResult(); throw; }
            }
            if (Failure is not null) throw Failure;
            string text = Status != 200 ? "{\"error\":{\"message\":\"upstream test error\"}}" : Stream
                ? "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call_1\",\"index\":0,\"function\":{\"name\":\"echo\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: {\"choices\":[],\"usage\":{\"total_tokens\":2}}\n\ndata: [DONE]\n\n"
                : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}";
            return new((HttpStatusCode)Status) { Content = new StringContent(text, Encoding.UTF8, Stream ? "text/event-stream" : "application/json") };
        }
    }

    private sealed class AuditHost : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _environment = new();
        private WebApplication _app = null!;
        private ProviderRegistry _registry = null!;
        public AuditHandler Handler = new();
        public HttpClient Client = null!;

        public static async Task<AuditHost> Start()
        {
            var host = new AuditHost();
            foreach (string provider in ProviderCapabilitiesRegistry.KnownProviders)
            {
                string prefix = ProviderCapabilitiesRegistry.Get(provider).EnvPrefix;
                host.Set($"PROVIDER_{prefix}_API_KEY", provider is "kimicode" or "deepseek" ? "audit-dummy-key" : null);
                host.Set($"PROVIDER_{prefix}_BASE_URL", $"http://{provider}.test");
            }
            host.Set("DEEPSEEK_API_KEY", null);
            host.Set("PROVIDER_OLLAMACLOUD_API_KEY", null);
            host._registry = new(new ProviderHttpClientFactory(host.Handler));
            var builder = WebApplication.CreateBuilder();
            // Tests must not depend on access to the host Windows Event Log.
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(host._registry);
            builder.Services.AddSingleton<ModelSelectionStore>();
            builder.Services.AddSingleton<ModelCatalogService>();
            builder.Services.AddSingleton<ReasoningCacheService>();
            builder.Services.AddSingleton<RequestTransformer>();
            builder.Services.AddSingleton<ChatStreamingService>();
            builder.Services.AddSingleton<OllamaResponseBuilder>();
            host._app = builder.Build();
            host._app.MapOpenAiEndpoints();
            host._app.MapOllamaEndpoints();
            await host._app.StartAsync();
            host.Client = host._app.GetTestClient();
            return host;
        }

        private void Set(string key, string? value)
        {
            _environment.TryAdd(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            foreach (var provider in _registry.Providers) provider.Client.Dispose();
            Handler.Dispose();
            foreach (var entry in _environment) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }
}
