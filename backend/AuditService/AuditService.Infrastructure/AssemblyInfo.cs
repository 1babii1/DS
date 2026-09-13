using System.Runtime.CompilerServices;

// AuditConsumer.HandleWithRetryAndDeadLetter is internal specifically so
// AuditService.IntegrationTests can call it directly against a real database
// without needing a Kafka broker to drive it.
[assembly: InternalsVisibleTo("AuditService.IntegrationTests")]
