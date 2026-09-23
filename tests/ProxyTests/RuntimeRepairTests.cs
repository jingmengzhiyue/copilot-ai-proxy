using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProxyTests;

[Collection("Proxy")]
public class RuntimeRepairTests
{
    [Theory]
    [InlineData(false, null, "file-value")]
    [InlineData(false, "process-value", "process-value")]
    [InlineData(false, "", "")]
    [InlineData(true, null, null)]
    public void DotEnv_PreservesProcessAndIsolatesTesting(bool testing, string? processValue, string? expected)
    {
        string directory = Path.Combine(Path.GetTempPath(), "proxy-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string key = "PROXY_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(Path.Combine(directory, ".env"), $"# standalone comment\n =ignored\n{key}='file-value'\n");
            Environment.SetEnvironmentVariable(key, processValue);
            DotEnvLoader.Load(directory, directory, testing);
            Assert.Equal(expected, Environment.GetEnvironmentVariable(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DotEnv_NoFileDoesNotChangeEnvironment_AndApplicationFileWins()
    {
        string directory = Path.Combine(Path.GetTempPath(), "proxy-env-" + Guid.NewGuid().ToString("N"));
        string application = Path.Combine(directory, "app");
        Directory.CreateDirectory(application);
        string key = "PROXY_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            DotEnvLoader.Load(application, directory, false);
            Assert.Null(Environment.GetEnvironmentVariable(key));
            File.WriteAllText(Path.Combine(directory, ".env"), $"{key}=working\n");
            File.WriteAllText(Path.Combine(application, ".env"), $"{key}=application\n");
            DotEnvLoader.Load(application, directory, false);
            Assert.Equal("application", Environment.GetEnvironmentVariable(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Cache_BoundsCountBytesAndExpires()
    {
        TestClock clock = new();
        ReasoningCacheService cache = new(clock);
        for (int i = 0; i < ReasoningCacheService.MaximumEntries + 10; i++)
        {
            cache.Set($"k{i}", "value");
            clock.Now += TimeSpan.FromMilliseconds(1);
        }
        Assert.False(cache.TryGet("k0", out _));
        Assert.True(cache.TryGet($"k{ReasoningCacheService.MaximumEntries + 9}", out _));
        cache.Set("oversize", new string('x', (int)ReasoningCacheService.MaximumBytes));
        Assert.False(cache.TryGet("oversize", out _));
        for (int i = 0; i < 12; i++)
        {
            cache.Set($"large{i}", new string('x', 400_000));
            clock.Now += TimeSpan.FromMilliseconds(1);
        }
        Assert.False(cache.TryGet("large0", out _));
        Assert.True(cache.TryGet("large11", out _));
        clock.Now += ReasoningCacheService.Lifetime;
        Assert.False(cache.TryGet("large11", out _));
    }

    [Fact]
    public void Cache_ConcurrentWritesRemainBounded()
    {
        ReasoningCacheService cache = new();
        Parallel.For(0, 2000, index => cache.Set($"parallel{index}", new string('x', 100)));
        int retained = Enumerable.Range(0, 2000).Count(index => cache.TryGet($"parallel{index}", out _));
        Assert.InRange(retained, 1, ReasoningCacheService.MaximumEntries);
    }

    [Theory]
    [InlineData(false, false, 200, LogLevel.Information)]
    [InlineData(true, false, 200, LogLevel.Warning)]
    [InlineData(false, true, 200, LogLevel.Warning)]
    [InlineData(false, false, 503, LogLevel.Warning)]
    public async Task RequestLog_OneSafeSummary_WithFinalAttempt(bool fallback, bool retry, int status, LogLevel level)
    {
        CaptureLogger capture = new();
        using ServiceProvider services = new ServiceCollection().AddLogging(logging => logging.AddProvider(capture)).BuildServiceProvider();
        ApplicationBuilder app = new(services);
        app.UseModelRequestLogging();
        using HttpClient client = new();
        ProviderInfo first = new() { Name = "first", BaseUrl = "http://unused", ApiKey = "SECRET-KEY", Client = client };
        ProviderInfo final = new() { Name = "final", BaseUrl = "http://unused", ApiKey = "SECRET-KEY", Client = client };
        app.Run(context =>
        {
            ModelRequestLogging.SetRoute(context, new("requested\nforged", "normalized", null, "model",
                [(first, "upstream"), (final, "last-model")], fallback, RouteErrorKind.None, null), false);
            ModelRequestLogging.Attempt(context, first, "upstream");
            if (retry)
            {
                ModelRequestLogging.Failure(context, 429, "upstream_http");
                ModelRequestLogging.Attempt(context, final, "last-model");
            }
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        DefaultHttpContext context = new() { RequestServices = services, TraceIdentifier = "request-test" };
        context.Request.Path = "/v1/chat/completions";
        context.Request.Headers.Authorization = "Bearer SECRET-AUTH";
        await app.Build()(context);
        var entry = Assert.Single(capture.Entries);
        Assert.Equal(level, entry.Level);
        Assert.Contains("Id=request-test", entry.Message);
        Assert.Contains("Requested=requested_forged", entry.Message);
        Assert.Contains(retry ? "Final=last-model Provider=final" : "Final=upstream Provider=first", entry.Message);
        Assert.DoesNotContain("SECRET", entry.Message);
        Assert.DoesNotContain('\n', entry.Message);
        Assert.Contains(status >= 400 ? "Outcome=failed" : "Outcome=completed", entry.Message);
        Assert.Equal("request-test", context.Response.Headers["X-Request-ID"]);
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CaptureLogger : ILoggerProvider, ILogger
    {
        internal readonly List<(LogLevel Level, string Message)> Entries = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
        public void Dispose() { }
    }
}
