using System.Text.Json;

namespace ValidatedWorld.Serialization;

public sealed record GraphTemplateDto(
    int Version,
    string Id,
    string Description,
    string PurposeNodeId,
    IReadOnlyList<NodeDto> Nodes,
    IReadOnlyList<EdgeDto> Edges);

public static class TemplateProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumBytes = 1024 * 1024;

    public static GraphTemplateDto Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new JsonException($"A template cannot exceed {MaximumBytes} UTF-8 bytes.");
        var template = JsonSerializer.Deserialize<GraphTemplateDto>(json, Protocol.CreateJsonOptions())
            ?? throw new JsonException("The template cannot be null.");
        if (template.Version != CurrentVersion)
            throw new JsonException($"Unsupported template version {template.Version}; expected {CurrentVersion}.");
        if (string.IsNullOrWhiteSpace(template.Id) || string.IsNullOrWhiteSpace(template.Description) ||
            string.IsNullOrWhiteSpace(template.PurposeNodeId))
            throw new JsonException("Template id, description, and purposeNodeId are required.");
        if (template.Nodes.Count + template.Edges.Count > 1_000)
            throw new JsonException("A template cannot contain more than 1,000 graph entities.");
        return template;
    }

    public static string Serialize(GraphTemplateDto template) =>
        JsonSerializer.Serialize(template, Protocol.CreateJsonOptions());
}
