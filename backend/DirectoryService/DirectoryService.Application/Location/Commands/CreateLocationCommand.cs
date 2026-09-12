using DirectoryService.Contracts.Request.Location;

namespace DirectoryService.Application.Location.Commands;

public record CreateLocationCommand(CreateLocationRequest locationRequest);