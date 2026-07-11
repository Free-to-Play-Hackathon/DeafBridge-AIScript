using Accessibility.Domain.Enums;

namespace Accessibility.Application.AI.Models;

public class AgentResult
{
    public string DetectedLanguage { get; set; } = "vi";
    public string? TranslatedText { get; set; }
    public string Summary { get; set; } = string.Empty;
    public AnalysisCategory Category { get; set; } = AnalysisCategory.General;
    public Importance Importance { get; set; } = Importance.Normal;
    public List<string> SuggestedReplies { get; set; } = [];
    public List<AgentAction> Actions { get; set; } = [];
    public string? RawAgentResponseJson { get; set; }
    public string? ModelName { get; set; }
}

public class AgentAction
{
    public ActionType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public string? Location { get; set; }
    public string? RecipientEmail { get; set; }
    public bool RequiresConfirmation { get; set; } = true;
}
