using Accessibility.Application.Common.Interfaces;
using Accessibility.Application.Common.Models;
using Accessibility.Application.IntegrationEvents;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accessibility.Api.Controllers;

[ApiController]
[Route("api/proposed-actions")]
public class ProposedActionsController : ControllerBase
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IPublishEndpoint _publishEndpoint;

    public ProposedActionsController(IApplicationDbContext dbContext, IPublishEndpoint publishEndpoint)
    {
        _dbContext = dbContext;
        _publishEndpoint = publishEndpoint;
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var action = await _dbContext.ProposedActions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (action is null) return NotFound();
        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == action.ConversationId && x.UserId == user.Id, cancellationToken);
        if (conversation is null) return Forbid();
        return Ok(Map(action));
    }

    [HttpGet("/api/conversations/{conversationId:guid}/proposed-actions")]
    public async Task<IActionResult> GetByConversation(Guid conversationId, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == user.Id, cancellationToken);
        if (conversation is null) return Forbid();
        var actions = await _dbContext.ProposedActions.Where(x => x.ConversationId == conversationId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new ProposedActionDto(x.Id, x.ConversationId, x.AgentAnalysisId, x.ActionType.ToString(), x.Title, x.Description, x.ScheduledAt, x.DueAt, x.Location, x.RecipientEmail, x.PayloadJson, x.Status.ToString(), x.RequiresConfirmation, x.CreatedAt, x.ConfirmedAt, x.ExecutedAt, x.FailureReason, x.IdempotencyKey))
            .ToListAsync(cancellationToken);
        return Ok(actions);
    }

    [HttpPost("{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, [FromBody] ConfirmActionRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var action = await _dbContext.ProposedActions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (action is null) return NotFound();
        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == action.ConversationId && x.UserId == user.Id, cancellationToken);
        if (conversation is null) return Forbid();

        action.Title = request.Title ?? action.Title;
        action.ScheduledAt = request.ScheduledAt ?? action.ScheduledAt;
        action.Location = request.Location ?? action.Location;
        action.RecipientEmail = request.RecipientEmail ?? action.RecipientEmail;
        action.Status = ProposedActionStatus.Confirmed;
        action.ConfirmedAt = DateTimeOffset.UtcNow;
        await _dbContext.OutboxMessages.AddAsync(new OutboxMessage
        {
            Type = typeof(ProposedActionConfirmed).AssemblyQualifiedName!,
            Payload = JsonSerializer.Serialize(new ProposedActionConfirmed(action.Id)),
            OccurredAt = DateTimeOffset.UtcNow,
            RetryCount = 0
        }, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(Map(action));
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var action = await _dbContext.ProposedActions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (action is null) return NotFound();
        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == action.ConversationId && x.UserId == user.Id, cancellationToken);
        if (conversation is null) return Forbid();

        action.Status = ProposedActionStatus.Rejected;
        await _dbContext.OutboxMessages.AddAsync(new OutboxMessage
        {
            Type = typeof(ProposedActionRejected).AssemblyQualifiedName!,
            Payload = JsonSerializer.Serialize(new ProposedActionRejected(action.Id)),
            OccurredAt = DateTimeOffset.UtcNow,
            RetryCount = 0
        }, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(Map(action));
    }

    private async Task<User> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var userEmail = User?.Identity?.Name ?? "local@example.com";
        var user = await _dbContext.Users.FirstOrDefaultAsync(x => x.Email == userEmail, cancellationToken);
        if (user is null)
        {
            user = new User{ Email = userEmail, DisplayName = userEmail, PreferredLanguage = "vi", TimeZone = "Asia/Ho_Chi_Minh"};
            await _dbContext.Users.AddAsync(user, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        return user;
    }

    private static ProposedActionDto Map(ProposedAction action) => new(
        action.Id,
        action.ConversationId,
        action.AgentAnalysisId,
        action.ActionType.ToString(),
        action.Title,
        action.Description,
        action.ScheduledAt,
        action.DueAt,
        action.Location,
        action.RecipientEmail,
        action.PayloadJson,
        action.Status.ToString(),
        action.RequiresConfirmation,
        action.CreatedAt,
        action.ConfirmedAt,
        action.ExecutedAt,
        action.FailureReason,
        action.IdempotencyKey);
}
