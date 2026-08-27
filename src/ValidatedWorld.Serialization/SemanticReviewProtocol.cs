using ValidatedWorld.Core;

namespace ValidatedWorld.Serialization;

public enum SemanticReviewItemRole
{
    DirectEdit,
    SemanticConsequence,
    ScopeTopologyMembership,
    ContextOnlyAncestor,
}

public sealed record SemanticReviewBindingDto(
    string BaseFingerprint,
    string OperationFingerprint,
    string ProposedFingerprint,
    string AffectedFingerprint,
    string ReviewFingerprint);

public sealed record SemanticReviewProjectDto(
    string ProjectId,
    string Title,
    string PurposeNodeId,
    NodeDto Purpose);

public sealed record SemanticReviewOperationDto(
    OperationDto Operation,
    NodeDto? CurrentNode,
    NodeDto? ProposedNode,
    EdgeDto? CurrentEdge,
    EdgeDto? ProposedEdge);

public sealed record SemanticReviewAffectedNodeDto(
    string NodeId,
    SemanticReviewItemRole Role,
    bool IsDirectChange,
    int Distance,
    IReadOnlyList<string> ExplanationNodeIds,
    IReadOnlyList<string> ExplanationEdgeIds,
    NodeDto? CurrentNode,
    NodeDto? ProposedNode);

public sealed record SemanticReviewEvidenceEdgeDto(
    string EdgeId,
    string EvidenceRole,
    EdgeDto? CurrentEdge,
    EdgeDto? ProposedEdge);

public sealed record SemanticReviewScopeLineageDto(
    string AffectedNodeId,
    IReadOnlyList<string> CurrentPath,
    IReadOnlyList<string> ProposedPath);

public sealed record SemanticReviewContextNodeDto(
    string NodeId,
    SemanticReviewItemRole Role,
    IReadOnlyList<SemanticReviewScopeLineageDto> Lineages,
    NodeDto? CurrentNode,
    NodeDto? ProposedNode);

public sealed record SemanticReviewScopeTopologyChangeDto(
    string EdgeId,
    string? CurrentChildId,
    string? CurrentParentId,
    IReadOnlyList<string> CurrentChildSubtreeIds,
    IReadOnlyList<string> CurrentParentLineage,
    string? ProposedChildId,
    string? ProposedParentId,
    IReadOnlyList<string> ProposedChildSubtreeIds,
    IReadOnlyList<string> ProposedParentLineage);

public sealed record SemanticReviewDispositionDto(
    string NodeId,
    string Disposition,
    string? Rationale);

public sealed record SemanticReviewManifestDto(
    int OperationCount,
    int AffectedNodeCount,
    int ContextNodeCount,
    int EvidenceEdgeCount,
    int ScopeTopologyChangeCount,
    IReadOnlyList<string> OperationEntityIds,
    IReadOnlyList<string> AffectedNodeIds,
    IReadOnlyList<string> ContextNodeIds,
    IReadOnlyList<string> EvidenceEdgeIds,
    IReadOnlyList<string> ScopeTopologyChangeEdgeIds,
    IReadOnlyList<string> AllowedCitationIds,
    IReadOnlyList<string> Omissions);

public sealed record SemanticReviewRequestDto(
    int Version,
    string Instructions,
    SemanticReviewProjectDto Project,
    SemanticReviewBindingDto Binding,
    IReadOnlyList<SemanticReviewOperationDto> Operations,
    IReadOnlyList<SemanticReviewAffectedNodeDto> AffectedNodes,
    IReadOnlyList<SemanticReviewEvidenceEdgeDto> EvidenceEdges,
    IReadOnlyList<SemanticReviewContextNodeDto> ContextNodes,
    IReadOnlyList<SemanticReviewScopeTopologyChangeDto> ScopeTopologyChanges,
    ValidationDto CurrentValidation,
    ValidationDto ProposedValidation,
    IReadOnlyList<SemanticReviewDispositionDto> ReviewDispositions,
    SemanticReviewManifestDto Manifest);

public sealed record SemanticReviewCitationDto(string EntityId);

public sealed record SemanticReviewConcernDto(
    string Code,
    string Message,
    IReadOnlyList<SemanticReviewCitationDto> Citations);

public sealed record SemanticReviewModelOutputDto(
    string Status,
    string Summary,
    IReadOnlyList<SemanticReviewConcernDto> Concerns);
