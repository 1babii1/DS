using System.Reflection;
using Avro;

namespace Shared.Avro;

/// <summary>
/// The Avro schemas of the events a service publishes, read from the .avsc files compiled into its assembly (the files
/// in git are the source of truth, ADR 0023). Keyed by the record's name, which is the event's <c>message-type</c>.
/// </summary>
public sealed class EventSchemaCatalog
{
    private readonly Dictionary<string, (RecordSchema Schema, string Json)> _byEventType = new(StringComparer.Ordinal);

    public EventSchemaCatalog(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".avsc", StringComparison.Ordinal)))
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var schema = (RecordSchema)Schema.Parse(json);
                _byEventType[schema.Name] = (schema, json);
            }
        }
    }

    public IReadOnlyCollection<string> EventTypes => _byEventType.Keys;

    public bool TryGet(string eventType, out RecordSchema schema, out string json)
    {
        if (_byEventType.TryGetValue(eventType, out var entry))
        {
            (schema, json) = entry;
            return true;
        }

        schema = null!;
        json = null!;
        return false;
    }
}
