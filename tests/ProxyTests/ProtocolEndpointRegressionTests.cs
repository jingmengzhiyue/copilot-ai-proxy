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
public sealed class ProtocolEndpointRegressionTests
{
    [Theory]
    [InlineData("google", false)] [InlineData("google", true)]
    [InlineData("zhipu", false)] [InlineData("zhipu", true)]
    public async Task NativeEntry_UsesConfiguredChatPath(string provider, bool stream)
    {
        await using var host = await Host.Start();
        using var response = await host.Post("/api/chat", provider, stream);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/" + ProviderCapabilitiesRegistry.Get(provider).ChatPath, host.Handler.Paths.Single());
    }

    [Theory]
    [InlineData("ollama", null)] [InlineData("ollama", true)] [InlineData("ollama", false)]
    [InlineData("google", null)] [InlineData("google", true)] [InlineData("google", false)]
    public async Task NativeEntry_StreamDefaultMatchesResponseAndUpstream(string provider, bool? stream)
    {
        await using var host = await Host.Start();
        using var response = await host.Post("/api/chat", provider, stream);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(stream == false ? "application/json" : "application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        using var request = JsonDocument.Parse(host.Handler.Bodies.Single());
        Assert.Equal(stream != false, request.RootElement.GetProperty("stream").GetBoolean());
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"done\":true", body);
    }

    [Theory]
    [InlineData("/api/chat", "ollama", false)] [InlineData("/api/chat", "ollama", true)]
    [InlineData("/api/chat", "google", false)] [InlineData("/api/chat", "google", true)]
    [InlineData("/v1/chat/completions", "ollama", false)] [InlineData("/v1/chat/completions", "ollama", true)]
    public async Task TransportFailure_Is502(string path, string provider, bool stream)
    {
        await using var host = await Host.Start();
        host.Handler.Failure = new HttpRequestException("synthetic");
        using var response = await host.Post(path, provider, stream);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Single(host.Handler.Paths);
    }

    [Theory]
    [InlineData(false, 401)] [InlineData(true, 401)]
    [InlineData(false, 429)] [InlineData(true, 429)]
    [InlineData(false, 503)] [InlineData(true, 503)]
    public async Task NativeUpstreamHttpError_PreservesLastStatus(bool stream, int status)
    {
        await using var host = await Host.Start();
        host.Handler.Status = status;
        using var response = await host.Post("/v1/chat/completions", "ollama", stream);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Contains("synthetic upstream", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    public async Task CandidateTransportFailure_FailsOverBeforeWritingResponse(bool stream)
    {
        await using var host = await Host.Start();
        host.Handler.FailHost = host.Registry.ResolveCandidates("probe")[0].Provider.Client.BaseAddress!.Host;
        using var response = await host.Client.PostAsync("/v1/chat/completions", Json(new { model = "probe", stream, messages = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, host.Handler.Paths.Count);
    }

    [Theory]
    [InlineData("\"messages\":null")] [InlineData("\"messages\":42")] [InlineData("\"messages\":{}")]
    [InlineData("\"messages\":\"bad\"")] [InlineData("\"messages\":[null]")]
    [InlineData("\"messages\":[{\"role\":42}]")]
    [InlineData("\"messages\":[{\"tool_calls\":{}}]")]
    [InlineData("\"messages\":[{\"images\":42}]")]
    [InlineData("\"messages\":[{\"content\":false}]")]
    [InlineData("\"options\":{\"num_predict\":1.5}")]
    [InlineData("\"options\":{\"num_predict\":999999999999}")]
    public async Task InvalidFields_Are400BeforeCallingUpstream(string fields)
    {
        await using var host = await Host.Start();
        foreach (string path in new[] { "/api/chat", "/v1/chat/completions" })
        {
            using var response = await host.Client.PostAsync(path, new StringContent("{\"model\":\"probe@google\"," + fields + "}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Empty(host.Handler.Paths);
    }

    [Theory]
    [InlineData("{\"tool_calls\":[{\"function\":{\"name\":\"f\",\"arguments\":\"{}\"}}]}")]
    [InlineData("{\"content\":null}")]
    [InlineData("{\"content\":\"OK\"}")]
    public async Task OpenAiResponse_OptionalContentIsAccepted(string message)
    {
        await using var host = await Host.Start();
        host.Handler.ResponseBody = "{\"choices\":[{\"message\":" + message + "}]}";
        using var response = await host.Post("/api/chat", "google", false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"choices\":[]}")] [InlineData("{}")] [InlineData("not-json")]
    public async Task InvalidUpstreamResponse_Is502(string body)
    {
        await using var host = await Host.Start();
        host.Handler.ResponseBody = body;
        using var response = await host.Post("/api/chat", "google", false);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Theory]
    [InlineData("ollama")] [InlineData("google")]
    public async Task HeadersThenStalledBody_UsesModelDeadline(string provider)
    {
        await using var host = await Host.Start();
        host.Handler.StallBody = true;
        using var response = await host.Post("/api/chat", provider, true).WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.True(host.Handler.BodyCancelled);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NativeDefaults_RespectForceAndPreserveOtherOptions(bool force)
    {
        await using var host = await Host.Start();
        host.Store.ProviderModelSelections["ollama"] = [new("probe", 1, true, new(Temperature: 1, TopP: .8, MaxTokensPreferred: 4, OverrideClientParams: force))];
        using var response = await host.Client.PostAsync("/api/chat", Json(new { model = "probe@ollama", stream = false, options = new { temperature = .3, num_ctx = 12 }, messages = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(host.Handler.Bodies.Single());
        var options = doc.RootElement.GetProperty("options");
        Assert.Equal(force ? 1 : .3, options.GetProperty("temperature").GetDouble());
        Assert.Equal(4, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(12, options.GetProperty("num_ctx").GetInt32());
    }

    [Theory]
    [InlineData(1, "")] [InlineData(2, "text")]
    public async Task NativeToolSse_HasToolsStableIdentityAndFinishReason(int count, string content)
    {
        await using var host = await Host.Start();
        host.Handler.ResponseBody = JsonSerializer.Serialize(new { message = new { content, tool_calls = Enumerable.Range(0, count).Select(i => new { function = new { name = "f" + i, arguments = new { value = i } } }).ToArray() }, done = true });
        using var response = await host.Post("/v1/chat/completions", "ollama", true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var chunks = (await response.Content.ReadAsStringAsync()).Split('\n').Where(x => x.StartsWith("data: {")).Select(x => JsonDocument.Parse(x[6..])).ToArray();
        try
        {
            Assert.Equal(2, chunks.Length);
            Assert.Equal(chunks[0].RootElement.GetProperty("id").GetString(), chunks[1].RootElement.GetProperty("id").GetString());
            Assert.Equal(chunks[0].RootElement.GetProperty("created").GetInt64(), chunks[1].RootElement.GetProperty("created").GetInt64());
            var calls = chunks[0].RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls");
            Assert.Equal(count, calls.GetArrayLength());
            Assert.Equal(0, calls[0].GetProperty("index").GetInt32());
            Assert.Equal(JsonValueKind.String, calls[0].GetProperty("function").GetProperty("arguments").ValueKind);
            Assert.Equal("tool_calls", chunks[1].RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        }
        finally { foreach (var chunk in chunks) chunk.Dispose(); }
    }

    [Theory]
    [InlineData("https://example.test/picture.png")] [InlineData("data:image/png;base64,%%%%")]
    public async Task InvalidNativeImage_Is400AndNeverFetched(string url)
    {
        await using var host = await Host.Start();
        using var response = await host.Client.PostAsync("/v1/chat/completions", Json(new { model = "probe@ollama", messages = new[] { new { role = "user", content = new[] { new { type = "image_url", image_url = new { url } } } } } }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Handler.Paths);
    }

    [Fact]
    public async Task NativeParameters_MapStopSeedAndSchema()
    {
        await using var host = await Host.Start();
        using var response = await host.Client.PostAsync("/api/chat", Json(new { model = "probe@google", stream = false, format = new { type = "object", properties = new { answer = new { type = "string" } } }, options = new { stop = new[] { "END" }, seed = 42 }, messages = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(host.Handler.Bodies.Single());
        Assert.Equal(42, doc.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal("END", doc.RootElement.GetProperty("stop")[0].GetString());
        Assert.Equal("json_schema", doc.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }


    [Theory]
    [InlineData("/api/chat", false)] [InlineData("/api/chat", true)]
    [InlineData("/v1/chat/completions", false)] [InlineData("/v1/chat/completions", true)]
    public async Task History_NeverRecoversUnrelatedReasoning(string path, bool tools)
    {
        await using var host = await Host.Start();
        host.Cache.Set("assistant:0", "private-cache-marker");
        host.Cache.Set("toolcall:repeated", "private-cache-marker");
        object message = tools
            ? new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "repeated", type = "function", function = new { name = "echo", arguments = path == "/api/chat" ? (object)new { value = 1 } : "{\"value\":1}" } } } }
            : new { role = "assistant", content = "Other conversation" };
        using var response = await host.Client.PostAsync(path, Json(new { model = "probe@google", stream = false, messages = new[] { message } }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("private-cache-marker", host.Handler.Bodies.Single());
    }

    [Theory]
    [InlineData("/api/chat", false)] [InlineData("/api/chat", true)]
    [InlineData("/v1/chat/completions", false)] [InlineData("/v1/chat/completions", true)]
    public async Task Responses_DoNotCreateSharedReasoningCache(string path, bool stream)
    {
        await using var host = await Host.Start();
        const string message = """{"role":"assistant","content":"OK","reasoning_content":"private-response-marker","tool_calls":[{"index":0,"id":"fresh","function":{"name":"echo","arguments":"{}"}}]}""";
        host.Handler.ResponseBody = stream
            ? "data: {\"choices\":[{\"delta\":" + message + ",\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n"
            : "{\"choices\":[{\"message\":" + message + ",\"finish_reason\":\"tool_calls\"}]}";
        using var response = await host.Post(path, "google", stream);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(host.Cache.TryGet("assistant:0", out _));
        Assert.False(host.Cache.TryGet("toolcall:fresh", out _));
    }


    [Theory]
    [InlineData("2147483648")] [InlineData("1.5")] [InlineData("-1")]
    public async Task InvalidUpstreamToolIndex_Is502(string index)
    {
        await using var host = await Host.Start();
        host.Handler.ResponseBody = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":" + index + ",\"function\":{\"name\":\"f\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n";
        using var response = await host.Post("/api/chat", "google", true);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("\"done\":true", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MixedToolResultIds_ConsumeDistinctCalls(bool nullId)
    {
        await using var host = await Host.Start();
        string secondResult = nullId ? "\"tool_call_id\":null," : "";
        string body = """{"model":"probe@google","stream":false,"messages":[{"role":"assistant","tool_calls":[{"id":"first","function":{"name":"echo","arguments":{}}},{"id":"second","function":{"name":"echo","arguments":{}}}]},{"role":"tool","tool_call_id":"first","content":"one"},{"role":"tool","tool_name":"echo","content":"two",SECOND"other":"value"}]}""".Replace("SECOND", secondResult);
        using var response = await host.Client.PostAsync("/api/chat", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var request = JsonDocument.Parse(host.Handler.Bodies.Single());
        var messages = request.RootElement.GetProperty("messages");
        Assert.Equal("first", messages[1].GetProperty("tool_call_id").GetString());
        Assert.Equal("second", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Single(messages[2].EnumerateObject(), p => p.Name == "tool_call_id");
    }

    [Fact]
    public async Task UnknownExplicitToolResultId_Is400WithoutUpstreamCall()
    {
        await using var host = await Host.Start();
        using var response = await host.Client.PostAsync("/api/chat", Json(new { model = "probe@google", stream = false, messages = new[] { new { role = "tool", tool_call_id = "unknown", content = "value" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Handler.Paths);
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private sealed class Handler : HttpMessageHandler
    {
        internal List<string> Paths = [];
        internal List<string> Bodies = [];
        internal Exception? Failure;
        internal string? FailHost;
        internal string? ResponseBody;
        internal int Status = 200;
        internal bool StallBody;
        internal bool BodyCancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") };
            Paths.Add(request.RequestUri!.AbsolutePath);
            string body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
            if (Failure != null) throw Failure;
            if (request.RequestUri.Host == FailHost) throw new HttpRequestException("synthetic candidate failure");
            if (StallBody) return new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(this)) };
            using var doc = JsonDocument.Parse(body);
            bool stream = doc.RootElement.TryGetProperty("stream", out var value) && value.GetBoolean();
            bool native = request.RequestUri.AbsolutePath == "/api/chat";
            string text = ResponseBody ?? (Status != 200 ? "{\"error\":\"synthetic upstream\"}" : native
                ? "{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"done\":true}"
                : stream ? "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                : "{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}" );
            return new((HttpStatusCode)Status) { Content = new StringContent(text, Encoding.UTF8, native && stream ? "application/x-ndjson" : stream ? "text/event-stream" : "application/json") };
        }
    }
    private sealed class StalledStream(Handler handler) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { handler.BodyCancelled = true; throw; }
            return 0;
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        private Dictionary<string, string?> EnvironmentBefore = [];
        private WebApplication App = null!;
        internal Handler Handler = new();
        internal ProviderRegistry Registry = null!;
        internal ModelSelectionStore Store = new();
        internal HttpClient Client = null!;
        internal ReasoningCacheService Cache => App.Services.GetRequiredService<ReasoningCacheService>();
        internal static async Task<Host> Start()
        {
            var host = new Host();
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = (string)entry.Key;
                if (key.StartsWith("PROVIDER_") || key.StartsWith("DEEPSEEK_"))
                { host.EnvironmentBefore[key] = (string?)entry.Value; Environment.SetEnvironmentVariable(key, null); }
            }
            foreach (string provider in new[] { "google", "zhipu", "deepseek", "ollama" })
            {
                var caps = ProviderCapabilitiesRegistry.Get(provider);
                Environment.SetEnvironmentVariable($"PROVIDER_{caps.EnvPrefix}_BASE_URL", $"http://{provider}.test");
                Environment.SetEnvironmentVariable($"PROVIDER_{caps.EnvPrefix}_API_KEY", "synthetic");
                host.Store.ProviderModelSelections[provider] = [new("probe", 1, true, new(TimeoutSeconds: 1))];
            }
            host.Registry = new(new ProviderHttpClientFactory(host.Handler));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(host.Registry);
            builder.Services.AddSingleton(host.Store);
            builder.Services.AddSingleton<ModelCatalogService>();
            builder.Services.AddSingleton<ReasoningCacheService>();
            builder.Services.AddSingleton<RequestTransformer>();
            builder.Services.AddSingleton<ChatStreamingService>();
            builder.Services.AddSingleton<OllamaResponseBuilder>();
            host.App = builder.Build();
            host.App.MapOpenAiEndpoints(); host.App.MapOllamaEndpoints();
            await host.App.StartAsync(); host.Client = host.App.GetTestClient();
            return host;
        }
        internal Task<HttpResponseMessage> Post(string path, string provider, bool? stream)
        {
            var value = new Dictionary<string, object?> { ["model"] = "probe@" + provider, ["messages"] = Array.Empty<object>() };
            if (stream.HasValue) value["stream"] = stream.Value;
            return Client.PostAsync(path, Json(value));
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await App.DisposeAsync();
            foreach (var provider in Registry.Providers) provider.Client.Dispose();
            Handler.Dispose();
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = (string)entry.Key;
                if (key.StartsWith("PROVIDER_") || key.StartsWith("DEEPSEEK_")) Environment.SetEnvironmentVariable(key, null);
            }
            foreach (var item in EnvironmentBefore) Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }
}
