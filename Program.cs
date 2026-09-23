[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ProxyTests")]

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
DotEnvLoader.Load(AppContext.BaseDirectory, Directory.GetCurrentDirectory(), builder.Environment.IsEnvironment("Testing"));

// Keep framework warnings/errors while replacing repetitive lifecycle information
// with one application-level model-request summary.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

int port = int.TryParse(Environment.GetEnvironmentVariable("PROXY_PORT"), out int p) ? p : 11434;
string? proxyApiKey = Environment.GetEnvironmentVariable("PROXY_API_KEY");

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSingleton<ProviderHttpClientFactory>();
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<ModelSelectionStore>();
builder.Services.AddSingleton<ModelCatalogService>();
builder.Services.AddSingleton<ReasoningCacheService>();
builder.Services.AddSingleton<RequestTransformer>();
builder.Services.AddSingleton<OllamaResponseBuilder>();
builder.Services.AddSingleton<ChatStreamingService>();

builder.Services.AddHostedService<ProviderBenchmarkService>();
builder.Services.AddHostedService<ModelCatalogRefreshService>();

WebApplication app = builder.Build();
app.UseModelRequestLogging();
app.UseOptionalProxyAuthentication(proxyApiKey);

ModelCatalogService modelCatalog = app.Services.GetRequiredService<ModelCatalogService>();
ProviderRegistry providerRegistry = app.Services.GetRequiredService<ProviderRegistry>();

app.MapOpenAiEndpoints();
app.MapOllamaEndpoints();
app.MapHealthEndpoints();

Console.WriteLine($"╔══════════════════════════════════════════════════════════════════╗");
Console.WriteLine($"║   DeepSeek / Multi-Provider Copilot Proxy (Ultra)               ║");
Console.WriteLine($"╠══════════════════════════════════════════════════════════════════╣");
Console.WriteLine($"║  Version: 2026.06.02                                             ║");
Console.WriteLine($"║  Default: {providerRegistry.DefaultModel,-32}                                  ║");
Console.WriteLine($"║  Providers: {string.Join(", ", providerRegistry.Providers.Select(pv => pv.Name)),-32}                          ║");
Console.WriteLine($"║  Models:   {string.Join(", ", modelCatalog.AvailableModels),-32}                          ║");
Console.WriteLine($"║  URL:     http://localhost:{port}/v1                             ║");
Console.WriteLine($"║  Auth:    {(string.IsNullOrEmpty(proxyApiKey) ? "open (no key set)" : "required (PROXY_API_KEY)"),-18} ║");
Console.WriteLine($"╚══════════════════════════════════════════════════════════════════╝");

app.Run();

public partial class Program { }
