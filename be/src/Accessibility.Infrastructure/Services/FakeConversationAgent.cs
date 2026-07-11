using Accessibility.Application.AI;
using Accessibility.Application.AI.Models;
using Accessibility.Domain.Enums;
using System.Globalization;
using System.Text;

namespace Accessibility.Infrastructure.Services;

public class FakeConversationAgent : IConversationAgent
{
    public Task<AgentResult> AnalyzeAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var text = RemoveDiacritics(context.TranscriptText).ToLowerInvariant();
        var hasActionSignals = text.Contains("lich") || text.Contains("hen") || text.Contains("kham") ||
            text.Contains("nhac") || text.Contains("reminder") || text.Contains("appointment") ||
            text.Contains("doctor") || text.Contains("hospital") || text.Contains("benh vien");
        var hasTimeSignals = text.Contains("gio") || text.Contains("ngay") || text.Contains("tuan") ||
            text.Contains("thang") || text.Contains("sang") || text.Contains("chieu") || text.Contains("toi") ||
            text.Contains("tomorrow") || text.Contains("today") || text.Contains("morning") || text.Contains("afternoon");

        var agentResult = new AgentResult
        {
            DetectedLanguage = "vi",
            TranslatedText = "You have a medical appointment tomorrow at 8 AM at Cho Ray Hospital. Remember to be there earlier.",
            Summary = "Medical appointment reminder about a hospital visit tomorrow morning.",
            Category = AnalysisCategory.Medical,
            Importance = Importance.High,
            SuggestedReplies = ["I understand.", "I can help you schedule this."],
            ModelName = "fake-agent",
            RawAgentResponseJson = "{\"mode\":\"fake\"}",
            Actions = []
        };

        if (hasActionSignals && hasTimeSignals)
        {
            agentResult.Actions.Add(new AgentAction
            {
                Type = ActionType.CreateAppointment,
                Title = "Hospital appointment",
                Description = "Visit Cho Ray Hospital for medical results.",
                ScheduledAt = DateTimeOffset.Parse("2026-07-12T08:00:00+07:00"),
                Location = "Cho Ray Hospital",
                RequiresConfirmation = true
            });
            agentResult.Actions.Add(new AgentAction
            {
                Type = ActionType.ScheduleReminder,
                Title = "Reminder before hospital appointment",
                Description = "Reminder before the hospital appointment.",
                ScheduledAt = DateTimeOffset.Parse("2026-07-12T07:30:00+07:00"),
                Location = "Cho Ray Hospital",
                RequiresConfirmation = true
            });
        }

        if (text.Contains("khong") || text.Contains("no"))
        {
            agentResult.Actions.Clear();
            agentResult.Summary = "No action needed from the provided transcript.";
            agentResult.Category = AnalysisCategory.General;
        }

        return Task.FromResult(agentResult);
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
