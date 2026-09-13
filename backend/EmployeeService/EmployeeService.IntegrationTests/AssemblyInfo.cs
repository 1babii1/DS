using Xunit;

// One shared database per fixture, cleaned via Respawn between tests. Parallel test
// execution races two Respawn resets against each other and Postgres deadlocks -
// same reasoning as DirectoryService.IntegrationTests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]