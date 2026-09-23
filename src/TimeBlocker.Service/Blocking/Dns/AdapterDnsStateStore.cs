using Microsoft.Extensions.Logging;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Service.Blocking.Dns;

public interface IAdapterDnsStateStore
{
    /// <summary>저장된 상태를 읽는다. 없으면 null.</summary>
    AdapterDnsState? Load();

    /// <summary>상태를 저장한다. 어댑터를 바꾸기 <b>전에</b> 호출해야 한다.</summary>
    void Save(AdapterDnsState state);

    /// <summary>정상 원복 후 상태 파일을 지운다.</summary>
    void Clear();

    /// <summary>상태 파일이 존재하는지. (= 어댑터를 바꿔놓았을 수 있는 상태)</summary>
    bool Exists { get; }
}

/// <summary>
/// 어댑터 DNS 원본 설정을 JSON 파일로 보관한다.
///
/// 이 파일이 존재한다는 것은 "어댑터 DNS 를 우리가 바꿔놓았을 수 있다" 는 신호다.
/// 정상적으로 되돌리면 파일을 지우므로,
/// 서비스 시작 시 파일이 남아 있으면 이전 실행이 비정상 종료된 것으로 판단한다.
///
/// 이 파일은 PC 재부팅 후에도 남아야 하므로 %ProgramData% 아래에 둔다.
/// </summary>
public sealed class AdapterDnsStateStore : IAdapterDnsStateStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    public AdapterDnsStateStore(ILogger logger, string? path = null)
    {
        _logger = logger;
        _path = path ?? Path.Combine(AppPaths.RootDirectory, "state", "adapter-dns.json");
    }

    public bool Exists
    {
        get
        {
            lock (_lock) return File.Exists(_path);
        }
    }

    public AdapterDnsState? Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path)) return null;

                var json = File.ReadAllText(_path);
                return JsonUtil.Deserialize<AdapterDnsState>(json);
            }
            catch (Exception ex)
            {
                // 여기서 실패하면 원복 정보를 잃는다. 반드시 기록해 둔다.
                _logger.LogError(ex,
                    "어댑터 DNS 원본 설정 파일을 읽지 못했습니다. 수동 복구가 필요할 수 있습니다: {Path}", _path);
                return null;
            }
        }
    }

    public void Save(AdapterDnsState state)
    {
        lock (_lock)
        {
            // 저장에 실패하면 어댑터를 바꾸면 안 되므로 예외를 그대로 올린다.
            JsonUtil.WriteFileAtomic(_path, JsonUtil.Serialize(state));
            _logger.LogInformation(
                "어댑터 DNS 원본 설정을 저장했습니다 ({Count}개): {Path}", state.Adapters.Count, _path);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                    _logger.LogDebug("어댑터 DNS 원본 설정 파일을 제거했습니다.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "어댑터 DNS 상태 파일을 지우지 못했습니다: {Path}", _path);
            }
        }
    }
}
