using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public class Conversation : BaseEntity
{
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public ConversationStatus Status { get; set; } = ConversationStatus.Active;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
    public ICollection<TranscriptSegment> Transcripts { get; set; } = [];
    public ICollection<AgentAnalysis> Analyses { get; set; } = [];
    public ICollection<ProposedAction> ProposedActions { get; set; } = [];
}
