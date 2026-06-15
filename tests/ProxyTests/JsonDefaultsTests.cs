using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyTests;

public class JsonDefaultsTests
{
    private record TestPayload(
        string? UserName = null,
        int? Age = null,
        string? OptionalField = null
    );

    [Fact]
    public void SnakeCase_SerializesPropertiesInSnakeCase()
    {
        TestPayload payload = new(UserName: "John Doe", Age: 30);

        string json = JsonSerializer.Serialize(payload, JsonDefaults.SnakeCase);

        Assert.Contains("\"user_name\"", json);
        Assert.Contains("\"age\"", json);
    }

    [Fact]
    public void SnakeCase_DoesNotSerializeNullValues()
    {
        TestPayload payload = new(UserName: "John");

        string json = JsonSerializer.Serialize(payload, JsonDefaults.SnakeCase);

        Assert.Contains("\"user_name\"", json);
        Assert.DoesNotContain("optional_field", json);
    }

    [Fact]
    public void SnakeCase_SerializesNonNullValues()
    {
        TestPayload payload = new(UserName: "Jane", Age: 25, OptionalField: "present");

        string json = JsonSerializer.Serialize(payload, JsonDefaults.SnakeCase);

        Assert.Contains("\"user_name\":\"Jane\"", json);
        Assert.Contains("\"age\":25", json);
        Assert.Contains("\"optional_field\":\"present\"", json);
    }

    [Fact]
    public void SnakeCase_DeserializesSnakeCaseJson()
    {
        const string json = """{ "user_name": "Bob", "age": 40 }""";

        TestPayload? result = JsonSerializer.Deserialize<TestPayload>(json, JsonDefaults.SnakeCase);

        Assert.NotNull(result);
        Assert.Equal("Bob", result.UserName);
        Assert.Equal(40, result.Age);
    }

    [Fact]
    public void SnakeCase_DeserializesMissingPropertiesAsNull()
    {
        const string json = """{ "user_name": "Alice" }""";

        TestPayload? result = JsonSerializer.Deserialize<TestPayload>(json, JsonDefaults.SnakeCase);

        Assert.NotNull(result);
        Assert.Equal("Alice", result.UserName);
        Assert.Null(result.Age);
        Assert.Null(result.OptionalField);
    }

    [Fact]
    public void SnakeCase_IsConfiguredWithSnakeCaseLowerPolicy()
    {
        Assert.NotNull(JsonDefaults.SnakeCase.PropertyNamingPolicy);
        Assert.Equal(JsonNamingPolicy.SnakeCaseLower, JsonDefaults.SnakeCase.PropertyNamingPolicy);
    }

    [Fact]
    public void SnakeCase_IsConfiguredToIgnoreNullValues()
    {
        Assert.Equal(JsonIgnoreCondition.WhenWritingNull, JsonDefaults.SnakeCase.DefaultIgnoreCondition);
    }

    [Fact]
    public void SnakeCase_RoundTrip_PreservesData()
    {
        TestPayload original = new(UserName: "Test User", Age: 99, OptionalField: "extra");

        string json = JsonSerializer.Serialize(original, JsonDefaults.SnakeCase);
        TestPayload? deserialized = JsonSerializer.Deserialize<TestPayload>(json, JsonDefaults.SnakeCase);

        Assert.NotNull(deserialized);
        Assert.Equal(original.UserName, deserialized.UserName);
        Assert.Equal(original.Age, deserialized.Age);
        Assert.Equal(original.OptionalField, deserialized.OptionalField);
    }

    [Fact]
    public void SnakeCase_SerializesNestedObjects()
    {
        NestedPayload payload = new(
            OuterName: "parent",
            Child: new ChildPayload(InnerName: "child", InnerValue: 42)
        );

        string json = JsonSerializer.Serialize(payload, JsonDefaults.SnakeCase);

        Assert.Contains("\"outer_name\":\"parent\"", json);
        Assert.Contains("\"inner_name\":\"child\"", json);
        Assert.Contains("\"inner_value\":42", json);
    }

    private record ChildPayload(string InnerName, int InnerValue);
    private record NestedPayload(string OuterName, ChildPayload Child);
}