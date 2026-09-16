using System.Runtime.CompilerServices;

// WelcomeBonusConsumer.HandleWithRetryAndDeadLetter is internal specifically so
// RewardsService.IntegrationTests can call it directly against a real database
// without needing a Kafka broker to drive it.
[assembly: InternalsVisibleTo("RewardsService.IntegrationTests")]
