using Microsoft.AspNetCore.Identity;

namespace AuthService.Domain;

public class Role : IdentityRole<Guid>
{
    public Role()
    {
    }

    public Role(string roleName)
        : base(roleName)
    {
    }
}
