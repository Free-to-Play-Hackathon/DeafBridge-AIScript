using Accessibility.Application.IntegrationEvents;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accessibility.Infrastructure.Persistence;

public static class DemoDataSeeder
{
    private static readonly Guid DemoUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DemoConversationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DemoTranscriptId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string DemoTranscriptText = "You need to go to Cho Ray Hospital tomorrow at 8 AM for your medical appointment. Please remember to bring your insurance card, and I can remind you 30 minutes before.";

    public static async Task SeedAsync(AccessibilityDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Email == "local@example.com", cancellationToken);
        if (user is null)
        {
            user = new User
            {
                Id = DemoUserId,
                Email = "local@example.com",
                DisplayName = "Demo User",
                PreferredLanguage = "en",
                TimeZone = "Asia/Ho_Chi_Minh"
            };

            await dbContext.Users.AddAsync(user, cancellationToken);
        }

        var conversation = await dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == DemoConversationId, cancellationToken);
        if (conversation is null)
        {
            conversation = new Conversation
            {
                Id = DemoConversationId,
                UserId = user.Id,
                Title = "Demo AI Agent Tool Planning",
                Status = ConversationStatus.Active,
                StartedAt = DateTimeOffset.UtcNow
            };

            await dbContext.Conversations.AddAsync(conversation, cancellationToken);
        }

        var transcriptChanged = false;
        var transcript = await dbContext.TranscriptSegments.FirstOrDefaultAsync(x => x.Id == DemoTranscriptId, cancellationToken);
        if (transcript is null)
        {
            transcript = new TranscriptSegment
            {
                Id = DemoTranscriptId,
                ConversationId = DemoConversationId,
                Speaker = Speaker.HearingUser,
                Language = "en",
                OriginalText = DemoTranscriptText,
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = DateTimeOffset.UtcNow.AddSeconds(8),
                SequenceNumber = 1
            };

            await dbContext.TranscriptSegments.AddAsync(transcript, cancellationToken);
        }
        else if (transcript.OriginalText != DemoTranscriptText)
        {
            transcript.OriginalText = DemoTranscriptText;
            transcriptChanged = true;
        }

        if (transcriptChanged)
        {
            var demoActions = await dbContext.ProposedActions
                .Where(x => x.ConversationId == DemoConversationId)
                .ToListAsync(cancellationToken);
            dbContext.ProposedActions.RemoveRange(demoActions);

            var demoAnalyses = await dbContext.AgentAnalyses
                .Where(x => x.TranscriptSegmentId == DemoTranscriptId)
                .ToListAsync(cancellationToken);
            dbContext.AgentAnalyses.RemoveRange(demoAnalyses);
        }

        var hasAnalysis = !transcriptChanged && await dbContext.AgentAnalyses.AnyAsync(x => x.TranscriptSegmentId == DemoTranscriptId, cancellationToken);
        var outboxPayload = JsonSerializer.Serialize(new TranscriptReceived(DemoTranscriptId));
        var hasPendingOutbox = await dbContext.OutboxMessages.AnyAsync(
            x => x.Type == typeof(TranscriptReceived).AssemblyQualifiedName && x.Payload == outboxPayload && x.ProcessedAt == null,
            cancellationToken);

        if (!hasAnalysis && !hasPendingOutbox)
        {
            await dbContext.OutboxMessages.AddAsync(new OutboxMessage
            {
                Type = typeof(TranscriptReceived).AssemblyQualifiedName!,
                Payload = outboxPayload,
                OccurredAt = DateTimeOffset.UtcNow,
                RetryCount = 0
            }, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
