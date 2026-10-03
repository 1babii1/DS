using Microsoft.EntityFrameworkCore;
using NotificationService.Domain;

namespace NotificationService.Infrastructure.Postgres;

public sealed record ErasureResult(int Notifications, int Lookups);

/// <summary>
/// Removes what this service holds about a person (ADR 0047): the notifications addressed to their account, the link between their employee
/// record and their account, and leaves a mark so that nothing for them is created again. A subject may be given as an account id or as an
/// employee id; the link table says which accounts an employee id stands for.
/// </summary>
public sealed class SubjectErasure(NotificationDbContext db)
{
    public async Task<bool> IsErasedAsync(string subjectId, CancellationToken cancellationToken) =>
        await db.ErasedSubjects.AsNoTracking().AnyAsync(e => e.SubjectId == subjectId, cancellationToken);

    public async Task<ErasureResult> EraseAsync(IEnumerable<string> subjects, CancellationToken cancellationToken)
    {
        var notifications = 0;
        var lookups = 0;

        foreach (var subject in subjects.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal))
        {
            var accounts = new HashSet<Guid>();
            var marks = new HashSet<string> { subject };
            if (Guid.TryParse(subject, out var id))
            {
                accounts.Add(id);
                foreach (var link in await db.AccountLookups.AsNoTracking()
                             .Where(l => l.EmployeeId == id || l.AccountId == id).ToListAsync(cancellationToken))
                {
                    accounts.Add(link.AccountId);
                    marks.Add(link.AccountId.ToString());
                    marks.Add(link.EmployeeId.ToString());
                }
            }

            notifications += await db.Notifications.Where(n => accounts.Contains(n.RecipientAccountId)).ExecuteDeleteAsync(cancellationToken);
            lookups += await db.AccountLookups.Where(l => accounts.Contains(l.AccountId) || marks.Contains(l.EmployeeId.ToString()))
                .ExecuteDeleteAsync(cancellationToken);

            foreach (var mark in marks)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO notification.erased_subjects ("SubjectId", "ErasedAt") VALUES ({mark}, now())
                    ON CONFLICT ("SubjectId") DO NOTHING
                    """, cancellationToken);
            }
        }

        return new ErasureResult(notifications, lookups);
    }
}
