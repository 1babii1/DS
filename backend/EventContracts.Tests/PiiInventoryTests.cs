using System.Text.Json;
using System.Text.RegularExpressions;

namespace EventContracts.Tests;

// Personal data travels in events, and events are kept (the audit log keeps their payloads for good). This keeps the list of where
// it travels honest (ADR 0043): any field of a producer event that looks like personal data must have been decided about in
// docs/security/pii-inventory.json, as personal or as not, and a decision about a field that no longer exists is removed. It does not
// find personal data under a name the pattern does not know; it makes sure the ones it does know are never added by accident.
public class PiiInventoryTests
{
    // Names that suggest a person, or somewhere a person can be found.
    private static readonly Regex Looks = new(
        "email|phone|birth|passport|ssn|address|street|postal|(name|city|country|zip)$|^ip",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string BackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "backend.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("backend.slnx not found above the test output");
    }

    private static HashSet<string> Fields()
    {
        var found = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(BackendRoot(), "*.avsc", SearchOption.AllDirectories)
                     .Where(f => f.Replace('\\', '/').Contains("/IntegrationEvents/Schemas/", StringComparison.Ordinal)
                         && !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var record = document.RootElement.GetProperty("name").GetString()!;
            foreach (var field in document.RootElement.GetProperty("fields").EnumerateArray())
            {
                var name = field.GetProperty("name").GetString()!;
                if (Looks.IsMatch(name))
                {
                    found.Add($"{record}.{name}");
                }
            }
        }

        return found;
    }

    private static (HashSet<string> Pii, HashSet<string> NotPii) Declared()
    {
        var path = Path.Combine(BackendRoot(), "..", "docs", "security", "pii-inventory.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        static HashSet<string> Read(JsonElement list) =>
            list.EnumerateArray().Select(e => $"{e.GetProperty("event").GetString()}.{e.GetProperty("field").GetString()}").ToHashSet();

        return (Read(document.RootElement.GetProperty("pii")), Read(document.RootElement.GetProperty("notPii")));
    }

    [Fact]
    public void Every_field_that_looks_like_personal_data_has_been_decided_about()
    {
        var (pii, notPii) = Declared();

        var undecided = Fields().Except(pii).Except(notPii).Order().ToList();

        Assert.True(
            undecided.Count == 0,
            "Fields that look like personal data and are in no list of docs/security/pii-inventory.json (add each as pii, with a category "
            + $"and a purpose, or as notPii, with the reason): {string.Join(", ", undecided)}");
    }

    [Fact]
    public void Nothing_in_the_inventory_describes_a_field_that_no_longer_exists()
    {
        var (pii, notPii) = Declared();
        var existing = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(BackendRoot(), "*.avsc", SearchOption.AllDirectories)
                     .Where(f => f.Replace('\\', '/').Contains("/IntegrationEvents/Schemas/", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var record = document.RootElement.GetProperty("name").GetString()!;
            foreach (var field in document.RootElement.GetProperty("fields").EnumerateArray())
            {
                existing.Add($"{record}.{field.GetProperty("name").GetString()}");
            }
        }

        var stale = pii.Union(notPii).Except(existing).Order().ToList();

        Assert.True(stale.Count == 0, $"Inventory entries for fields that are not in any event schema: {string.Join(", ", stale)}");
    }

    [Fact]
    public void A_field_is_never_both_personal_and_not()
    {
        var (pii, notPii) = Declared();

        Assert.Empty(pii.Intersect(notPii));
    }
}
