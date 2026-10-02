using System.Reflection;
using Avro;

namespace EventContracts.Tests;

// The Avro schema of an event (ADR 0023) is what the registry and CI reason about; the C# record is what the producer
// actually serializes. This keeps the two the same: every producer event has a schema file, the schema parses, and its
// fields are the record's properties, with "optional in C#" meaning "a union with null" in the schema. Without it the
// compatibility gate would be checking a description of the event, not the event.
public class ProducerSchemaTests
{
    private static readonly Assembly[] Producers =
    [
        typeof(EmployeeService.Application.IntegrationEvents.EmployeeHiredEvent).Assembly,
        typeof(AuthService.Application.IntegrationEvents.AccountProvisionedEvent).Assembly,
        typeof(DirectoryService.Application.IntegrationEvents.DepartmentCreatedEvent).Assembly,
        typeof(RewardsService.Infrastructure.IntegrationEvents.CurrencyGrantedEvent).Assembly,
    ];

    private static readonly NullabilityInfoContext Nullability = new();

    public static IEnumerable<object[]> Events() => Producers
        .SelectMany(a => a.GetExportedTypes())
        .Where(t => t.Name.EndsWith("Event", StringComparison.Ordinal)
            && t.Namespace is { } ns && ns.EndsWith(".IntegrationEvents", StringComparison.Ordinal)
            && t.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) is not null)
        .OrderBy(t => t.Name)
        .Select(t => new object[] { t });

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "backend.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("backend.slnx not found above the test output");
    }

    private static string SchemaPath(Type eventType)
    {
        var name = eventType.Name[..^"Event".Length];
        var project = eventType.Assembly.GetName().Name!;
        var matches = Directory.GetFiles(RepositoryRoot(), name + ".avsc", SearchOption.AllDirectories)
            .Where(p => p.Replace('\\', '/').Contains($"/{project}/IntegrationEvents/Schemas/", StringComparison.Ordinal))
            .ToList();
        Assert.True(matches.Count == 1, $"{eventType.Name} needs exactly one schema, {name}.avsc, in {project}/IntegrationEvents/Schemas; found {matches.Count}");
        return matches[0];
    }

    [Fact]
    public void The_discovery_finds_every_producer_event()
    {
        // Guards the test itself: a changed namespace convention must not silently match nothing.
        Assert.True(Events().Count() >= 22);
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void Every_producer_event_has_a_schema_that_parses_and_matches_the_record(Type eventType)
    {
        var path = SchemaPath(eventType);
        var schema = (RecordSchema)Avro.Schema.Parse(File.ReadAllText(path));

        Assert.Equal(eventType.Name[..^"Event".Length], schema.Name);

        var properties = eventType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(p => p.GetMethod is not null)
            .ToList();
        Assert.Equal(
            properties.Select(p => p.Name).Order(StringComparer.Ordinal),
            schema.Fields.Select(f => f.Name).Order(StringComparer.Ordinal));

        foreach (var property in properties)
        {
            var field = schema.Fields.Single(f => f.Name == property.Name);
            var optionalInCode = Nullable.GetUnderlyingType(property.PropertyType) is not null
                || (!property.PropertyType.IsValueType && Nullability.Create(property).ReadState == NullabilityState.Nullable);
            var nullableInSchema = field.Schema is UnionSchema union && union.Schemas.Any(s => s.Tag == Schema.Type.Null);

            Assert.True(
                optionalInCode == nullableInSchema,
                $"{eventType.Name}.{property.Name}: nullable in code = {optionalInCode}, null allowed in the schema = {nullableInSchema}");
            if (nullableInSchema)
            {
                Assert.True(field.DefaultValue is not null, $"{eventType.Name}.{property.Name}: an optional field needs a default (null)");
            }
        }
    }

    [Fact]
    public void Every_schema_file_belongs_to_an_event()
    {
        var known = Events().Select(e => ((Type)e[0]).Name[..^"Event".Length]).ToHashSet();
        var orphans = Directory.GetFiles(RepositoryRoot(), "*.avsc", SearchOption.AllDirectories)
            .Where(p => !p.Replace('\\', '/').Contains("/obj/") && !p.Replace('\\', '/').Contains("/bin/"))
            .Where(p => p.Replace('\\', '/').Contains("/IntegrationEvents/Schemas/"))
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(n => !known.Contains(n))
            .ToList();

        Assert.True(orphans.Count == 0, $"Schemas without a producer event record: {string.Join(", ", orphans)}");
    }
}
