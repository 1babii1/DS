using Xunit;

// Все классы тестов работают с одной базой и чистят её через Respawn в DisposeAsync.
// При параллельном выполнении сбросы пересекаются и Postgres ловит deadlock,
// поэтому параллелизм между коллекциями отключён.
[assembly: CollectionBehavior(DisableTestParallelization = true)]