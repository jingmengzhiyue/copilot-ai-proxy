using System.Net;
using System.Net.Http.Headers;

internal sealed class ProviderHttpClientFactory : IDisposable
{
    private readonly HttpMessageHandler _sharedHandler;
    private readonly List<HttpClient> _clients = [];
    private bool _disposed;

    public ProviderHttpClientFactory() : this(NewDefaultHandler()) { }

    // Test seam: pass a custom HttpMessageHandler (e.g., a DelegatingHandler) so
    // unit tests can drive provider HTTP responses without touching the network.
    // Production code uses the parameterless ctor.
    internal ProviderHttpClientFactory(HttpMessageHandler sharedHandler)
    {
        _sharedHandler = sharedHandler;
    }

    private static SocketsHttpHandler NewDefaultHandler() => new()
    {
        // HTTP/2 with SSE streaming can cause premature connection termination.
        // Prefer HTTP/1.1 which handles long-lived streaming connections more reliably.
        MaxConnectionsPerServer = 256,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(120),
        KeepAlivePingDelay = TimeSpan.FromSeconds(60),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        PreAuthenticate = false
    };

    internal HttpClient CreateProviderClient(string providerName, string baseUrl, string apiKey) =>
        CreateProviderClient(providerName, baseUrl, apiKey, _sharedHandler);

    internal HttpClient CreateProviderClient(string providerName, string baseUrl, string apiKey, HttpMessageHandler handler)
    {
        // Normalize: HttpClient resolves a relative Uri against the *last segment*
        // of BaseAddress, not the end of the string. "https://api.groq.com/openai"
        // + "v1/chat/completions" would otherwise become "https://api.groq.com/v1/...".
        string normalizedBase = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";

        HttpClient client = new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMinutes(5),
            BaseAddress = new Uri(normalizedBase)
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (providerName.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
        {
            string? referer = Environment.GetEnvironmentVariable("PROVIDER_OPENROUTER_REFERER")
                ?? Environment.GetEnvironmentVariable("OPENROUTER_HTTP_REFERER");
            string? title = Environment.GetEnvironmentVariable("PROVIDER_OPENROUTER_TITLE")
                ?? Environment.GetEnvironmentVariable("OPENROUTER_X_TITLE");

            if (!string.IsNullOrWhiteSpace(referer))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", referer);
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", title);
            }
        }

        lock (_clients)
        {
            if (_disposed)
            {
                client.Dispose();
                throw new ObjectDisposedException(nameof(ProviderHttpClientFactory));
            }
            _clients.Add(client);
        }
        return client;
    }

    // DI owns the factory. Clients borrow the shared handler, which is released once
    // after all clients when the host stops. Custom per-call handlers stay caller-owned.
    public void Dispose()
    {
        lock (_clients)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (HttpClient client in _clients) client.Dispose();
            _clients.Clear();
            _sharedHandler.Dispose();
        }
    }
}
