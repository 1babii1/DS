namespace Shared.IntegrationEvents;

/// <summary>
/// Written to a service's own outbox when an operator puts a parked message back, so the audit log shows who did it.
/// Every service can publish it, which is why its schema lives with the shared code, not with one service.
/// </summary>
public record OutboxMessageRedrivenEvent(Guid MessageId, string MessageType, string RedrivenBy);
