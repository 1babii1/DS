using System.Runtime.CompilerServices;

// DomainEventsConsumer.HandleWithRetryAndDeadLetter is internal specifically so
// SearchService.IntegrationTests can call it directly against a real database and a real
// (Testcontainers) Elasticsearch without needing a Kafka broker to drive it.
[assembly: InternalsVisibleTo("SearchService.IntegrationTests")]
