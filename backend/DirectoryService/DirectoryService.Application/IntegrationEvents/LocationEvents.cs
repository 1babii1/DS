namespace DirectoryService.Application.IntegrationEvents;

public static class LocationEventTypes
{
    public const string Created = "LocationCreated";
}

public record LocationCreatedEvent(
    Guid LocationId, string Name, string Street, string City, string Country, string Timezone);
