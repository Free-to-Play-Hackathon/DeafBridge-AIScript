using Accessibility.Application.Common.Interfaces;
using Accessibility.Application.IntegrationEvents;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Infrastructure.Consumers;

public class CreateTaskConsumer : IConsumer<TaskCreationRequested>
{
    private readonly IApplicationDbContext _dbContext;

    public CreateTaskConsumer(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task Consume(ConsumeContext<TaskCreationRequested> context)
    {
        var action = await _dbContext.ProposedActions.FirstOrDefaultAsync(x => x.Id == context.Message.ProposedActionId, context.CancellationToken);
        if (action is null || action.Status == ProposedActionStatus.Completed)
        {
            return;
        }

        var conversation = await _dbContext.Conversations.FirstAsync(x => x.Id == action.ConversationId, context.CancellationToken);
        var task = new UserTask
        {
            UserId = conversation.UserId,
            ConversationId = action.ConversationId,
            Title = action.Title,
            Description = action.Description,
            DueAt = action.DueAt ?? action.ScheduledAt,
            Priority = TaskPriority.Normal,
            Status = UserTaskStatus.Pending
        };

        await _dbContext.UserTasks.AddAsync(task, context.CancellationToken);
        action.Status = ProposedActionStatus.Completed;
        action.ExecutedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(context.CancellationToken);
    }
}
