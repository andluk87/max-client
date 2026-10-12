using System.Globalization;
using System.Text.Json.Nodes;
namespace MaxClient;
internal static class Data
{
    public static string Text(JsonNode? node, string key) => node?[key]?.ToString() ?? "";
    public static long Number(JsonNode? node, string key) =>
        long.TryParse(Text(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    public static IEnumerable<JsonNode> Items(JsonNode? node, string key) =>
        node?[key] is JsonArray array ? array.OfType<JsonNode>() : Enumerable.Empty<JsonNode>();
    public static string Name(JsonNode? node) =>
        Items(node, "names").Select(n => Text(n, "name")).FirstOrDefault(n => n.Length > 0) ?? Text(node, "id");
    public static string LoginToken(JsonNode node) => node["tokenAttrs"]?["LOGIN"]?["token"]?.ToString() ?? "";
}
internal sealed record Chat(long Id, string Title, string Preview, long Unread, long LastTime);
internal sealed record Message(string Id, long Time, string SenderId, string Text);
internal sealed record Credentials(string DeviceId, string Token);
internal sealed class MaxException(string code) : Exception("MAX: " + code)
{
    public string Code { get; } = code;
}
