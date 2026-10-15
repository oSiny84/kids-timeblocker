using Microsoft.Extensions.Logging;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Service.Blocking.BrowserPolicy;

public interface IBrowserPolicyStateStore
{
    /// <summary>저장된 원본 백업을 읽는다. 없으면 null.</summary>
    BrowserPolicySnapshot? Load();

    /// <summary>백업을 저장한다. 레지스트리를 바꾸기 <b>전에</b> 호출해야 한다.</summary>
    void Save(BrowserPolicySnapshot snapshot);

    /// <summary>정상 복구 후 백업 파일을 지운다.</summary>
    void Clear();

    /// <summary>백업 파일이 존재하는지. (= 정책을 우리가 바꿔놓았을 수 있는 상태)</summary>
    bool Exists { get; }
}

/// <summary>
/// 브라우저 정책 원본을 JSON 파일로 보관한다.
///
/// 어댑터 DNS 백업(AdapterDnsStateStore)과 완전히 같은 역할이다.
/// 이 파일이 있으면 "레지스트리에 우리 정책이 들어가 있을 수 있다" 는 뜻이고,
/// cleanup / uninstall 은 이 파일을 보고 원래 값으로 되돌린다.
///
/// PC 재부팅 후에도 남아야 하므로 %ProgramData% 아래에 둔다.
/// </summary>
public sealed class BrowserPolicyStateStore : IBrowserPolicyStateStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    public BrowserPolicyStateStore(ILogger logger, string? path = null)
    {
        _logger = logger;
        _path = path ?? Path.Combine(AppPaths.RootDirectory, "state", "browser-policy.json");
    }

    public bool Exists
    {
        get
        {
            lock (_lock) return File.Exists(_path);
        }
    }

    public BrowserPolicySnapshot? Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path)) return null;

                return JsonUtil.Deserialize<BrowserPolicySnapshot>(File.ReadAllText(_path));
            }
            catch (Exception ex)
            {
                // 여기서 실패하면 원래 정책 값을 되돌릴 근거를 잃는다. 반드시 기록해 둔다.
                _logger.LogError(ex,
                    "브라우저 정책 원본 백업을 읽지 못했습니다. 수동 복구가 필요할 수 있습니다: {Path}", _path);
                return null;
            }
        }
    }

    public void Save(BrowserPolicySnapshot snapshot)
    {
        lock (_lock)
        {
            // 저장에 실패하면 레지스트리를 바꾸면 안 되므로 예외를 그대로 올린다.
            JsonUtil.WriteFileAtomic(_path, JsonUtil.Serialize(snapshot));
            _logger.LogInformation(
                "브라우저 정책 원본을 저장했습니다 (브라우저 {Count}개): {Path}", snapshot.Browsers.Count, _path);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path)) return;

                File.Delete(_path);
                _logger.LogDebug("브라우저 정책 백업 파일을 제거했습니다.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "브라우저 정책 백업 파일을 지우지 못했습니다: {Path}", _path);
            }
        }
    }
}
