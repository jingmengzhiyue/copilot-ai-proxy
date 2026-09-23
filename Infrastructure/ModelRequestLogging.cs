using System.Diagnostics;

/// <summary>Collects only routing metadata; never inspects request or response bodies.</summary>
internal static class ModelRequestLogging
{
    private static readonly object StateKey = new();

    private sealed class RequestState
    {
        internal ResolvedRoute? Route;
        internal bool Stream;
        internal string Provider = "-";
        internal string Model = "-";
        internal string Error = "none";
        internal int Attempts;
        internal readonly List<string> Failures = [];
    }

    internal static void SetRoute(HttpContext context, ResolvedRoute route, bool stream)
    {
        RequestState state = GetState(context);
        state.Route = route;
        state.Stream = stream;
        if (route.Error != RouteErrorKind.None)
            state.Error = route.Error.ToString();
    }

    internal static void Attempt(HttpContext context, ProviderInfo provider, string upstreamModel)
    {
        RequestState state = GetState(context);
        state.Provider = Safe(provider.Name);
        state.Model = Safe(upstreamModel);
        state.Error = "none";
        state.Attempts++;
    }

    internal static void Failure(HttpContext context, int? upstreamStatus, string errorKind)
    {
        RequestState state = GetState(context);
        state.Error = Safe(errorKind);
        // Bound diagnostics even if a future caller introduces an unbounded retry loop.
        if (state.Failures.Count < 16)
            state.Failures.Add($"{state.Provider}:{upstreamStatus?.ToString() ?? "-"}:{state.Error}");
    }

    internal static IApplicationBuilder UseModelRequestLogging(this IApplicationBuilder app)
    {
        ILogger logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Proxy.Requests");
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path != "/v1/chat/completions" && context.Request.Path != "/api/chat")
            {
                await next(context);
                return;
            }

            long started = Stopwatch.GetTimestamp();
            RequestState state = GetState(context);
            string requestId = Safe(context.TraceIdentifier);
            context.Response.Headers["X-Request-ID"] = requestId;
            bool unhandled = false;
            bool cancelled = false;
            try
            {
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                cancelled = true;
                state.Error = "client_cancelled";
                throw;
            }
            catch (Exception exception)
            {
                unhandled = true;
                // Exception messages can embed URLs, credentials or upstream text.
                state.Error = exception.GetType().Name;
                throw;
            }
            finally
            {
                long elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                int status = unhandled && !context.Response.HasStarted ? 500 : context.Response.StatusCode;
                bool fallback = state.Route?.FallbackUsed == true || state.Attempts > 1;
                bool diagnostic = fallback || status >= 400 || elapsed >= 30_000 || state.Error != "none";
                string outcome = cancelled ? "cancelled" : unhandled || status >= 400 || state.Error != "none"
                    ? "failed" : "completed";
                LogLevel level = diagnostic ? LogLevel.Warning : LogLevel.Information;
                const string summary = "Request Id={RequestId} Requested={RequestedModel} Normalized={NormalizedModel} Final={FinalModel} Provider={Provider} Fallback={FallbackUsed} Status={HttpStatus} ElapsedMs={ElapsedMs} Stream={Stream} Outcome={Outcome}";
                object?[] values = [requestId, Safe(state.Route?.RequestedModel), Safe(state.Route?.NormalizedModel),
                    state.Model, state.Provider, fallback, status, elapsed, state.Stream, outcome];
                if (diagnostic)
                {
                    string candidates = state.Route is null ? "-" : string.Join(",", state.Route.Candidates.Take(16)
                        .Select(c => $"{Safe(c.Provider.Name)}/{Safe(c.UpstreamModel)}"));
                    logger.Log(level, summary + " Attempts={Attempts} Candidates={Candidates} Error={ErrorKind} Failures={Failures} Slow={Slow} ResponseStarted={ResponseStarted}",
                        [.. values, state.Attempts, candidates, state.Error, string.Join(",", state.Failures), elapsed >= 30_000, context.Response.HasStarted]);
                }
                else
                    logger.Log(level, summary, values);
            }
        });
    }

    private static RequestState GetState(HttpContext context)
    {
        if (context.Items.TryGetValue(StateKey, out object? value) && value is RequestState state)
            return state;
        RequestState created = new();
        context.Items[StateKey] = created;
        return created;
    }

    private static string Safe(string? value) => string.IsNullOrEmpty(value) ? "-" :
        new string(value.Take(160).Select(c => char.IsControl(c) ? '_' : c).ToArray());
}
