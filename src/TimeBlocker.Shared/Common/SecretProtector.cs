using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace TimeBlocker.Shared.Common;

/// <summary>
/// Windows DPAPI 로 민감정보(Telegram Bot Token)를 암호화한다.
///
/// 범위를 LocalMachine 으로 쓰는 이유:
///   서비스는 LocalSystem 계정, GUI 는 로그인 사용자 계정으로 돌기 때문에
///   CurrentUser 범위로 암호화하면 서비스가 복호화하지 못한다.
/// LocalMachine 범위이므로 파일 자체는 ACL(관리자만 쓰기)로 보호한다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretProtector
{
    // 같은 머신 안에서도 다른 앱이 우연히 복호화하지 못하도록 하는 추가 엔트로피.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TimeBlocker.v1.secret");

    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return null;

        var bytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>복호화 실패 시 예외 대신 null 을 반환한다. (설정파일이 다른 PC 에서 복사된 경우 등)</summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;

        try
        {
            var encrypted = Convert.FromBase64String(protectedBase64);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>로그에 토큰이 찍히지 않도록 마스킹한다. 예: 12345678:AAE... -> 1234****...GjQ</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "(none)";
        if (secret.Length <= 8) return "****";
        return $"{secret[..4]}****{secret[^3..]}";
    }
}
