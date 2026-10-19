using System.Text.Json.Nodes;

namespace HeartRateMonitor.Events;

public enum EventKind { Begin, End, Mark }

/// <summary>
/// One event from another program (see README, "Events from other programs"): a JSON object
/// with at least "kind" = "begin" | "end" | "mark". Everything else is optional and kept verbatim.
/// </summary>
public sealed record AppEvent(DateTime Time, EventKind Kind, JsonObject Json)
{
    public string? Source => Str("source");
    public string Type => Str("type") ?? "event";
    public string? Id => Str("id");
    public string? Parent => Str("parent");
    public string? Label => Str("label");
    public JsonObject? Data => Json["data"] as JsonObject;

    private string? Str(string name) => Json[name] is JsonValue v && v.TryGetValue(out string? s) ? s : Json[name]?.ToString();

    /// <summary>Parses one event, or returns null with a reason if it isn't one.</summary>
    public static AppEvent? Parse(string text, DateTime time, out string? error)
    {
        error = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException ex)
        {
            error = $"not valid JSON ({ex.Message})";
            return null;
        }
        if (node is not JsonObject json)
        {
            error = "not a JSON object";
            return null;
        }
        EventKind? kind = (json["kind"] as JsonValue)?.TryGetValue(out string? k) == true ? k.ToLowerInvariant() switch
        {
            "begin" => EventKind.Begin,
            "end" => EventKind.End,
            "mark" => EventKind.Mark,
            _ => null,
        } : null;
        if (kind is null)
        {
            error = "\"kind\" must be \"begin\", \"end\" or \"mark\"";
            return null;
        }
        return new AppEvent(time, kind.Value, json);
    }
}
