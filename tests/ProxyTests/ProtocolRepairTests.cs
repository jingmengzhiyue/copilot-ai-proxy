using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace ProxyTests;

public sealed class ProtocolRepairTests
{
    private static string Convert(string method, string body) => (string)typeof(OpenAiEndpoints)
        .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, method == "BuildOllamaChatRequest" ? [body, "test", false] : [body, "test"])!;

    [Fact]
    public void ToolHistory_PreservesRoleAndConvertsArguments()
    {
        string body = """{"messages":[{"role":"assistant","tool_calls":[{"id":"call_a","type":"function","function":{"name":"echo","arguments":"{\"x\":1}"}}]},{"role":"tool","tool_call_id":"call_a","content":"result"}]}""";
        using var doc = JsonDocument.Parse(Convert("BuildOllamaChatRequest", body));
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal("tool", messages[1].GetProperty("role").GetString());
        Assert.Equal("echo", messages[1].GetProperty("tool_name").GetString());
        Assert.Equal(JsonValueKind.Object, messages[0].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").ValueKind);
    }

    [Fact]
    public void NativeTools_ReceiveOpenAiIdsAndStringArguments()
    {
        using var doc = JsonDocument.Parse(Convert("ConvertOllamaChatToOpenAiCompletion", """{"message":{"tool_calls":[{"function":{"name":"echo","arguments":{"nested":{"x":1}}}}]},"done":true}"""));
        var choice = doc.RootElement.GetProperty("choices")[0];
        var call = choice.GetProperty("message").GetProperty("tool_calls")[0];
        Assert.StartsWith("call_", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, call.GetProperty("function").GetProperty("arguments").ValueKind);
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void ImageDataUrl_ExtractsBase64Payload()
    {
        using var doc = JsonDocument.Parse(Convert("BuildOllamaChatRequest", """{"messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,YQ=="}}]}]}"""));
        Assert.Equal("YQ==", doc.RootElement.GetProperty("messages")[0].GetProperty("images")[0].GetString());
    }

    [Fact]
    public async Task TruncatedSse_DoesNotProduceSuccessfulDone()
    {
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n") };
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var service = new ChatStreamingService(new ReasoningCacheService());
        try { await service.StreamOllamaAndCache(upstream, ctx.Response, "test", CancellationToken.None); }
        catch (IOException) { }
        Assert.DoesNotContain("\"done\":true", Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()));
    }

    [Fact]
    public async Task InterleavedToolFragments_AreEmittedOnlyAsCompleteObjects()
    {
        string[] data =
        [
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"echo","arguments":"{\"nested\":"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"name":"other","arguments":"{\"text\":\"a"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"x\":1}"}},{"index":1,"function":{"arguments":"b\"}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"}"}}]},"finish_reason":"tool_calls"}]}"""
        ];
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Join("\n\n", data.Select(x => "data: " + x)) + "\n\ndata: [DONE]\n\n") };
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        await new ChatStreamingService(new ReasoningCacheService()).StreamOllamaAndCache(upstream, ctx.Response, "test", default);
        string output = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        using var final = JsonDocument.Parse(output.Trim());
        var calls = final.RootElement.GetProperty("message").GetProperty("tool_calls");
        Assert.Equal(2, calls.GetArrayLength());
        Assert.Equal(1, calls[0].GetProperty("function").GetProperty("arguments").GetProperty("nested").GetProperty("x").GetInt32());
        Assert.Equal("ab", calls[1].GetProperty("function").GetProperty("arguments").GetProperty("text").GetString());
        Assert.True(final.RootElement.GetProperty("done").GetBoolean());
    }

    [Theory]
    [InlineData("data: {broken\n\n")]
    [InlineData("data: {\"error\":{\"message\":\"synthetic\"}}\n\n")]
    public async Task InvalidSse_NeverProducesSuccessfulDone(string stream)
    {
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream) };
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        await Assert.ThrowsAnyAsync<Exception>(() => new ChatStreamingService(new ReasoningCacheService()).StreamOllamaAndCache(upstream, ctx.Response, "test", default));
        Assert.DoesNotContain("\"done\":true", Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()));
    }

    [Fact]
    public void OptionalOpenAiNumbers_KeepLegalNullSemantics()
    {
        using var doc = JsonDocument.Parse("""{"messages":[{"role":"assistant","content":null}],"temperature":null,"top_p":null,"max_tokens":null,"seed":null,"stop":null}""");
        Assert.Null(ChatProtocol.ValidateRequest(doc.RootElement, ollama: false));
    }
}
