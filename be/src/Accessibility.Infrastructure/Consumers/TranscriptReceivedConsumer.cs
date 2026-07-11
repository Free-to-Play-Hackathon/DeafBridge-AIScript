using Accessibility.Application.Common.Interfaces;
using Accessibility.Application.IntegrationEvents;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accessibility.Infrastructure.Consumers;

public class TranscriptReceivedConsumer : IConsumer<TranscriptReceived>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ILogger<TranscriptReceivedConsumer> _logger;
    private readonly IPublishEndpoint _publishEndpoint;

    public TranscriptReceivedConsumer(IApplicationDbContext dbContext, ILogger<TranscriptReceivedConsumer> logger, IPublishEndpoint publishEndpoint)
    {
        _dbContext = dbContext;
        _logger = logger;
        _publishEndpoint = publishEndpoint;
    }

    public async Task Consume(ConsumeContext<TranscriptReceived> context)
    {
        var transcript = await _dbContext.TranscriptSegments.FirstOrDefaultAsync(x => x.Id == context.Message.TranscriptSegmentId, context.CancellationToken);
        if (transcript is null)
        {
            _logger.LogWarning("Transcript {TranscriptId} was not found for analysis", context.Message.TranscriptSegmentId);
            return;
        }

        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == transcript.ConversationId, context.CancellationToken);
        if (conversation is null)
        {
            return;
        }

        var analysis = new AgentAnalysis
        {
            ConversationId = conversation.Id,
            TranscriptSegmentId = transcript.Id,
            Summary = "Pending analysis",
            Status = AnalysisStatus.Pending,
            DetectedLanguage = transcript.Language
        };

        await _dbContext.AgentAnalyses.AddAsync(analysis, context.CancellationToken);
        await _dbContext.SaveChangesAsync(context.CancellationToken);
        await _publishEndpoint.Publish(new TranscriptAnalyzed(transcript.Id, analysis.Id), context.CancellationToken);

        _logger.LogInformation("Queued analysis for transcript {TranscriptId} conversation {ConversationId}", transcript.Id, conversation.Id);
    }
}
