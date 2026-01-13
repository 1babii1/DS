using System.Data;
using CSharpFunctionalExtensions;
using DirectoryService.Application.Database;
using Microsoft.Extensions.Logging;
using Shared;

namespace DirectoryService.Infrastructure.Postgres;

public class TransactionScope : ITransactionScope
{
    private readonly IDbTransaction _transaction;
    private readonly ILogger<TransactionScope> _logger;

    public TransactionScope(IDbTransaction transaction, ILogger<TransactionScope> logger)
    {
        _transaction = transaction;
        _logger = logger;
    }

    public UnitResult<Error> Commit()
    {
        try
        {
            _transaction.Commit();
            return UnitResult.Success<Error>();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error committing transaction");
            return UnitResult.Failure<Error>(Error.Failure());
        }
    }

    public UnitResult<Error> Rollback()
    {
        try
        {
            _transaction.Rollback();
            return UnitResult.Success<Error>();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error rolling back transaction");
            return UnitResult.Failure<Error>(Error.Failure());
        }
    }

    public void Dispose()
    {
        _transaction.Dispose();
    }
}