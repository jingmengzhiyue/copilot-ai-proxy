/// <summary>
/// Why a route could not be resolved.
/// </summary>
internal enum RouteErrorKind
{
    /// <summary>Resolution succeeded.</summary>
    None,

    /// <summary>An explicit provider hint ("@provider", "provider/model", or the
    /// "PROVIDER - model" display alias) named a provider that is not configured
    /// in this proxy.</summary>
    ProviderNotFound,

    /// <summary>The provider hinted by the request is configured, but it neither
    /// discovered nor declares the requested model.</summary>
    ModelNotFound
}

/// <summary>
/// Result of resolving a client-requested model id into an ordered list of
/// (provider, upstreamModel) candidates. All endpoints share this resolution via
/// <see cref="ModelCatalogService.ResolveRoute"/>; no endpoint keeps its own parsing.
/// </summary>
/// <param name="RequestedModel">The model id exactly as the client sent it.</param>
/// <param name="NormalizedModel">The result of alias normalization: the canonical
/// model id with any "@provider" hint, "PROVIDER - " display prefix and Ollama
/// ":tag" suffix removed. When nothing resolves, this is the requested id itself —
/// never silently replaced by the default model.</param>
/// <param name="ProviderHint">The explicit provider hint when the request carried
/// one ("model@provider", "provider/model", "PROVIDER - model"); canonical provider
/// name once resolved, verbatim hint when resolution failed.</param>
/// <param name="Model">The effective model the proxy serves: <paramref name="NormalizedModel"/>
/// normally, the configured default model when <paramref name="FallbackUsed"/> is true.
/// Used for execution-config / timeout lookups.</param>
/// <param name="Candidates">Ordered failover chain. An explicit provider hint always
/// yields at most one candidate — strict routing never fails over to another provider.</param>
/// <param name="FallbackUsed">True when the requested model could not be resolved
/// anywhere and the configured default model was used instead.</param>
/// <param name="Error">Resolution error kind; <see cref="RouteErrorKind.None"/> on success.</param>
/// <param name="ErrorMessage">Human-readable detail when <paramref name="Error"/> is set.</param>
internal sealed record ResolvedRoute(
    string RequestedModel,
    string NormalizedModel,
    string? ProviderHint,
    string Model,
    IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> Candidates,
    bool FallbackUsed,
    RouteErrorKind Error,
    string? ErrorMessage);
