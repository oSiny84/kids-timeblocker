using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Service.Blocking.BrowserPolicy;

/// <summary>
/// HKLM 아래 정책 키를 읽고 쓰는 최소 접점.
///
/// 레지스트리를 직접 만지는 코드를 한 곳에 몰아두는 이유는 두 가지다.
///  - 테스트에서 실제 HKLM 을 건드리지 않도록 가짜 구현으로 바꿔 끼울 수 있다.
///  - 쓰기 실패(권한 없음 등)를 한 군데서 같은 방식으로 처리할 수 있다.
///
/// 경로는 항상 HKLM 기준 상대 경로다. 예: SOFTWARE\Policies\Google\Chrome
/// </summary>
public interface IRegistryPolicyEditor
{
    bool KeyExists(string subKey);

    /// <summary>값 하나를 읽는다. 키나 값이 없으면 null.</summary>
    PolicyValue? ReadValue(string subKey, string name);

    void WriteValue(string subKey, string name, PolicyValue value);

    /// <summary>값을 지운다. 없으면 아무 일도 하지 않는다.</summary>
    void DeleteValue(string subKey, string name);

    /// <summary>
    /// 번호 값(1, 2, 3...)으로 된 목록 키를 읽는다. 키가 없으면 null.
    /// Chromium 계열 정책에서 URLBlocklist 같은 목록형 정책이 이 형태를 쓴다.
    /// </summary>
    IReadOnlyList<string>? ReadList(string subKey);

    /// <summary>목록 키를 주어진 내용으로 교체한다. 기존 번호 값은 모두 지운다.</summary>
    void WriteList(string subKey, IReadOnlyList<string> values);

    /// <summary>키를 하위까지 지운다. 없으면 아무 일도 하지 않는다.</summary>
    void DeleteKey(string subKey);
}

/// <summary>
/// 실제 HKLM 을 읽고 쓰는 구현.
///
/// 쓰기에는 관리자 권한이 필요하다. 권한이 없으면 UnauthorizedAccessException 이
/// 그대로 올라가므로, 호출하는 쪽(BrowserPolicyManager)에서 한 번에 처리한다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryPolicyEditor : IRegistryPolicyEditor
{
    private readonly ILogger<RegistryPolicyEditor> _logger;

    public RegistryPolicyEditor(ILogger<RegistryPolicyEditor> logger)
    {
        _logger = logger;
    }

    public bool KeyExists(string subKey)
    {
        using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
        return key is not null;
    }

    public PolicyValue? ReadValue(string subKey, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
        if (key is null) return null;

        var raw = key.GetValue(name);
        if (raw is null) return null;

        return key.GetValueKind(name) switch
        {
            RegistryValueKind.DWord => PolicyValue.Dword(Convert.ToInt32(raw)),
            RegistryValueKind.String or RegistryValueKind.ExpandString => PolicyValue.Text(raw.ToString() ?? string.Empty),

            // 우리가 쓰지 않는 종류(QWord, Binary, MultiString)는 문자열로 보존해 둔다.
            // 복구할 때 종류가 바뀔 수 있으므로 경고를 남긴다.
            var other => WarnUnexpectedKind(subKey, name, other, raw)
        };
    }

    private PolicyValue WarnUnexpectedKind(string subKey, string name, RegistryValueKind kind, object raw)
    {
        _logger.LogWarning(
            "예상하지 못한 레지스트리 값 종류입니다. 복구 시 문자열로 되돌아갈 수 있습니다: " +
            @"HKLM\{SubKey}\{Name} ({Kind})", subKey, name, kind);

        return PolicyValue.Text(raw.ToString() ?? string.Empty);
    }

    public void WriteValue(string subKey, string name, PolicyValue value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException($@"레지스트리 키를 만들지 못했습니다: HKLM\{subKey}");

        if (value.Kind == PolicyValueKind.Dword)
        {
            key.SetValue(name, value.DwordValue ?? 0, RegistryValueKind.DWord);
        }
        else
        {
            key.SetValue(name, value.StringValue ?? string.Empty, RegistryValueKind.String);
        }
    }

    public void DeleteValue(string subKey, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: true);
        if (key is null) return;

        // throwOnMissingValue: false - 이미 없으면 목적이 달성된 것이다.
        key.DeleteValue(name, throwOnMissingValue: false);
    }

    public IReadOnlyList<string>? ReadList(string subKey)
    {
        using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
        if (key is null) return null;

        // 값 이름이 "1", "2", "10" 처럼 번호다. 문자열 정렬이 아니라 숫자 순서로 읽어야
        // 10 이 2보다 앞에 오는 일이 없다.
        var entries = new List<(int Index, string Value)>();

        foreach (var name in key.GetValueNames())
        {
            if (!int.TryParse(name, out var index)) continue;

            var raw = key.GetValue(name)?.ToString();
            if (!string.IsNullOrWhiteSpace(raw)) entries.Add((index, raw));
        }

        return entries.OrderBy(e => e.Index).Select(e => e.Value).ToList();
    }

    public void WriteList(string subKey, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            // 목록을 비우라는 요청이다. 빈 키를 남기면 정책이 "빈 목록"으로 적용되므로 키째로 지운다.
            DeleteKey(subKey);
            return;
        }

        using var key = Registry.LocalMachine.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException($@"레지스트리 키를 만들지 못했습니다: HKLM\{subKey}");

        // 기존 번호 값을 먼저 지운다. 지우지 않으면 예전 항목이 뒤에 남는다.
        foreach (var name in key.GetValueNames())
        {
            if (int.TryParse(name, out _)) key.DeleteValue(name, throwOnMissingValue: false);
        }

        for (var i = 0; i < values.Count; i++)
        {
            key.SetValue((i + 1).ToString(), values[i], RegistryValueKind.String);
        }
    }

    public void DeleteKey(string subKey)
    {
        // 상위 키는 남기고 마지막 구간만 지운다. (SOFTWARE\Policies\Google\Chrome 을 지우면 안 된다)
        var separator = subKey.LastIndexOf('\\');
        if (separator <= 0) throw new ArgumentException($"지울 수 없는 경로입니다: {subKey}", nameof(subKey));

        var parentPath = subKey[..separator];
        var leaf = subKey[(separator + 1)..];

        using var parent = Registry.LocalMachine.OpenSubKey(parentPath, writable: true);
        if (parent is null) return;

        // throwOnMissingSubKey: false - 이미 없으면 목적이 달성된 것이다.
        parent.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
    }
}
