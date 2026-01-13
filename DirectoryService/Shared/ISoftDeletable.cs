namespace Shared;

public interface ISoftDeletable
{
    DateTime? DeletedAt { get; set; }

    void Delete();

    void Activate();
}