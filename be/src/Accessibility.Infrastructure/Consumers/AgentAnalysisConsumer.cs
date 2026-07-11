using Accessibility.Application.AI;
using Accessibility.Application.AI.Models;
using Accessibility.Application.Common.Interfaces;
using Accessibility.Application.IntegrationEvents;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Accessibility.Infrastructure.Consumers;

public class AgentAnalysisConsumer : IConsumer<TranscriptAnalyzed>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IConversationAgent _agent;
    private readonly ILogger<AgentAnalysisConsumer> _logger;
    private readonly IPublishEndpoint _publishEndpoint;

    public AgentAnalysisConsumer(IApplicationDbContext dbContext, IConversationAgent agent, ILogger<AgentAnalysisConsumer> logger, IPublishEndpoint publishEndpoint)
    {
        _dbContext = dbContext;
        _agent = agent;
        _logger = logger;
        _publishEndpoint = publishEndpoint;
    }

    public async Task Consume(ConsumeContext<TranscriptAnalyzed> context)
    {
        var analysis = await _dbContext.AgentAnalyses.FirstOrDefaultAsync(x => x.Id == context.Message.AgentAnalysisId, context.CancellationToken);
        if (analysis is null)
        {
            return;
        }

        var transcript = await _dbContext.TranscriptSegments.FirstOrDefaultAsync(x => x.Id == analysis.TranscriptSegmentId, context.CancellationToken);
        if (transcript is null)
        {
            return;
        }

        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == analysis.ConversationId, context.CancellationToken);
        var user = await _dbContext.Users.FirstOrDefaultAsync(x => x.Id == conversation!.UserId, context.CancellationToken);

        var agentResult = await _agent.AnalyzeAsync(new AgentContext
        {
            ConversationId = analysis.ConversationId,
            TranscriptSegmentId = analysis.TranscriptSegmentId,
            TranscriptText = transcript.OriginalText,
            Speaker = transcript.Speaker.ToString(),
            Timestamp = transcript.StartedAt,
            UserTimeZone = user?.TimeZone ?? "Asia/Ho_Chi_Minh",
            UserPreferredLanguage = user?.PreferredLanguage ?? "vi"
        }, context.CancellationToken);

        analysis.Summary = agentResult.Summary;
        analysis.Category = agentResult.Category;
        analysis.Importance = agentResult.Importance;
        analysis.DetectedLanguage = agentResult.DetectedLanguage;
        analysis.TranslatedText = agentResult.TranslatedText;
        analysis.SuggestedRepliesJson = JsonSerializer.Serialize(agentResult.SuggestedReplies);
        analysis.RawAgentResponseJson = agentResult.RawAgentResponseJson;
        analysis.ModelName = agentResult.ModelName;
        analysis.Status = AnalysisStatus.Completed;
        analysis.CompletedAt = DateTimeOffset.UtcNow;

        var createdActions = new List<ProposedAction>();
        foreach (var action in agentResult.Actions)
        {
            var idempotencyKey = ProposedAction.GenerateIdempotencyKey(
                conversation!.UserId,
                action.Type,
                action.Title,
                action.ScheduledAt,
                action.Location);

            var existing = await _dbContext.ProposedActions.FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey && x.Status != ProposedActionStatus.Rejected && x.Status != ProposedActionStatus.Cancelled, context.CancellationToken);
            if (existing is not null)
            {
                continue;
            }

            var proposedAction = new ProposedAction
            {
                ConversationId = analysis.ConversationId,
                AgentAnalysisId = analysis.Id,
                ActionType = action.Type,
                Title = action.Title,
                Description = action.Description,
                ScheduledAt = action.ScheduledAt,
                DueAt = action.DueAt,
                Location = action.Location,
                RecipientEmail = action.RecipientEmail,
                PayloadJson = JsonSerializer.Serialize(action),
                RequiresConfirmation = action.RequiresConfirmation,
                IdempotencyKey = idempotencyKey,
                Status = action.RequiresConfirmation ? ProposedActionStatus.Proposed : ProposedActionStatus.Confirmed
            };

            createdActions.Add(proposedAction);
            await _dbContext.ProposedActions.AddAsync(proposedAction, context.CancellationToken);
        }

        await _dbContext.SaveChangesAsync(context.CancellationToken);
        foreach (var action in createdActions)
        {
            await _publishEndpoint.Publish(new ProposedActionCreated(action.Id));
        }

        _logger.LogInformation("Agent analysis completed for conversation {ConversationId}", analysis.ConversationId);
    }
}
