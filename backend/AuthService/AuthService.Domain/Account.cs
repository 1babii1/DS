using Microsoft.AspNetCore.Identity;

namespace AuthService.Domain;

public class Account : IdentityUser<Guid>
{
    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
