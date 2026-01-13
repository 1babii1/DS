using System.Collections;

namespace Shared;

public class Errors : IEnumerable<Error>
{
    private readonly List<Error> _errors;

    public Errors(IEnumerable<Error> errors)
    {
        // _errors = errors.ToList(); // Копирует все элементы из исходной коллекции.
        _errors = [..errors]; // Распространяем все элементы из этой коллекции в новую коллекцию.
    }

    public IEnumerator<Error> GetEnumerator()
    {
        return _errors.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static implicit operator Errors(Error[] errors) => new(errors);

    public static implicit operator Errors(Error error) => new([error]);
}