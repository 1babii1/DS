using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuditService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuditService.Infrastructure;

public sealed class PiiVaultOptions
{
    public const string SectionName = "Audit";

    /// <summary>The key that wraps every subject's key: 32 random bytes, base64. Not set means personal fields are stored as they arrive.</summary>
    public string? PiiMasterKeyBase64 { get; set; }
}

/// <summary>
/// Stores the personal fields of audit payloads encrypted under a key per subject, and makes them readable again, or permanently not (ADR 0046).
///
/// Writing: each field the catalog calls personal is replaced by an envelope (<c>{"$pii":"v1","s":subject,"n":nonce,"c":ciphertext}</c>)
/// made with AES-GCM under the subject's data key, bound to the subject and the field name so that an envelope cannot be moved
/// to another field or person. Reading puts the plaintext back for a caller allowed to see it. Erasing a subject empties its data key,
/// after which no copy of any of its fields in any partition, backup or replica can be read, and every reader sees a marker.
/// The data key is itself wrapped by the master key, which is configuration (a secret), not data.
/// </summary>
public sealed class PiiVault(AuditDbContext db, IOptions<PiiVaultOptions> options, ILogger<PiiVault> logger)
{
    public const string ErasedMarker = "[erased]";
    private const string EnvelopeKey = "$pii";

    private readonly Dictionary<string, byte[]?> _keys = [];
    private byte[]? _master;
    private bool _masterRead;

    /// <summary>False when no master key is configured; the vault then passes payloads through unchanged.</summary>
    public bool Enabled => Master is not null;

    private byte[]? Master
    {
        get
        {
            if (_masterRead)
            {
                return _master;
            }

            _masterRead = true;
            var text = options.Value.PiiMasterKeyBase64;
            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("Audit:PiiMasterKeyBase64 is not set: personal fields in audit payloads are stored in the clear");
                return null;
            }

            var bytes = Convert.FromBase64String(text);
            _master = bytes.Length == 32
                ? bytes
                : throw new InvalidOperationException("Audit:PiiMasterKeyBase64 must be 32 bytes (44 characters of base64).");
            return _master;
        }
    }

    /// <summary>The payload with its personal fields encrypted under the subject's key (or redacted, when the subject was erased).</summary>
    public string Protect(string subject, string eventType, string payloadJson)
    {
        var fields = PiiCatalog.For(eventType);
        if (fields.Length == 0 || !Enabled)
        {
            return payloadJson;
        }

        if (JsonNode.Parse(payloadJson) is not JsonObject payload)
        {
            return payloadJson;
        }

        var key = KeyFor(subject, create: true);
        var changed = false;
        foreach (var property in payload.ToList())
        {
            if (!fields.Contains(property.Key, StringComparer.OrdinalIgnoreCase) || property.Value is null || IsEnvelope(property.Value))
            {
                continue;
            }

            payload[property.Key] = key is null ? ErasedMarker : Seal(subject, property.Key, property.Value.ToJsonString(), key);
            changed = true;
        }

        return changed ? payload.ToJsonString() : payloadJson;
    }

    /// <summary>The payload with its envelopes opened: the plaintext, or <see cref="ErasedMarker"/> where the subject has been erased.</summary>
    public string Reveal(string payloadJson)
    {
        // Cheap test first: most payloads of most events hold no envelope and need no parsing.
        if (!payloadJson.Contains(EnvelopeKey, StringComparison.Ordinal) || !Enabled)
        {
            return payloadJson;
        }

        if (JsonNode.Parse(payloadJson) is not JsonObject payload)
        {
            return payloadJson;
        }

        foreach (var property in payload.ToList())
        {
            if (property.Value is not JsonObject envelope || !IsEnvelope(envelope))
            {
                continue;
            }

            var subject = envelope["s"]!.GetValue<string>();
            var key = KeyFor(subject, create: false);
            payload[property.Key] = key is null ? ErasedMarker : JsonNode.Parse(Open(subject, property.Key, envelope, key));
        }

        return payload.ToJsonString();
    }

    /// <summary>Destroys the keys of these subjects. Idempotent. Returns how many subjects had a key that was destroyed or were newly marked.</summary>
    public async Task<int> EraseAsync(IEnumerable<string> subjects, CancellationToken cancellationToken)
    {
        var erased = 0;
        foreach (var subject in subjects.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal))
        {
            // One statement: either the row exists (its key is emptied) or a tombstone is made, so that later events stay redacted.
            erased += await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO audit.subject_keys ("SubjectId", "WrappedKey", "CreatedAt", "ErasedAt")
                VALUES ({subject}, ''::bytea, now(), now())
                ON CONFLICT ("SubjectId") DO UPDATE
                   SET "WrappedKey" = ''::bytea, "ErasedAt" = COALESCE(audit.subject_keys."ErasedAt", now())
                 WHERE audit.subject_keys."ErasedAt" IS NULL
                """, cancellationToken);
            _keys.Remove(subject);
        }

        return erased;
    }

    private static bool IsEnvelope(JsonNode node) => node is JsonObject o && o.ContainsKey(EnvelopeKey);

    // The subject's data key, or null when the subject has been erased. Created on first use when asked to.
    private byte[]? KeyFor(string subject, bool create)
    {
        if (_keys.TryGetValue(subject, out var cached))
        {
            return cached;
        }

        var row = db.Set<SubjectKey>().AsNoTracking().SingleOrDefault(k => k.SubjectId == subject);
        if (row is null && create)
        {
            var fresh = RandomNumberGenerator.GetBytes(32);
            var wrapped = Wrap(subject, fresh);
            db.Database.ExecuteSqlInterpolated($"""
                INSERT INTO audit.subject_keys ("SubjectId", "WrappedKey", "CreatedAt")
                VALUES ({subject}, {wrapped}, now())
                ON CONFLICT ("SubjectId") DO NOTHING
                """);

            // Another writer may have made the key first; theirs is the one that counts.
            row = db.Set<SubjectKey>().AsNoTracking().Single(k => k.SubjectId == subject);
        }

        var key = row is null || row.IsErased ? null : Unwrap(subject, row.WrappedKey);
        _keys[subject] = key;
        return key;
    }

    private byte[] Wrap(string subject, byte[] dataKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[dataKey.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Master!, 16);
        aes.Encrypt(nonce, dataKey, cipher, tag, System.Text.Encoding.UTF8.GetBytes($"key|{subject}"));
        return [.. nonce, .. tag, .. cipher];
    }

    private byte[] Unwrap(string subject, byte[] wrapped)
    {
        var plain = new byte[wrapped.Length - 28];
        using var aes = new AesGcm(Master!, 16);
        aes.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(28), wrapped.AsSpan(12, 16), plain, System.Text.Encoding.UTF8.GetBytes($"key|{subject}"));
        return plain;
    }

    private static JsonObject Seal(string subject, string field, string valueJson, byte[] key)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(valueJson);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, System.Text.Encoding.UTF8.GetBytes($"{subject}|{field.ToLowerInvariant()}"));
        return new JsonObject
        {
            [EnvelopeKey] = "v1",
            ["s"] = subject,
            ["n"] = Convert.ToBase64String(nonce),
            ["c"] = Convert.ToBase64String([.. tag, .. cipher]),
        };
    }

    private static string Open(string subject, string field, JsonObject envelope, byte[] key)
    {
        var nonce = Convert.FromBase64String(envelope["n"]!.GetValue<string>());
        var sealedBytes = Convert.FromBase64String(envelope["c"]!.GetValue<string>());
        var plain = new byte[sealedBytes.Length - 16];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, sealedBytes.AsSpan(16), sealedBytes.AsSpan(0, 16), plain, System.Text.Encoding.UTF8.GetBytes($"{subject}|{field.ToLowerInvariant()}"));
        return System.Text.Encoding.UTF8.GetString(plain);
    }
}
