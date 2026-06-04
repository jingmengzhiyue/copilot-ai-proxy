using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ProxyTests;

public class ProxyAuthenticationMiddlewareTests
{
    [Fact]
    public async Task UseOptionalProxyAuthentication_WithNullKey_SkipsAuthentication()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication(null);
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
                }));

        HttpResponseMessage response = await server.CreateClient().GetAsync("/");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("ok", body);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_WithEmptyKey_SkipsAuthentication()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("");
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
                }));

        HttpResponseMessage response = await server.CreateClient().GetAsync("/");

        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_WithValidKey_AndValidBearer_Returns200()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("secret-key-123");
                    app.Run(ctx => ctx.Response.WriteAsync("authorized"));
                }));

        HttpClient client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-key-123");

        HttpResponseMessage response = await client.GetAsync("/");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("authorized", body);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_WithValidKey_AndInvalidBearer_Returns401()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("secret-key-123");
                    app.Run(ctx => ctx.Response.WriteAsync("authorized"));
                }));

        HttpClient client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-key");

        HttpResponseMessage response = await client.GetAsync("/");

        Assert.Equal(401, (int)response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unauthorized", body);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_WithValidKey_AndMissingBearer_Returns401()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("secret-key-123");
                    app.Run(ctx => ctx.Response.WriteAsync("authorized"));
                }));

        HttpResponseMessage response = await server.CreateClient().GetAsync("/");

        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_WithValidKey_AndNonBearerAuth_Returns401()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("secret-key-123");
                    app.Run(ctx => ctx.Response.WriteAsync("authorized"));
                }));

        HttpClient client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "c2VjcmV0LWtleS0xMjM=");

        HttpResponseMessage response = await client.GetAsync("/");

        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_ResponseContentType_IsJson()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("secret-key-123");
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
                }));

        HttpResponseMessage response = await server.CreateClient().GetAsync("/");

        Assert.Equal(401, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_BearerPrefix_IsCaseInsensitive()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("secret-key");
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
                }));

        HttpClient client = server.CreateClient();
        // Manually set header with lowercase "bearer"
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "bearer secret-key");

        HttpResponseMessage response = await client.GetAsync("/");

        Assert.Equal(200, (int)response.StatusCode);
    }
}