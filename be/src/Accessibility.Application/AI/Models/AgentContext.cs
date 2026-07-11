namespace Accessibility.Application.AI.Models;

public class AgentContext
{
    public Guid ConversationId { get; set; }
    public Guid TranscriptSegmentId { get; set; }
    public string UserTimeZone { get; set; } = "Asia/Ho_Chi_Minh";
    public string UserPreferredLanguage { get; set; } = "vi";
    public string TranscriptText { get; set; } = string.Empty;
    public string Speaker { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public List<PreviousContext> RecentContext { get; set; } = [];
}

public class PreviousContext
{
    public string Speaker { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
}
