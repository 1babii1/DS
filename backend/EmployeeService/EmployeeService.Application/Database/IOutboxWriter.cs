namespace EmployeeService.Application.Database;

public interface IOutboxWriter
{
    void Enqueue(string type, string aggregateId, object payload);
}
