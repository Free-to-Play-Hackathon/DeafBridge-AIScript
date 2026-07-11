using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public class AgentAnalysis : BaseEntity
{
    public Guid ConversationId { get; set; }
    public Guid TranscriptSegmentId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public AnalysisCategory Category { get; set; } = AnalysisCategory.General;
    public Importance Importance { get; set; } = Importance.Normal;
    public string? DetectedLanguage { get; set; }
    public string? TranslatedText { get; set; }
    public string? SuggestedRepliesJson { get; set; }
    public string? RawAgentResponseJson { get; set; }
    public string? ModelName { get; set; }
    public AnalysisStatus Status { get; set; } = AnalysisStatus.Pending;
    public DateTimeOffset? CompletedAt { get; set; }

    // Navigation
    public Conversation Conversation { get; set; } = null!;
    public TranscriptSegment TranscriptSegment { get; set; } = null!;
    public ICollection<ProposedAction> ProposedActions { get; set; } = [];
}
