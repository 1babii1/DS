using Xunit;

// Same reasoning as the other two integration test projects: one shared database
// per fixture, Respawn resets race under parallel execution.
[assembly: CollectionBehavior(DisableTestParallelization = true)]