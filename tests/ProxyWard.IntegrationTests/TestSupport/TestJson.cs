using System.Net;
using System.Text;
using System.Text.Json;

namespace ProxyWard.IntegrationTests;

internal static class TestJson
{
    public static StringContent Content(string json) =>
        new(json, Encoding.UTF8, "application/json");

    public static async Task<JsonDocument> ReadAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    public static async Task<JsonDocument> ReadOkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }
}
