using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public class TranscriptSegment : BaseEntity
{
    public Guid ConversationId { get; set; }
    public Speaker Speaker { get; set; }
    public string OriginalText { get; set; } = string.Empty;
    public string Language { get; set; } = "vi";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public int SequenceNumber { get; set; }

    // Navigation
    public Conversation Conversation { get; set; } = null!;
    public AgentAnalysis? Analysis { get; set; }
}
