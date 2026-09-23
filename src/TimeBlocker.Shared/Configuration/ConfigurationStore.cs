using TimeBlocker.Shared.Common;

namespace TimeBlocker.Shared.Configuration;

public interface IConfigurationStore
{
    /// <summary>현재 메모리에 올라온 설정. 항상 non-null.</summary>
    TimeBlockerConfig Current { get; }

    /// <summary>디스크에서 다시 읽는다. 파일이 없으면 기본 설정을 만들어 저장한다.</summary>
    TimeBlockerConfig Reload();

    /// <summary>설정을 저장하고 Current 를 교체한다.</summary>
    void Save(TimeBlockerConfig config);

    /// <summary>설정이 바뀌었을 때 발생.</summary>
    event EventHandler<TimeBlockerConfig>? Changed;
}

/// <summary>
/// timeblocker.config.json 읽기/쓰기.
/// 설정 파일이 손상되어도 서비스가 죽지 않도록, 실패 시 기본 설정으로 계속 동작하고
/// 원본은 .broken 으로 백업한다.
/// </summary>
public sealed class JsonConfigurationStore : IConfigurationStore
{
    private readonly string _path;
    private readonly Action<string, Exception?>? _onError;
    private readonly object _lock = new();
    private TimeBlockerConfig _current;

    public JsonConfigurationStore(string? path = null, Action<string, Exception?>? onError = null)
    {
        _path = path ?? AppPaths.ConfigFile;
        _onError = onError;
        _current = LoadFromDisk();
    }

    public TimeBlockerConfig Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    public event EventHandler<TimeBlockerConfig>? Changed;

    public TimeBlockerConfig Reload()
    {
        var loaded = LoadFromDisk();
        lock (_lock) _current = loaded;
        Changed?.Invoke(this, loaded);
        return loaded;
    }

    public void Save(TimeBlockerConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        config.Normalize();

        lock (_lock)
        {
            JsonUtil.WriteFileAtomic(_path, JsonUtil.Serialize(config));
            _current = config;
        }

        Changed?.Invoke(this, config);
    }

    private TimeBlockerConfig LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_path))
            {
                var fresh = TimeBlockerConfig.CreateDefault();
                fresh.Normalize();
                JsonUtil.WriteFileAtomic(_path, JsonUtil.Serialize(fresh));
                return fresh;
            }

            var json = File.ReadAllText(_path);
            var config = JsonUtil.Deserialize<TimeBlockerConfig>(json) ?? TimeBlockerConfig.CreateDefault();
            config.Normalize();
            return config;
        }
        catch (Exception ex)
        {
            _onError?.Invoke($"설정파일을 읽지 못했습니다. 기본 설정으로 동작합니다: {_path}", ex);
            TryBackupBrokenFile();

            var fallback = TimeBlockerConfig.CreateDefault();
            fallback.Normalize();
            return fallback;
        }
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Copy(_path, _path + ".broken", overwrite: true);
            }
        }
        catch
        {
            // 백업 실패는 무시한다. 여기서 예외가 나면 서비스가 시작조차 못 한다.
        }
    }
}
