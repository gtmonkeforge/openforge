using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace OpenForge.Infrastructure;

public static class JsonBody
{
    public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task<JsonNode?> ReadAsync(HttpRequest request)
    {
        string mediaType = request.ContentType?.Split(';')[0].Trim() ?? "";

        if (!mediaType.EndsWith("json", System.StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return await JsonNode.ParseAsync(request.Body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<JsonObject?> ReadObjectAsync(HttpRequest request)
    {
        return await ReadAsync(request) as JsonObject;
    }

    public static async Task<JsonObject> RequireObjectAsync(HttpRequest request, string message)
    {
        return await ReadObjectAsync(request) ?? throw ApiException.BadRequest(message);
    }

    public static JsonObject? ParseObject(string? text)
    {
        if (text == null)
            return null;

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static HashSet<string> Keys(this JsonObject obj)
    {
        return obj.Select(pair => pair.Key).ToHashSet();
    }

    public static bool HasOnly(this JsonObject obj, params string[] allowed)
    {
        return obj.Keys().IsSubsetOf(allowed);
    }

    public static bool HasExactly(this JsonObject obj, params string[] keys)
    {
        return obj.Keys().SetEquals(keys);
    }

    public static bool IsString(this JsonNode? node, out string value)
    {
        value = "";

        if (node is not JsonValue json || json.GetValueKind() != JsonValueKind.String)
            return false;

        value = json.GetValue<string>();
        return true;
    }

    public static bool IsBool(this JsonNode? node, out bool value)
    {
        JsonValueKind? kind = node?.GetValueKind();
        value = kind == JsonValueKind.True;

        return kind is JsonValueKind.True or JsonValueKind.False;
    }

    public static bool IsInt(this JsonNode? node, out long value)
    {
        value = 0;

        if (node is not JsonValue json || json.GetValueKind() != JsonValueKind.Number)
            return false;

        string raw = json.ToJsonString();
        return raw.IndexOfAny(['.', 'e', 'E']) < 0 && long.TryParse(raw, out value);
    }

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    public static List<string> StringList(string? json)
    {
        if (json == null)
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool SameJson(string? a, string? b)
    {
        if (a == b)
            return true;

        try
        {
            return JsonNode.DeepEquals(a == null ? null : JsonNode.Parse(a), b == null ? null : JsonNode.Parse(b));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
