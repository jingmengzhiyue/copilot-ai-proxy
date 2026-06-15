internal static class ProxyAuthenticationMiddleware
{
    internal static IApplicationBuilder UseOptionalProxyAuthentication(this IApplicationBuilder app, string? proxyApiKey)
    {
        if (string.IsNullOrEmpty(proxyApiKey))
        {
            return app;
        }

        app.Use(async (ctx, next) =>
        {
            string? auth = ctx.Request.Headers.Authorization;
            if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && auth["Bearer ".Length..] == proxyApiKey)
            {
                await next();
            }
            else
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{"error":"unauthorized"}""");
            }
        });

        return app;
    }
}
