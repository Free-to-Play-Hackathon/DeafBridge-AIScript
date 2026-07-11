using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public class ProposedAction : BaseEntity
{
    public Guid ConversationId { get; set; }
    public Guid AgentAnalysisId { get; set; }
    public ActionType ActionType { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public string? Location { get; set; }
    public string? RecipientEmail { get; set; }
    public string? PayloadJson { get; set; }
    public ProposedActionStatus Status { get; set; } = ProposedActionStatus.Proposed;
    public bool RequiresConfirmation { get; set; } = true;
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string? FailureReason { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;

    // Navigation
    public Conversation Conversation { get; set; } = null!;
    public AgentAnalysis AgentAnalysis { get; set; } = null!;

    /// <summary>
    /// Generates a deterministic idempotency key based on action attributes.
    /// </summary>
    public static string GenerateIdempotencyKey(
        Guid userId, ActionType actionType, string title,
        DateTimeOffset? scheduledAt, string? location)
    {
        var normalized = $"{userId}|{actionType}|{title.Trim().ToLowerInvariant()}|{scheduledAt?.ToString("O") ?? ""}|{location?.Trim().ToLowerInvariant() ?? ""}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..32];
    }
}
