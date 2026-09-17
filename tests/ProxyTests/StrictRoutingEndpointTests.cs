using System.Net;
using System.Text.Json;

namespace ProxyTests;

/// <summary>
/// Endpoint-level tests for strict "model@provider" routing and request-body
/// validation on /v1/chat/completions, exercised through the shared
/// <see cref="ProxyFixture"/> (in-memory stub provider standing in for DeepSeek).
/// </summary>
[Collection("Proxy")]
public class StrictRoutingEndpointTests(ProxyFixture fixture)
{
    private readonly HttpClient _client = fixture.Client;

    private static StringContent ChatBody(string model) => new(
        $$"""{"model":{{JsonSerializer.Serialize(model)}},"messages":[{"role":"user","content":"hi"}],"stream":false}""",
        System.Text.Encoding.UTF8, "application/json");

    // ── Invalid JSON → 400 (never 500) ────────────────────────────────────

    [Fact]
    public async Task V1Chat_InvalidJson_Returns400WithInvalidJsonCode()
    {
        using StringContent body = new("this is not json",
            System.Text.Encoding.UTF8, "application/json");

        using HttpResponseMessage r = await _client.PostAsync("/v1/chat/completions", body);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        string resp = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(resp);
        JsonElement error = d.RootElement.GetProperty("error");
        Assert.Equal("invalid_json", error.GetProperty("code").GetString());
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task V1Chat_NonObjectJson_Returns400()
    {
        using StringContent body = new("[1,2,3]",
            System.Text.Encoding.UTF8, "application/json");

        using HttpResponseMessage r = await _client.PostAsync("/v1/chat/completions", body);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        string resp = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(resp);
        Assert.Equal("invalid_json", d.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Explicit provider: strict routing ─────────────────────────────────

    [Fact]
    public async Task V1Chat_UnknownExplicitProvider_Returns404WithoutFallback()
    {
        using StringContent body = ChatBody("kimi-for-coding@invalid-provider");

        using HttpResponseMessage r = await _client.PostAsync("/v1/chat/completions", body);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        // Diagnostics must show the strict attempt, never a silent default-model fallback.
        Assert.Equal("kimi-for-coding@invalid-provider", r.Headers.GetValues("X-Proxy-Requested-Model").Single());
        Assert.Equal("kimi-for-coding", r.Headers.GetValues("X-Proxy-Resolved-Model").Single());
        Assert.Equal("invalid-provider", r.Headers.GetValues("X-Proxy-Provider-Hint").Single());
        Assert.Equal("0", r.Headers.GetValues("X-Proxy-Candidate-Count").Single());
        Assert.Equal("false", r.Headers.GetValues("X-Proxy-Fallback-Used").Single());
        Assert.False(r.Headers.Contains("X-Proxy-Primary-Provider"));

        string resp = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(resp);
        JsonElement error = d.RootElement.GetProperty("error");
        Assert.Equal("provider_not_found", error.GetProperty("code").GetString());
        Assert.Contains("invalid-provider", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task V1Chat_UnknownModelAtKnownProvider_Returns404WithoutFallback()
    {
        using StringContent body = ChatBody("does-not-exist@deepseek");

        using HttpResponseMessage r = await _client.PostAsync("/v1/chat/completions", body);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("deepseek", r.Headers.GetValues("X-Proxy-Provider-Hint").Single());
        Assert.Equal("0", r.Headers.GetValues("X-Proxy-Candidate-Count").Single());
        Assert.Equal("false", r.Headers.GetValues("X-Proxy-Fallback-Used").Single());

        string resp = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(resp);
        Assert.Equal("model_not_found", d.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task V1Chat_ExplicitProvider_StrictRoute_SucceedsWithDiagnostics()
    {
        using StringContent body = ChatBody("deepseek-v4-pro@deepseek");

        using HttpResponseMessage r = await _client.PostAsync("/v1/chat/completions", body);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("deepseek-v4-pro@deepseek", r.Headers.GetValues("X-Proxy-Requested-Model").Single());
        Assert.Equal("deepseek-v4-pro", r.Headers.GetValues("X-Proxy-Resolved-Model").Single());
        Assert.Equal("deepseek", r.Headers.GetValues("X-Proxy-Provider-Hint").Single());
        Assert.Equal("1", r.Headers.GetValues("X-Proxy-Candidate-Count").Single());
        Assert.Equal("deepseek", r.Headers.GetValues("X-Proxy-Primary-Provider").Single());
        Assert.Equal("deepseek-v4-pro", r.Headers.GetValues("X-Proxy-Primary-Upstream").Single());
        Assert.Equal("false", r.Headers.GetValues("X-Proxy-Fallback-Used").Single());
    }

    [Fact]
    public async Task V1Chat_UnknownBareModel_FallsBackToDefaultModel_Flagged()
    {
        using StringContent body = ChatBody("totally-unknown-model");

        using HttpResponseMessage r = await _client.PostAsync("/v1/chat/completions", body);

        // Legacy behaviour preserved (BYOM clients send arbitrary ids), but the
        // fallback is now explicit in the diagnostics.
        Assert.Equal("true", r.Headers.GetValues("X-Proxy-Fallback-Used").Single());
        Assert.False(r.Headers.Contains("X-Proxy-Provider-Hint"));
    }

    // ── Round-trip: everything /api/tags emits must route back ────────────

    [Fact]
    public async Task ApiTags_EveryEmittedNameModelAndAlias_RoundTripsWithoutFallback()
    {
        using HttpResponseMessage tagsResp = await _client.GetAsync("/api/tags");
        tagsResp.EnsureSuccessStatusCode();
        using JsonDocument tags = JsonDocument.Parse(await tagsResp.Content.ReadAsStringAsync());
        List<JsonElement> entries = tags.RootElement.GetProperty("models").EnumerateArray().ToList();
        Assert.NotEmpty(entries);

        foreach (JsonElement m in entries)
        {
            string name = m.GetProperty("name").GetString()!;
            string model = m.GetProperty("model").GetString()!;
            string[] aliases = m.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()!).ToArray();

            foreach (string id in new[] { name, model }.Concat(aliases).Distinct())
            {
                using StringContent body = ChatBody(id);
                using HttpResponseMessage r = await _client.PostAsync("/v1/chat/completions", body);

                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                Assert.Equal("false", r.Headers.GetValues("X-Proxy-Fallback-Used").Single());
                Assert.Equal("deepseek", r.Headers.GetValues("X-Proxy-Primary-Provider").Single());
                Assert.DoesNotContain(":latest", r.Headers.GetValues("X-Proxy-Primary-Upstream").Single());
            }
        }
    }

    [Fact]
    public async Task ApiShow_AcceptsEveryTagsDisplayName()
    {
        using HttpResponseMessage tagsResp = await _client.GetAsync("/api/tags");
        tagsResp.EnsureSuccessStatusCode();
        using JsonDocument tags = JsonDocument.Parse(await tagsResp.Content.ReadAsStringAsync());

        foreach (JsonElement m in tags.RootElement.GetProperty("models").EnumerateArray())
        {
            string name = m.GetProperty("name").GetString()!;
            using StringContent body = new(
                $$"""{"model":{{JsonSerializer.Serialize(name)}}}""",
                System.Text.Encoding.UTF8, "application/json");

            using HttpResponseMessage r = await _client.PostAsync("/api/show", body);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
    }

    // ── /api/chat shares the same strict routing ──────────────────────────

    [Fact]
    public async Task ApiChat_InvalidJson_Returns400()
    {
        using StringContent body = new("{ not json",
            System.Text.Encoding.UTF8, "application/json");

        using HttpResponseMessage r = await _client.PostAsync("/api/chat", body);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task ApiChat_UnknownExplicitProvider_Returns404()
    {
        using StringContent body = ChatBody("kimi-for-coding@invalid-provider");

        using HttpResponseMessage r = await _client.PostAsync("/api/chat", body);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
