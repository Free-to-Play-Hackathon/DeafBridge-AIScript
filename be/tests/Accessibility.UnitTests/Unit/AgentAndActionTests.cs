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
using Microsoft.EntityFrameworkCore;
using Moq;
using System.Security.Claims;
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
}
