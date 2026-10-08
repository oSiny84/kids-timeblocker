using System.Text.Json.Serialization;

namespace TimeBlocker.Shared.Models;

/// <summary>레지스트리 정책 값의 종류. Microsoft.Win32 에 의존하지 않도록 자체 enum 을 둔다.</summary>
public enum PolicyValueKind
{
    String = 0,
    Dword = 1
}

/// <summary>정책 값 하나. 문자열 또는 DWORD 중 하나만 의미가 있다.</summary>
public sealed record PolicyValue(PolicyValueKind Kind, string? StringValue, int? DwordValue)
{
    public static PolicyValue Text(string value) => new(PolicyValueKind.String, value, null);

    public static PolicyValue Dword(int value) => new(PolicyValueKind.Dword, null, value);

    /// <summary>로그/상태 표시용 짧은 표기.</summary>
    public override string ToString() =>
        Kind == PolicyValueKind.Dword ? (DwordValue?.ToString() ?? "(null)") : (StringValue ?? "(null)");
}

/// <summary>
/// 우리가 건드린 단일 값의 원래 상태.
/// Kind 가 null 이면 "원래 이 값이 없었다" 는 뜻이고, 복구할 때는 값을 지운다.
/// </summary>
public sealed class PolicyValueBackup
{
    public string Name { get; set; } = string.Empty;

    public PolicyValueKind? Kind { get; set; }

    public string? StringValue { get; set; }

    public int? DwordValue { get; set; }

    /// <summary>원래 값이 존재했는지. 계산값이므로 파일에 쓰지 않는다.</summary>
    [JsonIgnore]
    public bool Existed => Kind is not null;

    public PolicyValue? ToValue() =>
        Kind is null ? null : new PolicyValue(Kind.Value, StringValue, DwordValue);

    public static PolicyValueBackup Absent(string name) => new() { Name = name };

    public static PolicyValueBackup From(string name, PolicyValue value) => new()
    {
        Name = name,
        Kind = value.Kind,
        StringValue = value.StringValue,
        DwordValue = value.DwordValue
    };
}

/// <summary>
/// URLBlocklist 처럼 번호 값(1, 2, 3...)으로 이루어진 목록 하위 키의 원래 상태.
/// Existed 가 false 면 원래 키가 없었다는 뜻이고, 복구할 때는 키를 지운다.
/// </summary>
public sealed class PolicyListBackup
{
    /// <summary>하위 키 이름. 예: URLBlocklist</summary>
    public string Name { get; set; } = string.Empty;

    public bool Existed { get; set; }

    public List<string> Values { get; set; } = new();

    public static PolicyListBackup Absent(string name) => new() { Name = name, Existed = false };

    public static PolicyListBackup From(string name, IEnumerable<string> values) =>
        new() { Name = name, Existed = true, Values = values.ToList() };
}

/// <summary>브라우저 한 개의 정책 원본 백업.</summary>
public sealed class BrowserPolicyBackup
{
    /// <summary>chrome / edge / whale 같은 식별자.</summary>
    public string BrowserId { get; set; } = string.Empty;

    /// <summary>HKLM 아래의 정책 키 경로. 예: SOFTWARE\Policies\Google\Chrome</summary>
    public string RegistryKey { get; set; } = string.Empty;

    public List<PolicyValueBackup> Values { get; set; } = new();

    public List<PolicyListBackup> Lists { get; set; } = new();

    public PolicyValueBackup? FindValue(string name) =>
        Values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    public PolicyListBackup? FindList(string name) =>
        Lists.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 브라우저 정책을 건드리기 전의 원본 상태 전체.
///
/// 이 파일이 존재한다는 것은 "레지스트리 정책을 우리가 바꿔놓았을 수 있다" 는 신호다.
/// 어댑터 DNS 백업(adapter-dns.json)과 같은 역할을 한다.
/// </summary>
public sealed class BrowserPolicySnapshot
{
    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<BrowserPolicyBackup> Browsers { get; set; } = new();

    public BrowserPolicyBackup? Find(string browserId) =>
        Browsers.FirstOrDefault(b => string.Equals(b.BrowserId, browserId, StringComparison.OrdinalIgnoreCase));
}
