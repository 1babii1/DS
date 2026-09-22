using AuthService.Domain;
using Quartz;

namespace AuthService.Web.Configuration;

/// <summary>
/// Checks daily whether either key purpose is due for rotation (SigningKeyStore.
/// RotationInterval past its current primary's CreatedAt) - a no-op most days. Runs both
/// purposes independently since a signing-key rotation does not imply an encryption-key
/// rotation is also due, and vice versa.
/// </summary>
public class SigningKeyRotationJob(SigningKeyStore signingKeys, ILogger<SigningKeyRotationJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        foreach (var purpose in new[] { SigningKeyPurpose.Signing, SigningKeyPurpose.Encryption })
        {
            if (await signingKeys.RotateIfDueAsync(purpose, cancellationToken))
            {
                logger.LogInformation("Rotated the {Purpose} key.", purpose);
            }
        }
    }
}
