using Accessibility.Domain.Common;

namespace Accessibility.Domain.Entities;

/// <summary>
/// Transactional outbox message for reliable event publishing.
/// </summary>
public class OutboxMessage : BaseEntity
{
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? Error { get; set; }
}
