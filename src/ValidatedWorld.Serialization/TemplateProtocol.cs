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

    public static GraphTemplateDto Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var template = JsonSerializer.Deserialize<GraphTemplateDto>(json, Protocol.CreateJsonOptions())
            ?? throw new JsonException("The template cannot be null.");
        if (template.Version != CurrentVersion)
            throw new JsonException($"Unsupported template version {template.Version}; expected {CurrentVersion}.");
        if (string.IsNullOrWhiteSpace(template.Id) || string.IsNullOrWhiteSpace(template.Description) ||
            string.IsNullOrWhiteSpace(template.PurposeNodeId))
            throw new JsonException("Template id, description, and purposeNodeId are required.");
        return template;
    }

    public static string Serialize(GraphTemplateDto template) =>
        JsonSerializer.Serialize(template, Protocol.CreateJsonOptions());
}
