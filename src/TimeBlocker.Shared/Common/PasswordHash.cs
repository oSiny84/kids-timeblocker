using System.Security.Cryptography;
using System.Text;

namespace TimeBlocker.Shared.Common;

/// <summary>
/// 관리자 비밀번호를 평문으로 저장하지 않기 위한 PBKDF2 해시.
/// 저장 형식: PBKDF2$iterations$saltBase64$hashBase64
/// </summary>
public static class PasswordHash
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int DefaultIterations = 120_000;
    private const string Prefix = "PBKDF2";

    public static string Create(string password, int iterations = DefaultIterations)
    {
        if (password is null) throw new ArgumentNullException(nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, iterations);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>해시가 없으면(=비밀번호 미설정) false 를 돌려준다. 호출측에서 미설정 상태를 따로 처리한다.</summary>
    public static bool Verify(string? storedHash, string? password)
    {
        if (string.IsNullOrWhiteSpace(storedHash) || password is null) return false;

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Derive(password, salt, iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int size = HashSize)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            size);
    }
}
