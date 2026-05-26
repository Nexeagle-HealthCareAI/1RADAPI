using _1Rad.Application.Interfaces;
using BCrypt.Net;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

public class PasswordHasher : IPasswordHasher
{
    private readonly ILogger<PasswordHasher>? _logger;

    public PasswordHasher(ILogger<PasswordHasher>? logger = null)
    {
        _logger = logger;
    }

    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);

    /// <summary>
    /// Verifies a password against the stored hash. Returns false for any
    /// malformed / corrupt / non-BCrypt hash — never throws.
    ///
    /// Why this matters: when a user record holds a value that isn't a valid
    /// BCrypt hash (legacy migration, plaintext from a manual SQL fix,
    /// truncated value), BCrypt.Verify throws ArgumentException / SaltParseException.
    /// That used to surface to the client as a generic 400 ARGUMENT_INVALID,
    /// which is confusing — the credentials are wrong, but it looks like
    /// the request itself was malformed.
    /// </summary>
    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        if (string.IsNullOrEmpty(password)) return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (SaltParseException ex)
        {
            _logger?.LogWarning(ex, "PasswordHasher.Verify: stored hash is not a valid BCrypt hash. Length={Len}, prefix={Prefix}", hash.Length, hash.Length >= 4 ? hash[..4] : hash);
            return false;
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "PasswordHasher.Verify: BCrypt rejected the stored hash. Length={Len}, prefix={Prefix}", hash.Length, hash.Length >= 4 ? hash[..4] : hash);
            return false;
        }
    }
}
