using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Shared.UnitTests;

// Fitness functions for the rule the whole architecture rests on: services are independent. They run over the source
// tree, so a change that quietly couples two services fails a test instead of surfacing as a deployment problem.
// Each rule has a negative case that proves the check can fail; a fitness function that cannot fail is decoration.
public partial class ServiceBoundaryTests
{
    // folder under backend/ -> the Postgres schema that service owns (ADR 0001)
    private static readonly Dictionary<string, string> Services = new()
    {
        ["AuditService"] = "audit",
        ["AuthService"] = "auth",
        ["DirectoryService"] = "directory",
        ["EmployeeService"] = "employee",
        ["NotificationService"] = "notification",
        ["RewardsService"] = "rewards",
        ["SearchService"] = "search",
        ["McpServer"] = string.Empty,
    };

    private static readonly string Backend = FindBackend();

    private static string FindBackend()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "backend.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("backend.slnx not found above the test output");
    }

    private static bool IsGenerated(string path) =>
        path.Replace('\\', '/').Contains("/obj/") || path.Replace('\\', '/').Contains("/bin/");

    private static bool IsTestProject(string path) =>
        Path.GetFileNameWithoutExtension(path).EndsWith("Tests", StringComparison.Ordinal)
        || Path.GetFileNameWithoutExtension(path).EndsWith("ModelEval", StringComparison.Ordinal);

    /// <summary>Production projects and the production projects each references, by full path.</summary>
    private static Dictionary<string, List<string>> ProjectGraph() =>
        Directory.GetFiles(Backend, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !IsGenerated(p) && !IsTestProject(p))
            .ToDictionary(
                p => p,
                p => XDocument.Load(p).Descendants("ProjectReference")
                    .Select(r => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(p)!, ((string)r.Attribute("Include")!).Replace('\\', '/'))))
                    .ToList());

    private static string ServiceOf(string project)
    {
        var relative = Path.GetRelativePath(Backend, project).Replace('\\', '/');
        return relative.Split('/')[0];
    }

    // The rule, as a function so a negative case can feed it a violation.
    internal static List<string> CrossServiceReferences(Dictionary<string, List<string>> graph) =>
        graph.SelectMany(g => g.Value.Select(target => (From: g.Key, To: target)))
            .Where(e => ServiceOf(e.From) != "Shared" && ServiceOf(e.To) != "Shared" && ServiceOf(e.From) != ServiceOf(e.To))
            .Select(e => $"{ServiceOf(e.From)} -> {ServiceOf(e.To)} ({Path.GetFileName(e.From)} references {Path.GetFileName(e.To)})")
            .ToList();

    [Fact]
    public void The_discovery_sees_the_services()
    {
        var graph = ProjectGraph();

        Assert.True(graph.Count >= 20);
        Assert.All(Services.Keys, name => Assert.Contains(graph.Keys, p => ServiceOf(p) == name));
    }

    [Fact]
    public void A_service_references_only_itself_and_Shared()
    {
        Assert.Empty(CrossServiceReferences(ProjectGraph()));
    }

    [Fact]
    public void Shared_references_no_service()
    {
        var offenders = ProjectGraph()
            .Where(g => ServiceOf(g.Key) == "Shared")
            .SelectMany(g => g.Value.Where(t => ServiceOf(t) != "Shared"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_rule_would_catch_a_service_that_references_another()
    {
        var graph = new Dictionary<string, List<string>>
        {
            [Path.Combine(Backend, "RewardsService", "RewardsService.Web", "RewardsService.Web.csproj")] =
                [Path.Combine(Backend, "EmployeeService", "EmployeeService.Domain", "EmployeeService.Domain.csproj")],
        };

        var found = Assert.Single(CrossServiceReferences(graph));
        Assert.StartsWith("RewardsService -> EmployeeService", found);
    }

    [Fact]
    public void There_are_no_project_reference_cycles()
    {
        var graph = ProjectGraph();
        var state = new Dictionary<string, int>();

        foreach (var node in graph.Keys)
        {
            Assert.False(HasCycle(node, graph, state), $"A project reference cycle passes through {Path.GetFileName(node)}");
        }
    }

    [Fact]
    public void The_cycle_check_would_catch_a_cycle()
    {
        var a = "a.csproj";
        var b = "b.csproj";
        var graph = new Dictionary<string, List<string>> { [a] = [b], [b] = [a] };

        Assert.True(HasCycle(a, graph, []));
    }

    private static bool HasCycle(string node, Dictionary<string, List<string>> graph, Dictionary<string, int> state)
    {
        if (state.TryGetValue(node, out var s))
        {
            return s == 1;
        }

        state[node] = 1;
        foreach (var next in graph.GetValueOrDefault(node) ?? [])
        {
            if (HasCycle(next, graph, state))
            {
                return true;
            }
        }

        state[node] = 2;
        return false;
    }

    [GeneratedRegex(@"\b(?:FROM|JOIN|INTO|UPDATE)\s+""?(?<schema>[a-z_]+)""?\.""?[a-z_]+""?", RegexOptions.IgnoreCase)]
    private static partial Regex SchemaQualifiedTable();

    // A service owns one schema and reads no other (ADR 0001): what it needs from another it asks that service for, or
    // learns from events. Looks at the SQL written in source, which is where a cross-schema read would be typed.
    internal static List<string> ForeignSchemaReads(string service, string source, string path)
    {
        var own = Services[service];
        var owners = Services.Values.Where(s => s.Length > 0).ToHashSet();
        return SchemaQualifiedTable().Matches(source)
            .Select(m => m.Groups["schema"].Value.ToLowerInvariant())
            .Where(schema => owners.Contains(schema) && schema != own)
            .Select(schema => $"{path}: {service} reads the '{schema}' schema")
            .ToList();
    }

    [Fact]
    public void No_service_reads_another_services_schema_in_its_SQL()
    {
        var offenders = new List<string>();
        foreach (var (service, _) in Services)
        {
            var folder = Path.Combine(Backend, service);
            foreach (var file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Backend, file).Replace('\\', '/');
                if (IsGenerated(file) || relative.Contains("Tests/") || relative.Contains("/Migrations/") || relative.Contains("ModelEval/"))
                {
                    continue;
                }

                offenders.AddRange(ForeignSchemaReads(service, File.ReadAllText(file), relative));
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_schema_rule_would_catch_a_foreign_read_and_allow_its_own()
    {
        Assert.Single(ForeignSchemaReads("RewardsService", "SELECT * FROM employee.employees e", "x.cs"));
        Assert.Single(ForeignSchemaReads("AuditService", "select 1 from \"directory\".\"departments\"", "x.cs"));
        Assert.Empty(ForeignSchemaReads("DirectoryService", "SELECT count(*) FROM directory.positions p JOIN directory.departments d ON 1=1", "x.cs"));
        Assert.Empty(ForeignSchemaReads("SearchService", "FROM information_schema.tables", "x.cs"));
    }
}
