using Accessibility.Api.Controllers;
using Accessibility.Application.AI.Models;
using Accessibility.Application.Common.Models;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Accessibility.Infrastructure.Services;
using FluentAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Moq.Protected;
using System.Net;
using System.Security.Claims;
using System.Text;
using Xunit;

namespace Accessibility.UnitTests.Unit;

public class AgentAndActionTests
{
    [Fact]
    public async Task FakeAgent_ReturnsStructuredAppointmentAndReminderActions()
    {
        var agent = new FakeConversationAgent();
        var result = await agent.AnalyzeAsync(new AgentContext
        {
            TranscriptText = "Ngay mai luc 8 gio toi co lich kham o benh vien Cho Ray, hay nhac toi truoc 30 phut.",
            UserTimeZone = "Asia/Ho_Chi_Minh"
        }, CancellationToken.None);

        result.Category.Should().Be(AnalysisCategory.Medical);
        result.Importance.Should().Be(Importance.High);
        result.Actions.Should().Contain(x => x.Type == ActionType.CreateAppointment);
        result.Actions.Should().Contain(x => x.Type == ActionType.ScheduleReminder);
        result.Actions.Should().OnlyContain(x => x.Location == null || x.Location.Contains("Hospital"));
    }

    [Fact]
    public async Task FakeAgent_HandlesAmbiguousContentWithoutInventingDates()
    {
        var agent = new FakeConversationAgent();
        var result = await agent.AnalyzeAsync(new AgentContext
        {
            TranscriptText = "Toi can gap ai do vao tuan sau.",
            UserTimeZone = "Asia/Ho_Chi_Minh"
        }, CancellationToken.None);

        result.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenAIAgent_ExecutesResponsesFunctionCallAsReminderAction()
    {
        var firstResponse = """
        {
          "output": [
            {
              "type": "function_call",
              "call_id": "call_1",
              "name": "schedule_reminder",
              "arguments": "{\"title\":\"Hospital appointment reminder\",\"description\":\"Leave early for Cho Ray Hospital.\",\"scheduledAt\":\"2026-07-12T07:30:00+07:00\",\"dueAt\":null,\"location\":\"Cho Ray Hospital\",\"recipientEmail\":null}"
            }
          ]
        }
        """;
        var finalResponse = """
        {
          "output_text": "{\"detectedLanguage\":\"vi\",\"translatedText\":\"Reminder before hospital appointment.\",\"summary\":\"The user needs a reminder before a hospital appointment.\",\"category\":\"Reminder\",\"importance\":\"High\",\"suggestedReplies\":[\"I will remind you.\"]}",
          "output": []
        }
        """;
        var httpClientFactory = CreateHttpClientFactory(firstResponse, finalResponse);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI_PROVIDER"] = "openai",
                ["OPENAI_API_KEY"] = "test-key",
                ["AI_MODEL"] = "gpt-test"
            })
            .Build();
        var agent = new OpenAIConversationAgent(httpClientFactory.Object, configuration, NullLogger<OpenAIConversationAgent>.Instance);

        var result = await agent.AnalyzeAsync(new AgentContext
        {
            TranscriptText = "Ngay mai luc 8 gio toi co lich kham o benh vien Cho Ray, hay nhac toi truoc 30 phut.",
            Speaker = Speaker.HearingUser.ToString(),
            Timestamp = DateTimeOffset.Parse("2026-07-11T10:00:00+07:00"),
            UserTimeZone = "Asia/Ho_Chi_Minh"
        }, CancellationToken.None);

        result.ModelName.Should().Be("gpt-test");
        result.Category.Should().Be(AnalysisCategory.Reminder);
        result.Importance.Should().Be(Importance.High);
        result.Actions.Should().ContainSingle(action =>
            action.Type == ActionType.ScheduleReminder &&
            action.Title == "Hospital appointment reminder" &&
            action.ScheduledAt == DateTimeOffset.Parse("2026-07-12T07:30:00+07:00") &&
            action.RequiresConfirmation);
    }

    [Fact]
    public async Task OpenAIAgent_ExecutesFunctionCallFromSecondResponse()
    {
        var firstResponse = """
        {
          "output_text": "{\"detectedLanguage\":\"en\",\"translatedText\":\"Wake up at 10 AM.\",\"summary\":\"The user needs to wake up at 10 AM.\",\"category\":\"Reminder\",\"importance\":\"High\",\"suggestedReplies\":[\"I will remind you.\"]}",
          "output": []
        }
        """;
        var secondResponse = """
        {
          "output": [
            {
              "type": "function_call",
              "call_id": "call_2",
              "name": "schedule_reminder",
              "arguments": "{\"title\":\"Wake up\",\"description\":\"Wake up at 10 AM.\",\"scheduledAt\":\"2026-07-12T10:00:00+07:00\",\"dueAt\":null,\"location\":null,\"recipientEmail\":null}"
            }
          ]
        }
        """;
        var finalResponse = """
        {
          "output_text": "{\"detectedLanguage\":\"en\",\"translatedText\":\"Wake up at 10 AM.\",\"summary\":\"The user asked to wake up at 10 AM.\",\"category\":\"Reminder\",\"importance\":\"High\",\"suggestedReplies\":[\"I will remind you at 10 AM.\"]}",
          "output": []
        }
        """;
        var httpClientFactory = CreateHttpClientFactory(firstResponse, secondResponse, finalResponse);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI_PROVIDER"] = "openai",
                ["OPENAI_API_KEY"] = "test-key",
                ["AI_MODEL"] = "gpt-test"
            })
            .Build();
        var agent = new OpenAIConversationAgent(httpClientFactory.Object, configuration, NullLogger<OpenAIConversationAgent>.Instance);

        var result = await agent.AnalyzeAsync(new AgentContext
        {
            TranscriptText = "Wake me up at 10 AM.",
            Speaker = Speaker.HearingUser.ToString(),
            Timestamp = DateTimeOffset.Parse("2026-07-12T08:00:00+07:00"),
            UserTimeZone = "Asia/Ho_Chi_Minh"
        }, CancellationToken.None);

        result.Category.Should().Be(AnalysisCategory.Reminder);
        result.Actions.Should().ContainSingle(action =>
            action.Type == ActionType.ScheduleReminder &&
            action.Title == "Wake up" &&
            action.ScheduledAt == DateTimeOffset.Parse("2026-07-12T10:00:00+07:00") &&
            action.RequiresConfirmation);
        result.RawAgentResponseJson.Should().Contain("secondResponse");
    }

    [Fact]
    public async Task CohereAgent_ParsesJsonActionAsReminderAction()
    {
        var response = """
        {
          "id": "chat-1",
          "finish_reason": "COMPLETE",
          "message": {
            "role": "assistant",
            "content": [
              {
                "type": "text",
                "text": "{\"detectedLanguage\":\"en\",\"translatedText\":\"Wake me up at 10 AM.\",\"summary\":\"The user asked for a wake-up reminder at 10 AM.\",\"category\":\"Reminder\",\"importance\":\"High\",\"suggestedReplies\":[\"I will remind you at 10 AM.\"],\"actions\":[{\"type\":\"ScheduleReminder\",\"title\":\"Wake up\",\"description\":\"Wake-up reminder at 10 AM.\",\"scheduledAt\":\"2026-07-12T10:00:00+07:00\",\"dueAt\":null,\"location\":null,\"recipientEmail\":null,\"requiresConfirmation\":true}]}"
              }
            ]
          }
        }
        """;
        var httpClientFactory = CreateHttpClientFactory(response);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI_PROVIDER"] = "cohere",
                ["COHERE_API_KEY"] = "test-key",
                ["AI_MODEL"] = "command-r7b-12-2024"
            })
            .Build();
        var agent = new CohereConversationAgent(httpClientFactory.Object, configuration, NullLogger<CohereConversationAgent>.Instance);

        var result = await agent.AnalyzeAsync(new AgentContext
        {
            TranscriptText = "Wake me up at 10 AM.",
            Speaker = Speaker.HearingUser.ToString(),
            Timestamp = DateTimeOffset.Parse("2026-07-12T08:00:00+07:00"),
            UserTimeZone = "Asia/Ho_Chi_Minh"
        }, CancellationToken.None);

        result.ModelName.Should().Be("command-r7b-12-2024");
        result.Category.Should().Be(AnalysisCategory.Reminder);
        result.Importance.Should().Be(Importance.High);
        result.Actions.Should().ContainSingle(action =>
            action.Type == ActionType.ScheduleReminder &&
            action.Title == "Wake up" &&
            action.ScheduledAt == DateTimeOffset.Parse("2026-07-12T10:00:00+07:00") &&
            action.RequiresConfirmation);
    }

    [Fact]
    public async Task ConfirmAction_WritesOutboxMessageWithoutPublishingDirectly()
    {
        await using var dbContext = CreateDbContext();
        var user = new User { Email = "user@example.com", DisplayName = "User", PreferredLanguage = "en", TimeZone = "Asia/Ho_Chi_Minh" };
        var conversation = new Conversation { UserId = user.Id, Title = "Visit", Status = ConversationStatus.Active, StartedAt = DateTimeOffset.UtcNow };
        var transcript = new TranscriptSegment { ConversationId = conversation.Id, Speaker = Speaker.HearingUser, OriginalText = "Appointment", Language = "en", StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, SequenceNumber = 1 };
        var analysis = new AgentAnalysis { ConversationId = conversation.Id, TranscriptSegmentId = transcript.Id, Summary = "Pending", Status = AnalysisStatus.Completed };
        var action = new ProposedAction
        {
            ConversationId = conversation.Id,
            AgentAnalysisId = analysis.Id,
            ActionType = ActionType.CreateAppointment,
            Title = "Hospital appointment",
            Status = ProposedActionStatus.Proposed,
            RequiresConfirmation = true,
            IdempotencyKey = "key"
        };

        dbContext.Users.Add(user);
        dbContext.Conversations.Add(conversation);
        dbContext.TranscriptSegments.Add(transcript);
        dbContext.AgentAnalyses.Add(analysis);
        dbContext.ProposedActions.Add(action);
        await dbContext.SaveChangesAsync();

        var publishEndpoint = new Mock<IPublishEndpoint>(MockBehavior.Strict);
        var controller = new ProposedActionsController(dbContext, publishEndpoint.Object)
        {
            ControllerContext = CreateControllerContext(user.Email)
        };

        var result = await controller.Confirm(action.Id, new ConfirmActionRequest("Updated title", null, "Cho Ray Hospital", null), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        dbContext.OutboxMessages.Should().ContainSingle(x => x.Type.Contains("ProposedActionConfirmed"));
        publishEndpoint.VerifyNoOtherCalls();
    }

    [Fact]
    public void GenerateIdempotencyKey_NormalizesTitleAndLocation()
    {
        var userId = Guid.NewGuid();
        var scheduledAt = DateTimeOffset.Parse("2026-07-12T08:00:00+07:00");

        var first = ProposedAction.GenerateIdempotencyKey(userId, ActionType.CreateAppointment, " Hospital appointment ", scheduledAt, " Cho Ray Hospital ");
        var second = ProposedAction.GenerateIdempotencyKey(userId, ActionType.CreateAppointment, "hospital appointment", scheduledAt, "cho ray hospital");

        first.Should().Be(second);
    }

    private static AccessibilityDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AccessibilityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AccessibilityDbContext(options);
    }

    private static ControllerContext CreateControllerContext(string email)
    {
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, email)],
                    "TestAuth"))
            }
        };
    }

    private static Mock<IHttpClientFactory> CreateHttpClientFactory(params string[] responses)
    {
        var responseQueue = new Queue<string>(responses);
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseQueue.Dequeue(), Encoding.UTF8, "application/json")
            });

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler.Object));

        return httpClientFactory;
    }
}
