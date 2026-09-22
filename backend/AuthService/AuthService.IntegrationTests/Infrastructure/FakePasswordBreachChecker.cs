using AuthService.Web.Configuration;

namespace AuthService.IntegrationTests.Infrastructure;

// Swapped in for HaveIBeenPwnedPasswordChecker - integration tests must not depend on a real
// network call to a third-party API, same reasoning as FakeEmailSender standing in for a
// real SMTP server. Everything is "not breached" by default; a test opts a specific password
// in via MarkAsBreached to prove BreachedPasswordValidator actually blocks it.
public class FakePasswordBreachChecker : IPasswordBreachChecker
{
    private readonly HashSet<string> _breachedPasswords = [];

    public void MarkAsBreached(string password) => _breachedPasswords.Add(password);

    public Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken) =>
        Task.FromResult(_breachedPasswords.Contains(password));
}
