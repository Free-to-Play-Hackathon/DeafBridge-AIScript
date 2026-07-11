using Accessibility.Application.AI;
using Accessibility.Application.AI.Models;
using Accessibility.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Accessibility.Infrastructure.Services;

public class OpenAIConversationAgent : IConversationAgent
{
    private const string Endpoint = "https://api.openai.com/v1/responses";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAIConversationAgent> _logger;

    public OpenAIConversationAgent(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OpenAIConversationAgent> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AgentResult> AnalyzeAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var apiKey = _configuration["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = _configuration["AI_API_KEY"];
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY or AI_API_KEY is required when AI_PROVIDER=openai.");
        }

        var model = _configuration["AI_MODEL"];
        if (string.IsNullOrWhiteSpace(model) || model == "fake-agent")
        {
            model = "gpt-5.6";
        }

        var input = BuildInitialInput(context);
        var tools = BuildTools();
        var firstResponse = await CreateResponseAsync(apiKey, model, BuildInstructions(), input, tools, cancellationToken);
        var actions = ExecuteToolCalls(firstResponse, input);

        var finalInstructions = BuildInstructions() + "\n\nIf any backend action is needed and has not been called yet, call the matching tool now. Otherwise return the final analysis as JSON only. Include no markdown.";
        var secondResponse = await CreateResponseAsync(apiKey, model, finalInstructions, input, tools, cancellationToken);
        var finalActions = ExecuteToolCalls(secondResponse, input);
        actions.AddRange(finalActions);

        var finalResponse = secondResponse;
        if (finalActions.Count > 0)
        {
            finalInstructions = BuildInstructions() + "\n\nAll needed backend tools have now been called. Return the final analysis as JSON only. Include no markdown. Do not call any more tools.";
            finalResponse = await CreateResponseAsync(apiKey, model, finalInstructions, input, tools, cancellationToken);
        }

        var result = ParseFinalResult(finalResponse, actions, model);
        result.RawAgentResponseJson = JsonSerializer.Serialize(new
        {
            firstResponse,
            secondResponse,
            finalResponse
        }, JsonOptions);

        return result;
    }

    private static JsonArray BuildInitialInput(AgentContext context)
    {
        var recentContext = new JsonArray(context.RecentContext
            .TakeLast(20)
            .Select(item => new JsonObject
            {
                ["speaker"] = item.Speaker,
                ["text"] = item.Text,
                ["timestamp"] = item.Timestamp.ToString("O")
            })
            .ToArray<JsonNode?>());

        var payload = new JsonObject
        {
            ["userTimeZone"] = context.UserTimeZone,
            ["userPreferredLanguage"] = context.UserPreferredLanguage,
            ["conversationId"] = context.ConversationId.ToString(),
            ["transcriptSegmentId"] = context.TranscriptSegmentId.ToString(),
            ["currentTranscript"] = new JsonObject
            {
                ["speaker"] = context.Speaker,
                ["text"] = context.TranscriptText,
                ["timestamp"] = context.Timestamp.ToString("O")
            },
            ["memory"] = recentContext
        };

        return
        [
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = payload.ToJsonString(JsonOptions)
            }
        ];
    }

    private static string BuildInstructions() =>
        """
        You are an autonomous accessibility communication AI Agent for a deaf user's communication assistant.

        Decide whether the transcript requires backend action. If action is needed, call one or more provided tools.
        The backend will execute your tool calls by creating proposed actions; unsafe actions require user confirmation before final scheduling or sending.

        Important rules:
        - A HearingUser transcript is usually someone speaking to the deaf user. Create actions for the deaf account owner, not for the speaker.
        - Use the user's timezone and the transcript timestamp when resolving relative times.
        - Never invent missing dates, times, people, emails, or locations.
        - If a date or time is ambiguous, call no scheduling tool and explain that confirmation is needed.
        - Appointment, reminder, task, and email tools must only be called when explicit transcript evidence supports them.
        - Send email only when an explicit recipient email address is present.
        - Do not contact emergency services.

        Final response JSON schema:
        {
          "detectedLanguage": "vi|en|...",
          "translatedText": "English translation or null",
          "summary": "short useful summary",
          "category": "General|Medical|Appointment|Task|Reminder|Emergency|Education|Work|Shopping|Travel|Other",
          "importance": "Low|Normal|High|Critical",
          "suggestedReplies": ["short reply"]
        }
        """;

    private static JsonArray BuildTools()
    {
        JsonObject SharedProperties() => new()
        {
            ["title"] = new JsonObject { ["type"] = "string" },
            ["description"] = new JsonObject { ["type"] = NullableStringType() },
            ["scheduledAt"] = new JsonObject { ["type"] = NullableStringType(), ["description"] = "ISO-8601 datetime with offset." },
            ["dueAt"] = new JsonObject { ["type"] = NullableStringType(), ["description"] = "ISO-8601 datetime with offset." },
            ["location"] = new JsonObject { ["type"] = NullableStringType() },
            ["recipientEmail"] = new JsonObject { ["type"] = NullableStringType() }
        };

        JsonArray NullableStringType() => ["string", "null"];

        JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
        {
            ["type"] = "function",
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(value => JsonValue.Create(value)).ToArray<JsonNode?>()),
                ["additionalProperties"] = false
            },
            ["strict"] = true
        };

        return
        [
            Tool("save_note", "Create a proposed note from useful transcript information.", new JsonObject
            {
                ["title"] = new JsonObject { ["type"] = "string" },
                ["description"] = new JsonObject { ["type"] = NullableStringType() }
            }, "title", "description"),
            Tool("create_task", "Create a proposed task for the deaf user.", SharedProperties(), "title", "description", "dueAt", "scheduledAt", "location", "recipientEmail"),
            Tool("create_appointment", "Create a proposed appointment with a concrete time.", SharedProperties(), "title", "description", "scheduledAt", "dueAt", "location", "recipientEmail"),
            Tool("schedule_reminder", "Create a proposed reminder with a concrete reminder time.", SharedProperties(), "title", "description", "scheduledAt", "dueAt", "location", "recipientEmail"),
            Tool("send_email", "Create a proposed email only when an explicit recipient email is provided.", SharedProperties(), "title", "description", "recipientEmail", "scheduledAt", "dueAt", "location")
        ];
    }

    private async Task<JsonObject> CreateResponseAsync(
        string apiKey,
        string model,
        string instructions,
        JsonArray input,
        JsonArray tools,
        CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = instructions,
            ["input"] = input.DeepClone(),
            ["tools"] = tools.DeepClone(),
            ["parallel_tool_calls"] = true,
            ["max_output_tokens"] = 1600
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(request.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient(nameof(OpenAIConversationAgent));
        using var response = await client.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI Responses API failed with status {StatusCode}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"OpenAI Responses API failed with status {(int)response.StatusCode}.");
        }

        return JsonNode.Parse(body)?.AsObject()
            ?? throw new InvalidOperationException("OpenAI Responses API returned invalid JSON.");
    }

    private static List<AgentAction> ExecuteToolCalls(JsonObject response, JsonArray input)
    {
        var actions = new List<AgentAction>();
        var output = response["output"]?.AsArray();
        if (output is null)
        {
            return actions;
        }

        foreach (var item in output)
        {
            if (item is not null)
            {
                input.Add(item.DeepClone());
            }

            var itemObject = item?.AsObject();
            if (itemObject?["type"]?.GetValue<string>() != "function_call")
            {
                continue;
            }

            var name = itemObject["name"]?.GetValue<string>() ?? string.Empty;
            var callId = itemObject["call_id"]?.GetValue<string>() ?? string.Empty;
            var arguments = itemObject["arguments"]?.GetValue<string>() ?? "{}";
            var action = MapToolCall(name, arguments);
            if (action is not null)
            {
                actions.Add(action);
            }

            input.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = callId,
                ["output"] = JsonSerializer.Serialize(new
                {
                    accepted = action is not null,
                    proposedActionType = action?.Type.ToString(),
                    title = action?.Title
                }, JsonOptions)
            });
        }

        return actions;
    }

    private static AgentAction? MapToolCall(string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        var root = document.RootElement;

        var type = name switch
        {
            "save_note" => ActionType.SaveNote,
            "create_task" => ActionType.CreateTask,
            "create_appointment" => ActionType.CreateAppointment,
            "schedule_reminder" => ActionType.ScheduleReminder,
            "send_email" => ActionType.SendEmail,
            _ => ActionType.None
        };

        if (type == ActionType.None)
        {
            return null;
        }

        return new AgentAction
        {
            Type = type,
            Title = GetString(root, "title") ?? type.ToString(),
            Description = GetString(root, "description"),
            ScheduledAt = ParseDateTimeOffset(GetString(root, "scheduledAt")),
            DueAt = ParseDateTimeOffset(GetString(root, "dueAt")),
            Location = GetString(root, "location"),
            RecipientEmail = GetString(root, "recipientEmail"),
            RequiresConfirmation = type != ActionType.SaveNote
        };
    }

    private static AgentResult ParseFinalResult(JsonObject response, List<AgentAction> actions, string model)
    {
        var text = response["output_text"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = ExtractTextFromOutput(response);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<FinalAnalysisDto>(StripJsonFence(text ?? "{}"), JsonOptions);
            return new AgentResult
            {
                DetectedLanguage = string.IsNullOrWhiteSpace(dto?.DetectedLanguage) ? "unknown" : dto.DetectedLanguage,
                TranslatedText = dto?.TranslatedText,
                Summary = dto?.Summary ?? "OpenAI agent completed analysis.",
                Category = ParseEnum(dto?.Category, AnalysisCategory.Other),
                Importance = ParseEnum(dto?.Importance, Importance.Normal),
                SuggestedReplies = dto?.SuggestedReplies ?? [],
                Actions = actions,
                ModelName = model
            };
        }
        catch
        {
            return new AgentResult
            {
                DetectedLanguage = "unknown",
                Summary = text ?? "OpenAI agent completed analysis.",
                Category = AnalysisCategory.Other,
                Importance = Importance.Normal,
                SuggestedReplies = [],
                Actions = actions,
                ModelName = model
            };
        }
    }

    private static string? ExtractTextFromOutput(JsonObject response)
    {
        var output = response["output"]?.AsArray();
        if (output is null)
        {
            return null;
        }

        foreach (var item in output)
        {
            var content = item?["content"]?.AsArray();
            if (content is null)
            {
                continue;
            }

            foreach (var contentItem in content)
            {
                var text = contentItem?["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.GetString();
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct
    {
        return Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : fallback;
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
        return firstNewLine >= 0 && lastFence > firstNewLine
            ? trimmed[(firstNewLine + 1)..lastFence].Trim()
            : trimmed;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class FinalAnalysisDto
    {
        public string? DetectedLanguage { get; set; }
        public string? TranslatedText { get; set; }
        public string? Summary { get; set; }
        public string? Category { get; set; }
        public string? Importance { get; set; }
        public List<string>? SuggestedReplies { get; set; }
    }
}
