[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ProxyTests")]

// Cargar .env automáticamente si existe
string envPath = Path.Combine(AppContext.BaseDirectory, ".env");
if (!File.Exists(envPath))
{
    envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
}
if (File.Exists(envPath))
{
    Console.WriteLine($"📄 Cargando configuración desde: {envPath}");
    DotNetEnv.Env.Load(envPath);
}

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

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

WebApplication app = builder.Build();
app.UseOptionalProxyAuthentication(proxyApiKey);

ModelCatalogService modelCatalog = app.Services.GetRequiredService<ModelCatalogService>();
ProviderRegistry providerRegistry = app.Services.GetRequiredService<ProviderRegistry>();
await modelCatalog.RefreshAvailableModels(CancellationToken.None);

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
