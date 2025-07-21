using System.Text.Json;

namespace NArk.Tests;

public static class Json
{
    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }
}
