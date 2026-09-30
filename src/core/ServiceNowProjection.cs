using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Helpdesk.Core;

public static partial class ServiceNowProjection
{
    public static readonly HashSet<string> Tables =
        ["kb_knowledge", "incident", "problem", "change_request", "cmdb_ci_business_app"];

    public static string Field(JsonElement record, string name)
    {
        if (!record.TryGetProperty(name, out var value))
            return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Object => Field(value, "display_value") is { Length: > 0 } display
                ? display : Field(value, "value"),
            JsonValueKind.Null => "",
            _ => throw new InvalidDataException($"Unsupported field shape: {name}.")
        };
    }

    public static string SourceId(JsonElement record)
    {
        var id = Field(record, "sys_id");
        if (!IdPattern().IsMatch(id))
            throw new InvalidDataException("sys_id must contain 32 hexadecimal characters.");
        return id.ToLowerInvariant();
    }

    public static IReadOnlyList<KnowledgeDocument> Project(string table, JsonElement record, long version)
    {
        if (!Tables.Contains(table))
            throw new InvalidDataException($"Unknown source table: {table}.");
        var sourceId = SourceId(record);
        if (Field(record, "audience") != "employee" ||
            (table == "kb_knowledge" && Field(record, "active") == "false"))
            return [];

        var content = table switch
        {
            "kb_knowledge" when Field(record, "workflow_state") == "published" => Field(record, "text"),
            "incident" when Field(record, "incident_state") is "6" or "7" => Field(record, "close_notes"),
            "problem" when Field(record, "known_error") == "true" => Field(record, "resolution_notes"),
            _ => ""
        };
        content = PlainText(content);
        if (string.IsNullOrWhiteSpace(content))
            return [];
        var title = PlainText(Field(record, "short_description"));
        var number = Field(record, "number");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(number))
            throw new InvalidDataException("Eligible records require number and short_description.");
        if (!DateTimeOffset.TryParse(Field(record, "sys_updated_on"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var updatedAt))
            throw new InvalidDataException($"Invalid sys_updated_on on {number}.");
        var application = Field(record, "business_service");
        var category = Field(record, "category");
        var documents = new List<KnowledgeDocument>();
        const int chunkSize = 1800;
        for (var offset = 0; offset < content.Length;)
        {
            var chunk = documents.Count;
            var length = Math.Min(chunkSize, content.Length - offset);
            if (offset + length < content.Length && char.IsHighSurrogate(content[offset + length - 1]))
                length--;
            documents.Add(new($"{table}_{sourceId}_{chunk:D4}", sourceId, table, number,
                title, content.Substring(offset, length),
                application, category, "employee", updatedAt, version, chunk));
            offset += length;
        }
        return documents;
    }

    private static string PlainText(string text) =>
        WhitespacePattern().Replace(WebUtility.HtmlDecode(TagPattern().Replace(
            ScriptPattern().Replace(text, " "), " ")), " ").Trim();

    [GeneratedRegex("^[a-fA-F0-9]{32}$")]
    private static partial Regex IdPattern();
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptPattern();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
