namespace DirectoryService.Contracts.Request.Location;
public record GetLocationBySearchRequest(string Search, int? Size = 10, int? Page = 1);