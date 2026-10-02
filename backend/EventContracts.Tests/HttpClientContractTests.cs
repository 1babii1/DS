using System.Collections;
using System.Reflection;
using AuditService.Web.Controllers;
using DirectoryService.Contracts.Response.Department;
using DirectoryService.Contracts.Response.Position;
using EmployeeService.Application.Employees.Queries;
using McpServer.Api;
using SearchService.Web.Controllers;
using Shared;

namespace EventContracts.Tests;

// The same idea as the event reader schemas, for the synchronous boundary: a client that reads another service's JSON keeps
// its own record of what it reads (that is what lets it deploy alone), and nothing at compile time links the record to the
// provider. System.Text.Json leaves a property it cannot find at its default, so a rename on the provider turns into a
// client that quietly sees zeros and nulls. Each pair below is a consumer record and the provider type it is read from; the
// rule is that everything the consumer reads exists on the provider under the same name with a type the consumer can hold.
//
// This checks shapes, not behaviour: it cannot say what a field means or that an endpoint exists. The behaviour of the
// calls (URLs, tokens, failures) is what McpServer's own tests pin.
public class HttpClientContractTests
{
    private static Type Private(Type owner, string name) =>
        owner.GetNestedType(name, BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException($"{owner.Name} has no nested type {name}: the client changed, update the contract table");

    // (consumer's record, the provider type it is deserialized from, what it is)
    public static IEnumerable<object[]> Pairs()
    {
        yield return Pair(Private(typeof(DirectoryApiClient), "HierarchyDto"), typeof(ReadDepartmentHierarchyDto), "Directory: roots / subtree node");
        yield return Pair(Private(typeof(DirectoryApiClient), "DepartmentDto"), typeof(ReadDepartmentDto), "Directory: department by id");
        yield return Pair(Private(typeof(DirectoryApiClient), "SubtreeDto"), typeof(DepartmentSubtreeDto), "Directory: subtree");
        yield return Pair(Private(typeof(DirectoryApiClient), "PositionDto"), typeof(ReadPositionDto), "Directory: a position of a department");
        yield return Pair(Private(typeof(DirectoryApiClient), "PositionsPageDto"), typeof(PagedResponse<ReadPositionDto>), "Directory: positions page");
        yield return Pair(Private(typeof(EmployeeApiClient), "EmployeeDto"), typeof(EmployeeDto), "Employee: one employee");
        yield return Pair(Private(typeof(EmployeeApiClient), "PagedDto"), typeof(PagedResponse<EmployeeDto>), "Employee: a page of employees");
        yield return Pair(Private(typeof(SearchApiClient), "HitDto"), typeof(SearchResultDto), "Search: a hit");
        yield return Pair(Private(typeof(SearchApiClient), "SearchDto"), typeof(SearchResponse), "Search: the answer");
        yield return Pair(typeof(OrgSnapshotDto), typeof(OrgChartResponse), "Audit: org snapshot");
        yield return Pair(typeof(OrgSnapshotDepartment), typeof(AuditService.Domain.OrgDepartment), "Audit: a department of the snapshot");
        yield return Pair(typeof(OrgSnapshotPerson), typeof(AuditService.Domain.OrgPerson), "Audit: a person of the snapshot");
    }

    private static object[] Pair(Type consumer, Type provider, string what) => [consumer, provider, what];

    [Fact]
    public void The_table_covers_every_response_record_the_clients_keep()
    {
        var covered = Pairs().Select(p => (Type)p[0]).ToHashSet();
        var kept = new[] { typeof(DirectoryApiClient), typeof(EmployeeApiClient), typeof(SearchApiClient) }
            .SelectMany(c => c.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            .Where(t => t.Name.EndsWith("Dto", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(kept);
        Assert.All(kept, t => Assert.True(covered.Contains(t), $"{t.DeclaringType!.Name}.{t.Name} is read from JSON but has no provider in the contract table"));
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Everything_a_client_reads_exists_on_the_provider_with_a_type_it_can_hold(Type consumer, Type provider, string what)
    {
        Assert.Empty(Mismatches(consumer, provider));
        _ = what;
    }

    [Fact]
    public void The_check_would_catch_a_renamed_field_a_narrowed_type_and_a_lost_optionality()
    {
        Assert.Contains("Missing", Mismatches(typeof(Wants), typeof(HasRenamed)).Single());
        Assert.Contains("type", Mismatches(typeof(Wants), typeof(HasOtherType)).Single());
        Assert.Contains("optional", Mismatches(typeof(Wants), typeof(HasNullable)).Single());
        Assert.Empty(Mismatches(typeof(Wants), typeof(HasMore)));
        Assert.Empty(Mismatches(typeof(WantsNullable), typeof(HasMore)));
    }

    private sealed record Wants(Guid Id, string Name);

    private sealed record WantsNullable(Guid Id, string? Name);

    private sealed record HasMore(Guid Id, string Name, string Extra);

    private sealed record HasRenamed(Guid Id, string FullName);

    private sealed record HasOtherType(Guid Id, int Name);

    private sealed record HasNullable(Guid Id, string? Name);

    private static readonly NullabilityInfoContext Nullability = new();

    internal static List<string> Mismatches(Type consumer, Type provider)
    {
        var problems = new List<string>();
        var providerProperties = provider.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var wanted in consumer.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!providerProperties.TryGetValue(wanted.Name, out var offered))
            {
                problems.Add($"Missing: {consumer.Name}.{wanted.Name} is read but {provider.Name} has no such property");
                continue;
            }

            if (ElementOrSelf(wanted.PropertyType) is var (wantedKind, wantedType)
                && ElementOrSelf(offered.PropertyType) is var (offeredKind, offeredType)
                && (wantedKind != offeredKind || !SameShape(wantedType, offeredType)))
            {
                problems.Add($"{consumer.Name}.{wanted.Name}: type {wanted.PropertyType.Name} cannot hold {offered.PropertyType.Name} from {provider.Name}");
                continue;
            }

            if (IsOptional(offered) && !IsOptional(wanted))
            {
                problems.Add($"{consumer.Name}.{wanted.Name}: {provider.Name} may send it as null (optional) but the client treats it as always present");
            }
        }

        return problems;
    }

    private static bool IsOptional(PropertyInfo property) =>
        Nullable.GetUnderlyingType(property.PropertyType) is not null
        || (!property.PropertyType.IsValueType && Nullability.Create(property).ReadState == NullabilityState.Nullable);

    // A collection is compared by what it holds; anything else by itself.
    private static (bool IsCollection, Type Type) ElementOrSelf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var element = type.IsArray ? type.GetElementType()! : type.GetGenericArguments().FirstOrDefault() ?? typeof(object);
            return (true, Nullable.GetUnderlyingType(element) ?? element);
        }

        return (false, type);
    }

    // Scalars must be the same type; records the two sides model on their own are matched by their own pair elsewhere.
    private static bool SameShape(Type consumer, Type provider) =>
        consumer == provider
        || (!IsScalar(consumer) && !IsScalar(provider))
        || (consumer == typeof(double) && provider == typeof(float))
        || (consumer == typeof(decimal) && provider == typeof(double));

    private static bool IsScalar(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Guid) || type == typeof(decimal)
        || type == typeof(DateTime) || type == typeof(DateTimeOffset);
}
