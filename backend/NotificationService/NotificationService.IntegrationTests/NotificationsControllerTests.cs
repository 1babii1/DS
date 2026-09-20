using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Domain;
using NotificationService.Infrastructure.Postgres;
using NotificationService.Web.Controllers;
using Shared;

namespace NotificationService.IntegrationTests;

// Every endpoint here scopes to the caller's own "sub" claim. That scoping is the whole
// security property of this controller - a notification feed that leaks across accounts
// exposes one employee's hire/bonus/provisioning history to another - so each endpoint is
// tested with a second account's data present, not just the caller's own.
public class NotificationsControllerTests : IClassFixture<NotificationTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public NotificationsControllerTests(NotificationTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task List_returns_only_the_callers_own_notifications()
    {
        var mine = Guid.NewGuid();
        var someone_else = Guid.NewGuid();
        await Seed(Note(mine, "Mine"), Note(someone_else, "Theirs"));

        var page = await Act(mine, c => c.List(unreadOnly: null, page: null, pageSize: null, CancellationToken.None));

        Assert.Equal("Mine", Assert.Single(page.Value!.Items).Title);
    }

    [Fact]
    public async Task Unread_count_ignores_other_accounts()
    {
        var mine = Guid.NewGuid();
        var someone_else = Guid.NewGuid();
        await Seed(Note(mine, "Mine"), Note(someone_else, "Theirs"), Note(someone_else, "Theirs too"));

        var result = await Act(mine, c => c.UnreadCount(CancellationToken.None));

        Assert.Equal(1, result.Value!.Count);
    }

    [Fact]
    public async Task Unread_only_filter_excludes_what_was_already_read()
    {
        var mine = Guid.NewGuid();
        var read = Note(mine, "Already read");
        read.MarkRead();
        await Seed(read, Note(mine, "Still unread"));

        var page = await Act(mine, c => c.List(unreadOnly: true, page: null, pageSize: null, CancellationToken.None));

        Assert.Equal("Still unread", Assert.Single(page.Value!.Items).Title);
    }

    [Fact]
    public async Task Marking_someone_elses_notification_read_is_a_404_not_a_silent_write()
    {
        var mine = Guid.NewGuid();
        var someone_else = Guid.NewGuid();
        var theirs = Note(someone_else, "Theirs");
        await Seed(theirs);

        var response = await Act(mine, c => c.MarkRead(theirs.Id, CancellationToken.None));

        Assert.IsType<NotFoundResult>(response);
        Assert.False(await ReadInDb(db => db.Notifications.SingleAsync(n => n.Id == theirs.Id)) is { IsRead: true });
    }

    [Fact]
    public async Task Marking_own_notification_read_persists()
    {
        var mine = Guid.NewGuid();
        var note = Note(mine, "Mine");
        await Seed(note);

        var response = await Act(mine, c => c.MarkRead(note.Id, CancellationToken.None));

        Assert.IsType<NoContentResult>(response);
        Assert.True((await ReadInDb(db => db.Notifications.SingleAsync(n => n.Id == note.Id))).IsRead);
    }

    [Fact]
    public async Task Marking_the_same_notification_read_twice_is_a_no_op()
    {
        var mine = Guid.NewGuid();
        var note = Note(mine, "Mine");
        await Seed(note);

        await Act(mine, c => c.MarkRead(note.Id, CancellationToken.None));
        var first = (await ReadInDb(db => db.Notifications.SingleAsync(n => n.Id == note.Id))).ReadAt;
        await Act(mine, c => c.MarkRead(note.Id, CancellationToken.None));

        Assert.Equal(first, (await ReadInDb(db => db.Notifications.SingleAsync(n => n.Id == note.Id))).ReadAt);
    }

    [Fact]
    public async Task Read_all_does_not_touch_another_accounts_feed()
    {
        var mine = Guid.NewGuid();
        var someone_else = Guid.NewGuid();
        await Seed(Note(mine, "Mine"), Note(someone_else, "Theirs"));

        await Act(mine, c => c.MarkAllRead(CancellationToken.None));

        var theirs = await ReadInDb(db => db.Notifications.SingleAsync(n => n.RecipientAccountId == someone_else));
        Assert.False(theirs.IsRead);
        Assert.True((await ReadInDb(db => db.Notifications.SingleAsync(n => n.RecipientAccountId == mine))).IsRead);
    }

    [Fact]
    public async Task An_account_with_no_notifications_gets_an_empty_page_not_an_error()
    {
        var page = await Act(
            Guid.NewGuid(), c => c.List(unreadOnly: null, page: null, pageSize: null, CancellationToken.None));

        Assert.Empty(page.Value!.Items);
        Assert.Equal(0, page.Value.Total);
    }

    [Theory]
    [InlineData(null, PagedResponse<NotificationDto>.DefaultSize)]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, PagedResponse<NotificationDto>.MaxSize)]
    public async Task Page_size_is_clamped_rather_than_trusted(int? requested, int expected)
    {
        var mine = Guid.NewGuid();
        await Seed(Note(mine, "Mine"));

        var page = await Act(mine, c => c.List(unreadOnly: null, page: null, pageSize: requested, CancellationToken.None));

        Assert.Equal(expected, page.Value!.Size);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    private static Notification Note(Guid recipient, string title) =>
        Notification.Create(Guid.NewGuid(), recipient, "Test", title, "body");

    private async Task Seed(params Notification[] notifications)
    {
        await using var scope = _services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        dbContext.Notifications.AddRange(notifications);
        await dbContext.SaveChangesAsync();
    }

    private async Task<T> ReadInDb<T>(Func<NotificationDbContext, Task<T>> read)
    {
        await using var scope = _services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<NotificationDbContext>());
    }

    private async Task<T> Act<T>(Guid accountId, Func<NotificationsController, Task<T>> act)
    {
        await using var scope = _services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        var controller = new NotificationsController(dbContext)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity([new Claim("sub", accountId.ToString())], "test")),
                },
            },
        };

        return await act(controller);
    }
}
