namespace DirectoryService.Domain.Positions.ValueObjects;

public record PositionId
{
    public Guid Value { get; }

    private PositionId(Guid value)
    {
        Value = value;
    }

    public static PositionId NewPositionId() => new(Guid.NewGuid());

    public static PositionId FromValue(Guid value) => new(value);

    public static PositionId Empty() => new(Guid.Empty);
}