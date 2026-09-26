using System.Reflection;

namespace EventContracts.Tests;

// Every consumer here deserializes each event into its own local copy of the record, on purpose:
// a consumer owning its expectation of the payload is what keeps services independently
// deployable. The cost is that nothing at compile time links the copy to the producer, and an
// unknown or renamed field does not throw - System.Text.Json just leaves it at its default, so
// the consumer quietly does the wrong thing.
//
// This closes that gap without giving up the separate copies. By convention producers declare
// events in a *.IntegrationEvents namespace and consumers in a *.Consumers namespace under the
// same name, so the pairs are found automatically; adding a new event or consumer needs no
// edit here, it is checked the moment it exists.
public class ConsumerContractTests
{
    private static readonly Assembly[] Assemblies =
    [
        typeof(EmployeeService.Application.IntegrationEvents.EmployeeHiredEvent).Assembly,
        typeof(AuthService.Application.IntegrationEvents.AccountProvisionedEvent).Assembly,
        typeof(DirectoryService.Application.IntegrationEvents.DepartmentCreatedEvent).Assembly,
        typeof(RewardsService.Infrastructure.IntegrationEvents.CurrencyGrantedEvent).Assembly,
        typeof(AuthService.Web.Consumers.EmployeeHiredEvent).Assembly,
        typeof(EmployeeService.Web.Consumers.AccountProvisionedEvent).Assembly,
        typeof(NotificationService.Web.Consumers.CurrencyGrantedEvent).Assembly,
        typeof(SearchService.Web.Consumers.EmployeeHiredEvent).Assembly,
        typeof(RewardsService.Infrastructure.Consumers.EmployeeHiredEvent).Assembly,
    ];

    private static IReadOnlyList<Type> EventTypes(string namespaceSegment) => Assemblies
        .Distinct()
        .SelectMany(a => a.GetExportedTypes())
        .Where(t => t.Name.EndsWith("Event", StringComparison.Ordinal)
            && t.Namespace is { } ns
            && ns.EndsWith("." + namespaceSegment, StringComparison.Ordinal)
            && t.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) is not null)
        .ToList();

    public static IEnumerable<object[]> Consumers() =>
        EventTypes("Consumers").Select(t => new object[] { t });

    [Fact]
    public void The_discovery_convention_finds_producers_and_consumers()
    {
        // Guards the test itself: if a namespace convention changed and discovery silently
        // matched nothing, every check below would pass while checking nothing.
        Assert.True(EventTypes("IntegrationEvents").Count >= 10);
        Assert.True(EventTypes("Consumers").Count >= 10);
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public void A_consumer_copy_has_a_producer_it_can_be_read_from(Type consumer)
    {
        var producers = EventTypes("IntegrationEvents").Where(p => p.Name == consumer.Name).ToList();

        Assert.True(
            producers.Count == 1,
            $"{consumer.FullName} has no single producer named {consumer.Name} in an IntegrationEvents namespace " +
            $"(found {producers.Count}). If the event was renamed, its consumers stopped receiving it.");
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public void Every_field_a_consumer_reads_exists_on_the_producer_with_a_compatible_type(Type consumer)
    {
        var producer = EventTypes("IntegrationEvents").Single(p => p.Name == consumer.Name);
        var producerProps = producer.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(p => p.Name, p => p.PropertyType, StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var field in consumer.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!producerProps.TryGetValue(field.Name, out var producedType))
            {
                // Deserialization is case-sensitive here, so a casing change counts as missing too.
                problems.Add($"'{field.Name}' is not on {producer.FullName}");
                continue;
            }

            if (!IsReadable(producedType, field.PropertyType))
            {
                problems.Add($"'{field.Name}' is {Describe(producedType)} on the producer but {Describe(field.PropertyType)} in the consumer");
            }
        }

        Assert.True(
            problems.Count == 0,
            $"{consumer.FullName} would silently misread {producer.FullName}: {string.Join("; ", problems)}");
    }

    // A value the consumer can hold without loss. The one dangerous direction is a producer that
    // may send null into a consumer field that cannot hold it: the read yields a default value
    // (Guid.Empty, 0) instead of failing, which is exactly the silent failure being guarded.
    private static bool IsReadable(Type produced, Type consumed)
    {
        if (produced == consumed)
        {
            return true;
        }

        var producedUnderlying = Nullable.GetUnderlyingType(produced);
        var consumedUnderlying = Nullable.GetUnderlyingType(consumed);

        // Guid -> Guid? is safe. Guid? -> Guid is not.
        return producedUnderlying is null && consumedUnderlying == produced;
    }

    private static string Describe(Type type) => Nullable.GetUnderlyingType(type) is { } u ? $"{u.Name}?" : type.Name;
}
