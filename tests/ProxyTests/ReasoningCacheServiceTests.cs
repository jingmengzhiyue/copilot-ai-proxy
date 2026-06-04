using System.Text.Json;

namespace ProxyTests;

public class ReasoningCacheServiceTests
{
    [Fact]
    public void TryGet_ExistingKey_ReturnsTrueAndValue()
    {
        ReasoningCacheService sut = new();
        sut.Set("key1", "value1");

        bool found = sut.TryGet("key1", out string? value);

        Assert.True(found);
        Assert.Equal("value1", value);
    }

    [Fact]
    public void TryGet_NonExistingKey_ReturnsFalseAndNull()
    {
        ReasoningCacheService sut = new();

        bool found = sut.TryGet("missing", out string? value);

        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void NextAssistantKey_ReturnsSequentialKeys()
    {
        ReasoningCacheService sut = new();

        string key1 = sut.NextAssistantKey();
        string key2 = sut.NextAssistantKey();
        string key3 = sut.NextAssistantKey();

        Assert.Equal("assistant:0", key1);
        Assert.Equal("assistant:1", key2);
        Assert.Equal("assistant:2", key3);
    }

    [Fact]
    public void NextAssistantKey_IsolatedPerInstance()
    {
        ReasoningCacheService sut1 = new();
        ReasoningCacheService sut2 = new();

        string key1 = sut1.NextAssistantKey();
        string key2 = sut2.NextAssistantKey();

        Assert.Equal("assistant:0", key1);
        Assert.Equal("assistant:0", key2);
    }

    [Fact]
    public void CacheReasoningFromResponse_WithMessageReasoning_CachesByAssistantKey()
    {
        ReasoningCacheService sut = new();
        string json = """
            {
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": "hello",
                    "reasoning_content": "think deeply"
                  }
                }
              ]
            }
            """;

        sut.CacheReasoningFromResponse(json);

        Assert.True(sut.TryGet("assistant:0", out string? value));
        Assert.Equal("think deeply", value);
    }

    [Fact]
    public void CacheReasoningFromResponse_WithDeltaReasoning_CachesByAssistantKey()
    {
        ReasoningCacheService sut = new();
        string json = """
            {
              "choices": [
                {
                  "delta": {
                    "reasoning_content": "streaming thought"
                  }
                }
              ]
            }
            """;

        sut.CacheReasoningFromResponse(json);

        Assert.True(sut.TryGet("assistant:0", out string? value));
        Assert.Equal("streaming thought", value);
    }

    [Fact]
    public void CacheReasoningFromResponse_WithToolCalls_CachesByToolCallKey()
    {
        ReasoningCacheService sut = new();
        string json = """
            {
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": "",
                    "reasoning_content": "tool reasoning",
                    "tool_calls": [
                      { "id": "call_1", "type": "function", "function": { "name": "test" } }
                    ]
                  }
                }
              ]
            }
            """;

        sut.CacheReasoningFromResponse(json);

        Assert.True(sut.TryGet("toolcall:call_1", out string? value));
        Assert.Equal("tool reasoning", value);
    }

    [Fact]
    public void CacheReasoningFromResponse_WithMultipleToolCalls_CachesByCombinedKey()
    {
        ReasoningCacheService sut = new();
        string json = """
            {
              "choices": [
                {
                  "message": {
                    "reasoning_content": "multi tool reasoning",
                    "tool_calls": [
                      { "id": "call_1" },
                      { "id": "call_2" }
                    ]
                  }
                }
              ]
            }
            """;

        sut.CacheReasoningFromResponse(json);

        Assert.True(sut.TryGet("toolcall:call_1|call_2", out string? value));
        Assert.Equal("multi tool reasoning", value);
    }

    [Fact]
    public void CacheReasoningFromResponse_MissingChoices_DoesNotCache()
    {
        ReasoningCacheService sut = new();
        string json = """{ "id": "test" }""";

        sut.CacheReasoningFromResponse(json);

        Assert.False(sut.TryGet("assistant:0", out _));
    }

    [Fact]
    public void CacheReasoningFromResponse_EmptyChoices_DoesNotCache()
    {
        ReasoningCacheService sut = new();
        string json = """{ "choices": [] }""";

        sut.CacheReasoningFromResponse(json);

        Assert.False(sut.TryGet("assistant:0", out _));
    }

    [Fact]
    public void CacheReasoningFromResponse_MissingMessageAndDelta_DoesNotCache()
    {
        ReasoningCacheService sut = new();
        string json = """
            {
              "choices": [
                {
                  "index": 0,
                  "finish_reason": "stop"
                }
              ]
            }
            """;

        sut.CacheReasoningFromResponse(json);

        Assert.False(sut.TryGet("assistant:0", out _));
    }

    [Fact]
    public void CacheReasoningFromResponse_MissingReasoningContent_DoesNotCache()
    {
        ReasoningCacheService sut = new();
        string json = """
            {
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": "hello"
                  }
                }
              ]
            }
            """;

        sut.CacheReasoningFromResponse(json);

        Assert.False(sut.TryGet("assistant:0", out _));
    }

    [Fact]
    public void CacheReasoningFromResponse_EmptyReasoningContent_DoesNotCache()
    {
        ReasoningCacheService sut = new();
        string json = """
            {
              "choices": [
                {
                  "message": {
                    "reasoning_content": ""
                  }
                }
              ]
            }
            """;

        sut.CacheReasoningFromResponse(json);

        Assert.False(sut.TryGet("assistant:0", out _));
    }

    [Fact]
    public void CacheReasoningFromResponse_InvalidJson_DoesNotThrow()
    {
        ReasoningCacheService sut = new();

        Exception? ex = Record.Exception(() => sut.CacheReasoningFromResponse("not json"));

        Assert.Null(ex);
    }

    [Fact]
    public void CacheReasoningFromResponse_ToolCallsWithoutId_SkipsMissingIds()
    {
        ReasoningCacheService sut = new();
        string json = """
            {
              "choices": [
                {
                  "message": {
                    "reasoning_content": "partial tool reasoning",
                    "tool_calls": [
                      { "type": "function" },
                      { "id": "call_valid" }
                    ]
                  }
                }
              ]
            }
            """;

        sut.CacheReasoningFromResponse(json);

        Assert.True(sut.TryGet("toolcall:call_valid", out string? value));
        Assert.Equal("partial tool reasoning", value);
    }

    [Fact]
    public void Set_OverwritesExistingValue()
    {
        ReasoningCacheService sut = new();
        sut.Set("key1", "original");
        sut.Set("key1", "updated");

        Assert.True(sut.TryGet("key1", out string? value));
        Assert.Equal("updated", value);
    }

    [Fact]
    public void CacheReasoningFromResponse_SequentialCalls_IncrementAssistantKeys()
    {
        ReasoningCacheService sut = new();
        string json = """
            {
              "choices": [
                {
                  "message": {
                    "reasoning_content": "thought"
                  }
                }
              ]
            }
            """;

        sut.CacheReasoningFromResponse(json);
        sut.CacheReasoningFromResponse(json);
        sut.CacheReasoningFromResponse(json);

        Assert.True(sut.TryGet("assistant:0", out _));
        Assert.True(sut.TryGet("assistant:1", out _));
        Assert.True(sut.TryGet("assistant:2", out _));
    }

    [Fact]
    public void CacheReasoningFromResponse_ToolCallWithEmptyIds_FallsBackToAssistantKey()
    {
        ReasoningCacheService sut = new();
        string json = """
            {
              "choices": [
                {
                  "message": {
                    "reasoning_content": "fallback reasoning",
                    "tool_calls": [
                      { "type": "function" }
                    ]
                  }
                }
              ]
            }
            """;

        sut.CacheReasoningFromResponse(json);

        Assert.True(sut.TryGet("assistant:0", out string? value));
        Assert.Equal("fallback reasoning", value);
    }
}