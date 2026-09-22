using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AuthService.Web.Configuration;

/// <summary>Distinct type so password-reset tokens can be configured with their own lifespan.</summary>
public sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
}

/// <summary>
/// Thin subclass of the standard DataProtectorTokenProvider, existing only to resolve
/// IOptions&lt;PasswordResetTokenProviderOptions&gt; instead of the shared
/// IOptions&lt;DataProtectionTokenProviderOptions&gt; every other Identity token provider
/// resolves - see the comment where this is registered in AuthenticationConfiguration for why
/// a distinct type, not just a distinct provider *name*, was required to make the lifespan
/// actually take effect.
/// </summary>
public sealed class PasswordResetTokenProvider<TUser>(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<PasswordResetTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<TUser>> logger)
    : DataProtectorTokenProvider<TUser>(
        dataProtectionProvider,
        Microsoft.Extensions.Options.Options.Create<DataProtectionTokenProviderOptions>(options.Value),
        logger)
    where TUser : class
{
}
