namespace Shared.Exceptions;

public class NotFoundException : Exception
{
    public Error Error { get; } = null!;

    public NotFoundException(Error error)
        : base(error.GetMessage())
    {
        Error = error;
    }

    public NotFoundException()
    {
    }

    public NotFoundException(string message)
        : base(message)
    {
    }

    public NotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}