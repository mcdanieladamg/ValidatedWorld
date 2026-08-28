namespace ValidatedWorld.Application;

public sealed record AuthoringApproval(
    string ApprovalId,
    string ConversationId,
    string DatabasePath,
    ChangeSessionReference Reference,
    DateTimeOffset ApprovedUtc,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// Holds short-lived human approval for an exact authoring proposal. Approval is
/// process-local and cannot survive a changed proposal, changed review state, or
/// application restart.
/// </summary>
public sealed class AuthoringApprovalGate(Func<DateTimeOffset>? utcNow = null)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, AuthoringApproval> _approvals = new(StringComparer.Ordinal);

    public AuthoringApproval Approve(
        string conversationId,
        ChangeSessionSnapshot snapshot,
        TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(conversationId))
            throw new ArgumentException("A conversation ID is required.", nameof(conversationId));
        if (!snapshot.Readiness.IsReady)
            throw new InvalidOperationException("The complete affected review must be ready before approval.");

        var duration = lifetime ?? DefaultLifetime;
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        var now = _utcNow().ToUniversalTime();
        var approval = new AuthoringApproval(
            Guid.NewGuid().ToString("N"),
            conversationId,
            Path.GetFullPath(snapshot.Path),
            snapshot.Reference,
            now,
            now.Add(duration));
        lock (_gate) _approvals[conversationId] = approval;
        return approval;
    }

    public AuthoringApproval RequireCurrent(
        string conversationId,
        string databasePath,
        ChangeSessionReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_gate)
        {
            if (!_approvals.TryGetValue(conversationId, out var approval))
                throw new InvalidOperationException("This conversation has no human approval for the current proposal.");
            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            if (!pathComparer.Equals(
                    approval.DatabasePath, Path.GetFullPath(databasePath)))
                throw new InvalidOperationException("Human approval belongs to a different database path.");
            if (_utcNow().ToUniversalTime() >= approval.ExpiresUtc)
            {
                _approvals.Remove(conversationId);
                throw new InvalidOperationException("Human approval expired; present the current proposal again.");
            }
            if (!Same(approval.Reference, reference))
            {
                _approvals.Remove(conversationId);
                throw new InvalidOperationException("The proposal or review state changed after human approval.");
            }
            return approval;
        }
    }

    public void Invalidate(string conversationId)
    {
        lock (_gate) _approvals.Remove(conversationId);
    }

    private static bool Same(ChangeSessionReference left, ChangeSessionReference right) =>
        left.ProjectId == right.ProjectId &&
        StringComparer.Ordinal.Equals(left.SessionId, right.SessionId) &&
        StringComparer.Ordinal.Equals(left.BaseFingerprint, right.BaseFingerprint) &&
        StringComparer.Ordinal.Equals(left.OperationFingerprint, right.OperationFingerprint) &&
        StringComparer.Ordinal.Equals(left.ProposedFingerprint, right.ProposedFingerprint) &&
        StringComparer.Ordinal.Equals(left.AffectedFingerprint, right.AffectedFingerprint) &&
        StringComparer.Ordinal.Equals(left.ReviewFingerprint, right.ReviewFingerprint);
}
