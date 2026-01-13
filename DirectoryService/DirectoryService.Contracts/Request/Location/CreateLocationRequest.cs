namespace DirectoryService.Contracts.Request.Location;

public record CreateLocationRequest(string Name, Address Address, string Timezone);