using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TimeBlocker.Service.Logging;

/// <summary>
/// 파일 로그 기록기.
///  - 파일명: TimeBlocker-yyyy-MM-dd.log (날짜별)
///  - 크기 제한 초과 시 TimeBlocker-yyyy-MM-dd.1.log 형태로 롤링
///  - RetainDays 가 지난 파일은 삭제
///  - 백그라운드 스레드에서 기록하여 서비스 루프를 막지 않는다.
/// </summary>
public sealed class FileLogWriter : IDisposable
{
    private readonly string _directory;
    private readonly long _maxFileSizeBytes;
    private readonly int _retainDays;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 10_000);
    private readonly Thread _worker;
    private readonly object _fileLock = new();
    private DateOnly _currentDate = DateOnly.MinValue;
    private string? _currentPath;
    private bool _disposed;

    public FileLogWriter(string directory, int maxFileSizeMb, int retainDays)
    {
        _directory = directory;
        _maxFileSizeBytes = Math.Max(1, maxFileSizeMb) * 1024L * 1024L;
        _retainDays = Math.Max(1, retainDays);

        Directory.CreateDirectory(_directory);

        _worker = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "TimeBlocker.FileLog"
        };
        _worker.Start();
    }

    public void Write(string line)
    {
        if (_disposed) return;

        // 큐가 가득 차면 로그를 버린다. 로그 때문에 서비스가 멈추면 안 된다.
        _queue.TryAdd(line);
    }

    /// <summary>GUI 로그 조회용. 최근 N줄을 오늘 파일에서 읽는다.</summary>
    public List<string> ReadTail(int lineCount)
    {
        var path = GetPathForDate(DateOnly.FromDateTime(DateTime.Now));

        lock (_fileLock)
        {
            if (!File.Exists(path)) return new List<string>();

            try
            {
                // 공유 모드로 열어 기록 중에도 읽을 수 있게 한다.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var buffer = new Queue<string>(lineCount);
                while (reader.ReadLine() is { } line)
                {
                    if (buffer.Count == lineCount) buffer.Dequeue();
                    buffer.Enqueue(line);
                }
                return buffer.ToList();
            }
            catch (IOException)
            {
                return new List<string>();
            }
        }
    }

    private void ProcessQueue()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                WriteLineToFile(line);
            }
            catch
            {
                // 디스크 가득참 등으로 실패해도 서비스는 계속 돌아야 한다.
            }
        }
    }

    private void WriteLineToFile(string line)
    {
        lock (_fileLock)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (today != _currentDate || _currentPath is null)
            {
                _currentDate = today;
                _currentPath = GetPathForDate(today);
                CleanupOldFiles();
            }

            RollIfTooLarge(_currentPath);
            File.AppendAllText(_currentPath, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    private string GetPathForDate(DateOnly date) =>
        Path.Combine(_directory, $"TimeBlocker-{date:yyyy-MM-dd}.log");

    private void RollIfTooLarge(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < _maxFileSizeBytes) return;

        for (var index = 1; index < 1000; index++)
        {
            var rolled = Path.ChangeExtension(path, null) + $".{index}.log";
            if (File.Exists(rolled)) continue;

            File.Move(path, rolled);
            return;
        }
    }

    private void CleanupOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retainDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "TimeBlocker-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // 정리 실패는 무시한다.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(3));
        _queue.Dispose();
    }
}

/// <summary>ILoggerProvider 구현. Microsoft.Extensions.Logging 과 연결한다.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogWriter _writer;
    private readonly LogLevel _minimumLevel;

    public FileLoggerProvider(FileLogWriter writer, string minimumLevel)
    {
        _writer = writer;
        _minimumLevel = ParseLevel(minimumLevel);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_writer, categoryName, _minimumLevel);

    public void Dispose()
    {
        // FileLogWriter 는 DI 컨테이너가 소유한다. 여기서 닫지 않는다.
    }

    public static LogLevel ParseLevel(string? text) => (text ?? "INFO").Trim().ToUpperInvariant() switch
    {
        "TRACE" => LogLevel.Trace,
        "DEBUG" => LogLevel.Debug,
        "INFO" or "INFORMATION" => LogLevel.Information,
        "WARN" or "WARNING" => LogLevel.Warning,
        "ERROR" => LogLevel.Error,
        "NONE" => LogLevel.None,
        _ => LogLevel.Information
    };

    private sealed class FileLogger : ILogger
    {
        private readonly FileLogWriter _writer;
        private readonly string _category;
        private readonly LogLevel _minimumLevel;

        public FileLogger(FileLogWriter writer, string category, LogLevel minimumLevel)
        {
            _writer = writer;
            // "TimeBlocker.Service.Blocking.FirewallManager" -> "FirewallManager"
            var lastDot = category.LastIndexOf('.');
            _category = lastDot >= 0 ? category[(lastDot + 1)..] : category;
            _minimumLevel = minimumLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var level = logLevel switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO ",
                LogLevel.Warning => "WARN ",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "FATAL",
                _ => "INFO "
            };

            var message = formatter(state, exception);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {_category}: {message}";
            if (exception is not null) line += $" | {exception.GetType().Name}: {exception.Message}";

            _writer.Write(line);
        }
    }
}
