using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Shared.Core;

/// <summary>
/// 일시 허용 상태의 영속화. 서비스 재시작/PC 재부팅 후에도
/// 만료 시각을 그대로 복원하기 위해 필요하다.
/// </summary>
public interface IPermitStateStore
{
    List<TemporaryPermit> Load();

    void Save(IReadOnlyCollection<TemporaryPermit> permits);
}

/// <summary>JSON 파일 기반 구현. 파일이 깨져 있어도 빈 목록으로 복구한다.</summary>
public sealed class JsonPermitStateStore : IPermitStateStore
{
    private readonly string _path;
    private readonly Action<string, Exception?>? _onError;

    public JsonPermitStateStore(string? path = null, Action<string, Exception?>? onError = null)
    {
        _path = path ?? AppPaths.PermitStateFile;
        _onError = onError;
    }

    public List<TemporaryPermit> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<TemporaryPermit>();

            var json = File.ReadAllText(_path);
            var permits = JsonUtil.Deserialize<List<TemporaryPermit>>(json);
            return permits ?? new List<TemporaryPermit>();
        }
        catch (Exception ex)
        {
            // 상태 파일이 깨졌다고 서비스가 죽으면 안 된다. 허용이 사라지면 곧 차단되므로 안전한 쪽이다.
            _onError?.Invoke($"일시 허용 상태 파일을 읽지 못했습니다: {_path}", ex);
            return new List<TemporaryPermit>();
        }
    }

    public void Save(IReadOnlyCollection<TemporaryPermit> permits)
    {
        try
        {
            JsonUtil.WriteFileAtomic(_path, JsonUtil.Serialize(permits.ToList()));
        }
        catch (Exception ex)
        {
            _onError?.Invoke($"일시 허용 상태 파일을 저장하지 못했습니다: {_path}", ex);
        }
    }
}

/// <summary>테스트 및 재부팅 시뮬레이션용.</summary>
public sealed class InMemoryPermitStateStore : IPermitStateStore
{
    private List<TemporaryPermit> _permits = new();

    public List<TemporaryPermit> Load() => _permits.Select(p => p.Clone()).ToList();

    public void Save(IReadOnlyCollection<TemporaryPermit> permits) =>
        _permits = permits.Select(p => p.Clone()).ToList();
}
