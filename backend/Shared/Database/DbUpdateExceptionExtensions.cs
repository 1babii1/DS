using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Shared.Database;

/// <summary>
/// Same check - "was this actually a unique-index conflict, not some other
/// failure" - was hand-typed with the raw Postgres SQLSTATE code at three
/// separate call sites before this existed, one typo away from silently
/// matching the wrong thing.
/// </summary>
public static class DbUpdateExceptionExtensions
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";

    public static bool IsUniqueViolation(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };

    public static bool IsForeignKeyViolation(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: ForeignKeyViolation };
}