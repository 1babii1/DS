using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Contracts;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AuthService.Web.Configuration;

/// <summary>
/// WebAuthn/passkey registration and usernameless login. Challenges are cached server-side
/// between the "options" and "complete" calls of each two-step ceremony - a short-lived
/// IMemoryCache entry, not a DB row, since AuthService runs single-instance (matching the
/// in-memory Quartz store's own reasoning) and the challenge only needs to survive one
/// round trip to the browser and back.
/// </summary>
public class PasskeyService(
    IFido2 fido2,
    AuthDbContext dbContext,
    UserManager<Account> userManager,
    IMemoryCache cache)
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    public async Task<PasskeyChallengeResponse> BeginRegistrationAsync(Account user, CancellationToken cancellationToken)
    {
        var existingCredentials = await dbContext.PasskeyCredentials
            .Where(c => c.AccountId == user.Id)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToListAsync(cancellationToken);

        var fido2User = new Fido2User
        {
            Name = user.Email!,
            DisplayName = user.Email!,
            Id = user.Id.ToByteArray(),
        };

        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fido2User,
            ExcludeCredentials = existingCredentials,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                // Required, not just preferred: a discoverable ("resident key") credential
                // is what lets the usernameless login flow work at all - the browser can
                // only offer a saved passkey with no prior email/username if the credential
                // itself carries enough to find the account.
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var token = Guid.NewGuid().ToString("N");
        cache.Set(RegistrationCacheKey(token), options, ChallengeLifetime);
        return new PasskeyChallengeResponse(token, options.ToJson());
    }

    /// <returns>False if the challenge token is missing/expired - the caller treats that as
    /// a failed registration rather than throwing.</returns>
    public async Task<bool> CompleteRegistrationAsync(
        Account user, string token, AuthenticatorAttestationRawResponse attestationResponse, string label, CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(RegistrationCacheKey(token), out CredentialCreateOptions? options) || options is null)
        {
            return false;
        }

        cache.Remove(RegistrationCacheKey(token));

        var result = await fido2.MakeNewCredentialAsync(
            new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                    !await dbContext.PasskeyCredentials.AnyAsync(c => c.CredentialId == args.CredentialId, ct),
            },
            cancellationToken);

        dbContext.PasskeyCredentials.Add(new PasskeyCredential
        {
            Id = Guid.NewGuid(),
            AccountId = user.Id,
            CredentialId = result.Id,
            PublicKey = result.PublicKey,
            SignatureCounter = result.SignCount,
            AaGuid = result.AaGuid,

            // The client's maxlength is a UX hint, not a guarantee - truncating here keeps
            // an over-length label from throwing a Postgres error against the column's
            // varchar(100) limit instead of just... being 100 characters.
            Name = string.IsNullOrWhiteSpace(label) ? "Passkey" : label.Trim()[..Math.Min(label.Trim().Length, 100)],
            CreatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<PasskeySummary>> ListAsync(Guid accountId, CancellationToken cancellationToken) =>
        await dbContext.PasskeyCredentials
            .Where(c => c.AccountId == accountId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new PasskeySummary(c.Id, c.Name, c.CreatedAt, c.LastUsedAt))
            .ToListAsync(cancellationToken);

    /// <returns>False if no matching credential belongs to this account - scoped by
    /// AccountId so one account can never delete another's passkey by guessing an id.</returns>
    public async Task<bool> DeleteAsync(Guid accountId, Guid credentialRowId, CancellationToken cancellationToken)
    {
        var credential = await dbContext.PasskeyCredentials
            .SingleOrDefaultAsync(c => c.Id == credentialRowId && c.AccountId == accountId, cancellationToken);
        if (credential is null)
        {
            return false;
        }

        dbContext.PasskeyCredentials.Remove(credential);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<PasskeyChallengeResponse> BeginAuthenticationAsync()
    {
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            // Empty on purpose: this is the usernameless flow, so there is no known
            // account yet to scope the allowed-credentials list to - the browser offers
            // whatever discoverable credential for this RP it already has saved.
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Preferred,
        });

        var token = Guid.NewGuid().ToString("N");
        cache.Set(AuthenticationCacheKey(token), options, ChallengeLifetime);
        return Task.FromResult(new PasskeyChallengeResponse(token, options.ToJson()));
    }

    /// <returns>The signed-in account on success, null on any failure (expired challenge,
    /// unknown credential, or a verification failure Fido2 itself rejects) - deliberately
    /// one outcome shape so the caller cannot leak which specific step failed.</returns>
    public async Task<Account?> CompleteAuthenticationAsync(
        string token, AuthenticatorAssertionRawResponse assertionResponse, CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(AuthenticationCacheKey(token), out AssertionOptions? options) || options is null)
        {
            return null;
        }

        cache.Remove(AuthenticationCacheKey(token));

        var credential = await dbContext.PasskeyCredentials
            .SingleOrDefaultAsync(c => c.CredentialId == assertionResponse.RawId, cancellationToken);
        if (credential is null)
        {
            return null;
        }

        VerifyAssertionResult result;
        try
        {
            result = await fido2.MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = assertionResponse,
                    OriginalOptions = options,
                    StoredPublicKey = credential.PublicKey,
                    StoredSignatureCounter = credential.SignatureCounter,
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                        Task.FromResult(new Guid(args.UserHandle) == credential.AccountId),
                },
                cancellationToken);
        }
        catch (Fido2VerificationException)
        {
            return null;
        }

        credential.SignatureCounter = result.SignCount;
        credential.LastUsedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return await userManager.FindByIdAsync(credential.AccountId.ToString());
    }

    private static string RegistrationCacheKey(string token) => $"passkey-registration:{token}";

    private static string AuthenticationCacheKey(string token) => $"passkey-authentication:{token}";
}
