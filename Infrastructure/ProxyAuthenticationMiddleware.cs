using System.Security.Cryptography;
using System.Text;

internal static class ProxyAuthenticationMiddleware
{
    internal static IApplicationBuilder UseOptionalProxyAuthentication(this IApplicationBuilder app, string? proxyApiKey)
    {
        if (string.IsNullOrEmpty(proxyApiKey))
        {
            return app;
        }

        byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(proxyApiKey));
        app.Use(async (ctx, next) =>
        {
            string? auth = ctx.Request.Headers.Authorization;
            if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(auth["Bearer ".Length..])), expectedHash))
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
