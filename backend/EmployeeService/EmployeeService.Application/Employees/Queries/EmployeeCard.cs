using EmployeeService.Application.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmployeeService.Application.Employees.Queries;

public sealed class EmployeeCardOptions
{
    public const string SectionName = "EmployeeCard";

    /// <summary>How long a read that asked to see a given wallet version waits for the copy to reach it.</summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(25);
}

/// <summary>
/// One employee with their wallet, assembled from this service's own row and the copy of the wallet it keeps from RewardsService's
/// events (ADR 0034). <see cref="Balance"/> is null until the first wallet event has arrived; <see cref="BalanceAsOfVersion"/> and
/// <see cref="BalanceChangedAt"/> say how current it is, and <see cref="Consistent"/> is false when the caller asked to see a
/// wallet version the copy had not reached when the wait ran out.
/// </summary>
public record EmployeeCardDto(
    EmployeeDto Employee,
    decimal? Balance,
    int? BalanceAsOfVersion,
    DateTime? BalanceChangedAt,
    bool Consistent);

public class GetEmployeeCardHandler(IReadDbContext readDbContext, IOptions<EmployeeCardOptions> options, TimeProvider clock)
{
    public async Task<EmployeeCardDto?> Handle(Guid employeeId, int? atLeastWalletVersion, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var deadline = clock.GetUtcNow() + settings.MaxWait;

        while (true)
        {
            var card = await Read(employeeId, cancellationToken);
            if (card is null)
            {
                return null;
            }

            var reached = atLeastWalletVersion is null || (card.BalanceAsOfVersion ?? 0) >= atLeastWalletVersion;
            if (reached)
            {
                return card;
            }

            if (clock.GetUtcNow() >= deadline)
            {
                // Better an honest, slightly old card than a hung request: the caller is told it is behind what it asked for.
                return card with { Consistent = false };
            }

            await Task.Delay(settings.PollInterval, cancellationToken);
        }
    }

    private async Task<EmployeeCardDto?> Read(Guid employeeId, CancellationToken cancellationToken)
    {
        var row = await (
            from e in readDbContext.EmployeesRead
            where e.Id == employeeId
            join w in readDbContext.EmployeeWalletsRead on e.Id equals w.EmployeeId into wallets
            from w in wallets.DefaultIfEmpty()
            select new
            {
                Employee = new EmployeeDto(
                    e.Id, e.FullName, e.Email, e.DepartmentId, e.DepartmentName, e.PositionId, e.PositionName,
                    e.Status.ToString(), e.ProvisioningFailureReason, e.HiredAt),
                Balance = w == null ? (decimal?)null : w.Balance,
                Version = w == null ? (int?)null : w.WalletVersion,
                ChangedAt = w == null ? (DateTime?)null : w.BalanceChangedAt,
            }).SingleOrDefaultAsync(cancellationToken);

        return row is null ? null : new EmployeeCardDto(row.Employee, row.Balance, row.Version, row.ChangedAt, Consistent: true);
    }
}
