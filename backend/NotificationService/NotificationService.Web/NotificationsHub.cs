using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace NotificationService.Web;

// One group per recipient, named by their own "sub" claim - DomainEventsConsumer pushes
// to Clients.Group(recipientAccountId.ToString()), so every connection this account opens
// (multiple tabs, multiple devices) receives the same push. No hub methods beyond
// connect/disconnect: this hub is server -> client only, nothing the client calls on it.
[Authorize]
public class NotificationsHub : Hub
{
    public override Task OnConnectedAsync()
    {
        var accountId = Context.User!.FindFirst("sub")!.Value;
        return Groups.AddToGroupAsync(Context.ConnectionId, accountId);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var accountId = Context.User!.FindFirst("sub")!.Value;
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, accountId);
    }
}
