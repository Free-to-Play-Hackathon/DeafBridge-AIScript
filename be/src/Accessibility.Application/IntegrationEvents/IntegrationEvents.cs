namespace Accessibility.Application.IntegrationEvents;

public record TranscriptReceived(Guid TranscriptSegmentId);
public record TranscriptAnalysisRequested(Guid TranscriptSegmentId);
public record TranscriptAnalyzed(Guid TranscriptSegmentId, Guid AgentAnalysisId);
public record ProposedActionCreated(Guid ProposedActionId);
public record ProposedActionConfirmed(Guid ProposedActionId);
public record ProposedActionRejected(Guid ProposedActionId);
public record NoteCreationRequested(Guid ProposedActionId);
public record TaskCreationRequested(Guid ProposedActionId);
public record AppointmentCreationRequested(Guid ProposedActionId);
public record ReminderSchedulingRequested(Guid ProposedActionId);
public record ReminderDue(Guid ReminderId);
public record EmailSendRequested(Guid ReminderId, string RecipientEmail, string Title, string Description, DateTimeOffset ScheduledAt, string Location);
public record EmailSent(Guid ReminderId);
public record EmailFailed(Guid ReminderId, string Reason);
