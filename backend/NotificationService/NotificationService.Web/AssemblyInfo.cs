using System.Runtime.CompilerServices;

// DomainEventsConsumer.HandleWithRetryAndDeadLetter is internal specifically so
// NotificationService.IntegrationTests can call it directly against a real database
// without needing a Kafka broker to drive it.
[assembly: InternalsVisibleTo("NotificationService.IntegrationTests")]
