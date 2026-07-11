using Accessibility.Application.AI;
using Accessibility.Application.AI.Models;
using Accessibility.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Accessibility.Infrastructure.Services;

public class CohereConversationAgent : IConversationAgent
{
    private const string Endpoint = "https://api.cohere.com/v2/chat";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CohereConversationAgent> _logger;
    private readonly FakeConversationAgent _fallbackAgent = new();

    public CohereConversationAgent(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<CohereConversationAgent> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AgentResult> AnalyzeAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var apiKey = _configuration["COHERE_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = _configuration["AI_API_KEY"];
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return await _fallbackAgent.AnalyzeAsync(context, cancellationToken);
        }

        var model = _configuration["AI_MODEL"];
        if (string.IsNullOrWhiteSpace(model) || model == "fake-agent")
        {
            model = "command-r7b-12-2024";
        }

        var request = BuildRequest(model, context);
        var json = JsonSerializer.Serialize(request, JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient(nameof(CohereConversationAgent));
        using var response = await client.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Cohere agent failed with status {StatusCode}; using fake fallback", (int)response.StatusCode);
            return await _fallbackAgent.AnalyzeAsync(context, cancellationToken);
        }

        var content = ExtractAssistantContent(body);
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("Cohere agent returned empty content; using fake fallback");
            return await _fallbackAgent.AnalyzeAsync(context, cancellationToken);
        }

        try
        {
            var result = ParseAgentResult(content, model);
            result.RawAgentResponseJson = body;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cohere agent returned invalid structured JSON; using fake fallback");
            return await _fallbackAgent.AnalyzeAsync(context, cancellationToken);
        }
    }

    private static object BuildRequest(string model, AgentContext context)
    {
        var recentContext = context.RecentContext
            .TakeLast(20)
            .Select(item => new
            {
                speaker = item.Speaker,
                text = item.Text,
                timestamp = item.Timestamp
            })
            .ToArray();

        var userPayload = new
        {
            userTimeZone = context.UserTimeZone,
            userPreferredLanguage = context.UserPreferredLanguage,
            currentTranscript = new
            {
                speaker = context.Speaker,
                text = context.TranscriptText,
                timestamp = context.Timestamp
            },
            memory = recentContext
        };

        return new
        {
            model,
            temperature = 0.1,
            max_tokens = 1400,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = """
                    You are an autonomous accessibility communication AI Agent.

                    Return JSON only. No markdown. The backend executes proposed actions from your JSON.

                    Important domain context:
                    - The backend assists the deaf user who owns this account.
                    - A HearingUser transcript is often someone speaking to the deaf user.
                    - When a HearingUser says "you need to..." or "please remember...", create actions for the deaf user, not for the speaker.
                    - Preserve who said what in summaries when useful.

                    Supported action types:
                    SaveNote, CreateTask, CreateAppointment, ScheduleReminder, SendEmail, None.

                    Rules:
                    - Never invent missing dates, times, people, email addresses, or locations.
                    - Convert relative dates using userTimeZone and current transcript timestamp.
                    - If a date or time is ambiguous, leave scheduledAt/dueAt null and require confirmation.
                    - For explicit reminders with concrete time, add ScheduleReminder.
                    - For explicit appointments with concrete time, add CreateAppointment.
                    - For useful follow-up with missing time, add CreateTask to confirm missing details.
                    - SaveNote is safe and may set requiresConfirmation=false.
                    - CreateTask, CreateAppointment, ScheduleReminder, and SendEmail must set requiresConfirmation=true.
                    - SendEmail requires an explicit recipient email; otherwise do not propose SendEmail.
                    - Critical/emergency content may use importance Critical, but do not contact emergency services.
                    - Avoid duplicate or conflicting actions based on memory.

                    Required JSON schema:
                    {
                      "detectedLanguage": "vi|en|...",
                      "translatedText": "English translation or null",
                      "summary": "short useful summary",
                      "category": "General|Medical|Appointment|Task|Reminder|Emergency|Education|Work|Shopping|Travel|Other",
                      "importance": "Low|Normal|High|Critical",
                      "suggestedReplies": ["short reply"],
                      "actions": [
                        {
                          "type": "SaveNote|CreateTask|CreateAppointment|ScheduleReminder|SendEmail|None",
                          "title": "short title",
                          "description": "details or null",
                          "scheduledAt": "ISO-8601 with offset or null",
                          "dueAt": "ISO-8601 with offset or null",
                          "location": "location or null",
                          "recipientEmail": "email or null",
                          "requiresConfirmation": true
                        }
                      ]
                    }
                    """
                },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(userPayload, JsonOptions)
                }
            }
        };
    }

    private static string? ExtractAssistantContent(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("text", out var text))
            {
                var value = text.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : StripJsonFence(value);
            }
        }

        return null;
    }

    private static AgentResult ParseAgentResult(string content, string model)
    {
        var dto = JsonSerializer.Deserialize<CohereAgentResultDto>(content, JsonOptions)
            ?? throw new InvalidOperationException("Agent JSON was empty");

        return new AgentResult
        {
            DetectedLanguage = string.IsNullOrWhiteSpace(dto.DetectedLanguage) ? "unknown" : dto.DetectedLanguage,
            TranslatedText = dto.TranslatedText,
            Summary = dto.Summary ?? string.Empty,
            Category = ParseEnum(dto.Category, AnalysisCategory.Other),
            Importance = ParseEnum(dto.Importance, Importance.Normal),
            SuggestedReplies = dto.SuggestedReplies ?? [],
            Actions = (dto.Actions ?? [])
                .Select(MapAction)
                .Where(action => action.Type != ActionType.None)
                .ToList(),
            ModelName = model
        };
    }

    private static AgentAction MapAction(CohereAgentActionDto action)
    {
        var actionType = ParseEnum(action.Type, ActionType.None);
        var requiresConfirmation = actionType switch
        {
            ActionType.SaveNote => action.RequiresConfirmation,
            ActionType.None => false,
            _ => true
        };

        return new AgentAction
        {
            Type = actionType,
            Title = action.Title ?? actionType.ToString(),
            Description = action.Description,
            ScheduledAt = ParseDateTimeOffset(action.ScheduledAt),
            DueAt = ParseDateTimeOffset(action.DueAt),
            Location = action.Location,
            RecipientEmail = action.RecipientEmail,
            RequiresConfirmation = requiresConfirmation
        };
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct
    {
        return Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : fallback;
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string StripJsonFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewLine < 0 || lastFence <= firstNewLine)
        {
            return trimmed;
        }

        return trimmed[(firstNewLine + 1)..lastFence].Trim();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class CohereAgentResultDto
    {
        public string? DetectedLanguage { get; set; }
        public string? TranslatedText { get; set; }
        public string? Summary { get; set; }
        public string? Category { get; set; }
        public string? Importance { get; set; }
        public List<string>? SuggestedReplies { get; set; }
        public List<CohereAgentActionDto>? Actions { get; set; }
    }

    private sealed class CohereAgentActionDto
    {
        public string? Type { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? ScheduledAt { get; set; }
        public string? DueAt { get; set; }
        public string? Location { get; set; }
        public string? RecipientEmail { get; set; }
        public bool RequiresConfirmation { get; set; } = true;
    }
}
