using System.Text.Json.Serialization;

namespace DirectoryService.Domain.Locations.ValueObjects;

public record LocationId
{
    public Guid Value { get; }
    [JsonConstructor]
    private LocationId(Guid value)
    {
        Value = value;
    }

    public static LocationId NewLocationId() => new(Guid.NewGuid());
    public static LocationId Empty() => new(Guid.Empty);
    public static LocationId FromValue(Guid value) => new(value);
}