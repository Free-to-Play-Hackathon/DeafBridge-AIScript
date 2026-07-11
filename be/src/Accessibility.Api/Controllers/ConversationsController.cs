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
[Route("api/conversations")]
public class ConversationsController : ControllerBase
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IPublishEndpoint _publishEndpoint;

    public ConversationsController(IApplicationDbContext dbContext, IPublishEndpoint publishEndpoint)
    {
        _dbContext = dbContext;
        _publishEndpoint = publishEndpoint;
    }

    [HttpPost]
    public async Task<IActionResult> CreateConversation([FromBody] CreateConversationRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var conversation = new Conversation
        {
            UserId = user.Id,
            Title = request.Title,
            Status = ConversationStatus.Active,
            StartedAt = DateTimeOffset.UtcNow
        };

        await _dbContext.Conversations.AddAsync(conversation, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetConversation), new { id = conversation.Id }, new { id = conversation.Id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetConversation(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id, cancellationToken);
        if (conversation is null) return NotFound();
        return Ok(new ConversationSummaryDto(conversation.Id, conversation.Title, conversation.Status.ToString(), conversation.StartedAt, conversation.EndedAt, conversation.CreatedAt));
    }

    [HttpGet("{id:guid}/timeline")]
    public async Task<IActionResult> GetTimeline(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id, cancellationToken);
        if (conversation is null) return NotFound();

        var timeline = await _dbContext.TranscriptSegments.Where(x => x.ConversationId == id)
            .OrderBy(x => x.SequenceNumber)
            .Select(x => new { x.Id, x.OriginalText, x.Speaker, x.CreatedAt })
            .ToListAsync(cancellationToken);

        return Ok(timeline);
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> CompleteConversation(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id, cancellationToken);
        if (conversation is null) return NotFound();
        conversation.Status = ConversationStatus.Completed;
        conversation.EndedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    [HttpPost("{conversationId:guid}/transcripts")]
    public async Task<IActionResult> SubmitTranscript(Guid conversationId, [FromBody] TranscriptSubmissionRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == user.Id, cancellationToken);
        if (conversation is null) return NotFound();

        if (!Enum.TryParse<Speaker>(request.Speaker, true, out var speaker))
        {
            return BadRequest(new
            {
                error = "Invalid speaker",
                allowedValues = new[] { Speaker.DeafUser.ToString(), Speaker.HearingUser.ToString(), Speaker.System.ToString() }
            });
        }

        if (string.IsNullOrWhiteSpace(request.OriginalText))
        {
            return BadRequest(new { error = "originalText is required" });
        }

        var segment = new TranscriptSegment
        {
            ConversationId = conversation.Id,
            Speaker = speaker,
            OriginalText = request.OriginalText,
            Language = request.Language,
            StartedAt = request.StartedAt,
            EndedAt = request.EndedAt,
            SequenceNumber = request.SequenceNumber
        };

        var outbox = new OutboxMessage
        {
            Type = typeof(TranscriptReceived).AssemblyQualifiedName!,
            Payload = JsonSerializer.Serialize(new TranscriptReceived(segment.Id)),
            OccurredAt = DateTimeOffset.UtcNow,
            RetryCount = 0
        };

        await _dbContext.TranscriptSegments.AddAsync(segment, cancellationToken);
        await _dbContext.OutboxMessages.AddAsync(outbox, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Accepted();
    }

    [HttpGet("{conversationId:guid}/transcripts")]
    public async Task<IActionResult> GetTranscripts(Guid conversationId, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == user.Id, cancellationToken);
        if (conversation is null) return NotFound();

        var transcripts = await _dbContext.TranscriptSegments.Where(x => x.ConversationId == conversationId)
            .OrderBy(x => x.SequenceNumber)
            .Select(x => new TranscriptSegmentDto(x.Id, x.ConversationId, x.Speaker.ToString(), x.OriginalText, x.Language, x.StartedAt, x.EndedAt, x.SequenceNumber, x.CreatedAt))
            .ToListAsync(cancellationToken);

        return Ok(transcripts);
    }

    [HttpGet("{conversationId:guid}/analyses")]
    public async Task<IActionResult> GetAnalyses(Guid conversationId, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == user.Id, cancellationToken);
        if (conversation is null) return NotFound();

        var analyses = await _dbContext.AgentAnalyses.Where(x => x.ConversationId == conversationId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new AgentAnalysisDto(x.Id, x.ConversationId, x.TranscriptSegmentId, x.Summary, x.Category.ToString(), x.Importance.ToString(), x.DetectedLanguage, x.TranslatedText, x.SuggestedRepliesJson, x.RawAgentResponseJson, x.ModelName, x.Status.ToString(), x.CreatedAt, x.CompletedAt))
            .ToListAsync(cancellationToken);
        return Ok(analyses);
    }

    [HttpGet("/api/analyses/{analysisId:guid}")]
    public async Task<IActionResult> GetAnalysis(Guid analysisId, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var analysis = await _dbContext.AgentAnalyses.FirstOrDefaultAsync(x => x.Id == analysisId, cancellationToken);
        if (analysis is null) return NotFound();
        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == analysis.ConversationId && x.UserId == user.Id, cancellationToken);
        if (conversation is null) return NotFound();
        return Ok(new AgentAnalysisDto(analysis.Id, analysis.ConversationId, analysis.TranscriptSegmentId, analysis.Summary, analysis.Category.ToString(), analysis.Importance.ToString(), analysis.DetectedLanguage, analysis.TranslatedText, analysis.SuggestedRepliesJson, analysis.RawAgentResponseJson, analysis.ModelName, analysis.Status.ToString(), analysis.CreatedAt, analysis.CompletedAt));
    }

    [HttpPost("{conversationId:guid}/reanalyze")]
    public async Task<IActionResult> Reanalyze(Guid conversationId, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var conversation = await _dbContext.Conversations.FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == user.Id, cancellationToken);
        if (conversation is null) return NotFound();
        var latestTranscript = await _dbContext.TranscriptSegments.Where(x => x.ConversationId == conversationId).OrderByDescending(x => x.SequenceNumber).FirstOrDefaultAsync(cancellationToken);
        if (latestTranscript is null) return NotFound();
        var analysis = new AgentAnalysis
        {
            ConversationId = conversationId,
            TranscriptSegmentId = latestTranscript.Id,
            Summary = "Pending analysis",
            Status = AnalysisStatus.Pending,
            DetectedLanguage = latestTranscript.Language
        };
        await _dbContext.AgentAnalyses.AddAsync(analysis, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _publishEndpoint.Publish(new TranscriptAnalyzed(latestTranscript.Id, analysis.Id), cancellationToken);
        return Accepted();
    }

    private async Task<User> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var userEmail = User?.Identity?.Name ?? "local@example.com";
        var user = await _dbContext.Users.FirstOrDefaultAsync(x => x.Email == userEmail, cancellationToken);
        if (user is null)
        {
            user = new User
            {
                Email = userEmail,
                DisplayName = userEmail,
                PreferredLanguage = "vi",
                TimeZone = "Asia/Ho_Chi_Minh"
            };
            await _dbContext.Users.AddAsync(user, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        return user;
    }
}
