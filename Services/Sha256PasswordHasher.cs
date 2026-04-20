using System.Security.Cryptography;
using System.Text;

namespace SlotAd_Globe.Services;

/// <summary>SHA-256 with random per-user salt (admin-created accounts). Legacy logins use PBKDF2 when <see cref="Data.AppUser.PasswordSalt"/> is null.</summary>
public static class Sha256PasswordHasher
{
    public static (string PasswordHashBase64, string SaltBase64) CreateHash(string password)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        var hash = Hash(password, salt);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string? saltBase64, string? storedHashBase64)
    {
        if (string.IsNullOrEmpty(saltBase64) || string.IsNullOrEmpty(storedHashBase64))
            return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(saltBase64);
            expected = Convert.FromBase64String(storedHashBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Hash(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Hash(string password, byte[] salt)
    {
        var pwBytes = Encoding.UTF8.GetBytes(password);
        var combined = new byte[salt.Length + pwBytes.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(pwBytes, 0, combined, salt.Length, pwBytes.Length);
        return SHA256.HashData(combined);
    }
}
