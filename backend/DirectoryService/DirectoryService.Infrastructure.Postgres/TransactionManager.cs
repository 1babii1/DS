using CSharpFunctionalExtensions;
using DirectoryService.Application.Database;
using Microsoft.Extensions.Logging;
using Shared;

namespace DirectoryService.Infrastructure.Postgres
{
    public class TransactionManager : ITransactionManager
    {
        private readonly DirectoryServiceDbContext _dbContext;
        private readonly ILogger<TransactionManager> _logger;
        private readonly ILoggerFactory _loggerFactory;

        public TransactionManager(DirectoryServiceDbContext dbContext, ILogger<TransactionManager> logger,
            ILoggerFactory loggerFactory)
        {
            _dbContext = dbContext;
            _logger = logger;
            _loggerFactory = loggerFactory;
        }

        public async Task<Result<ITransactionScope, Error>> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            try
            {
                var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

                var transactionScopeLogger = _loggerFactory.CreateLogger<TransactionScope>();

                // IDbContextTransaction itself has CommitAsync/RollbackAsync - unwrapping to
                // the raw ADO.NET IDbTransaction via GetDbTransaction() (the previous shape of
                // this code) threw that away and left only the blocking sync members.
                var transactionScope = new TransactionScope(transaction, transactionScopeLogger);

                return transactionScope;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to begin transaction");
                return Error.Failure("database", "Failed to begin transaction");
            }
        }

        public async Task<UnitResult<Error>> SaveChangesAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                return UnitResult.Success<Error>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving changes");
                return UnitResult.Failure<Error>(GeneralErrors.DatabaseError());
            }
        }
    }
}