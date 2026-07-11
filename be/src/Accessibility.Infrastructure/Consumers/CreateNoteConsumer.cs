using Accessibility.Application.Common.Interfaces;
using Accessibility.Application.IntegrationEvents;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accessibility.Infrastructure.Consumers;

public class CreateNoteConsumer : IConsumer<NoteCreationRequested>
{
    private readonly IApplicationDbContext _dbContext;

    public CreateNoteConsumer(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task Consume(ConsumeContext<NoteCreationRequested> context)
    {
        var action = await _dbContext.ProposedActions.FirstOrDefaultAsync(x => x.Id == context.Message.ProposedActionId, context.CancellationToken);
        if (action is null || action.Status == ProposedActionStatus.Completed)
        {
            return;
        }

        var note = new Note
        {
            UserId = (await _dbContext.Conversations.FirstAsync(x => x.Id == action.ConversationId, context.CancellationToken)).UserId,
            ConversationId = action.ConversationId,
            Title = action.Title,
            Content = action.Description ?? string.Empty,
            Category = action.ActionType.ToString(),
            IsImportant = true,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _dbContext.Notes.AddAsync(note, context.CancellationToken);
        action.Status = ProposedActionStatus.Completed;
        action.ExecutedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(context.CancellationToken);
    }
}
