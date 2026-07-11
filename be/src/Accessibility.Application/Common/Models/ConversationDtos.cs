using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;

namespace Accessibility.Application.Common.Models;

public record TranscriptSubmissionRequest(
    string Speaker,
    string OriginalText,
    string Language,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int SequenceNumber);

public record CreateConversationRequest(string Title);

public record ConfirmActionRequest(string? Title, DateTimeOffset? ScheduledAt, string? Location, string? RecipientEmail);

public record ConversationSummaryDto(
    Guid Id,
    string Title,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset CreatedAt);

public record TranscriptSegmentDto(
    Guid Id,
    Guid ConversationId,
    string Speaker,
    string OriginalText,
    string Language,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int SequenceNumber,
    DateTimeOffset CreatedAt);

public record AgentAnalysisDto(
    Guid Id,
    Guid ConversationId,
    Guid TranscriptSegmentId,
    string Summary,
    string Category,
    string Importance,
    string? DetectedLanguage,
    string? TranslatedText,
    string? SuggestedRepliesJson,
    string? RawAgentResponseJson,
    string? ModelName,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public record ProposedActionDto(
    Guid Id,
    Guid ConversationId,
    Guid AgentAnalysisId,
    string ActionType,
    string Title,
    string? Description,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset? DueAt,
    string? Location,
    string? RecipientEmail,
    string? PayloadJson,
    string Status,
    bool RequiresConfirmation,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ExecutedAt,
    string? FailureReason,
    string IdempotencyKey);

public record DashboardDto(
    List<Note> ImportantNotes,
    List<UserTask> PendingTasks,
    List<Appointment> TodayAppointments,
    List<Reminder> UpcomingReminders);
