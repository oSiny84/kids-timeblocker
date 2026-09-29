using System.IO.Pipes;
using System.Text;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Notifications;

namespace TimeBlocker.Notifier;

/// <summary>
/// 서비스의 알림 파이프에 붙어 있는다.
///
/// 서비스가 재시작되거나 아직 안 떴을 수 있으므로 끊기면 계속 다시 붙는다.
/// 연결 실패는 정상적인 상황이므로 사용자에게 오류를 띄우지 않는다.
/// </summary>
public sealed class NotifierClient : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private Task? _loop;

    /// <summary>알림이 도착했을 때. UI 스레드가 아니므로 호출 쪽에서 Invoke 해야 한다.</summary>
    public event Action<NotificationEnvelope>? NotificationReceived;

    /// <summary>연결 상태가 바뀌었을 때.</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected { get; private set; }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>답장을 보낸다. 연결이 없으면 false.</summary>
    public async Task<bool> SendReplyAsync(string text)
    {
        var writer = _writer;
        if (writer is null || !IsConnected) return false;

        var reply = new NotifierReply
        {
            Text = text,
            UserName = Environment.UserName
        };

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(JsonUtil.Serialize(reply, indented: false)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            // 쓰기 실패는 연결이 끊어졌다는 뜻이다. 재연결 루프가 처리한다.
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 서비스가 아직 안 떴거나 재시작 중이다. 조용히 다시 시도한다.
            }
            finally
            {
                SetConnected(false);
            }

            try
            {
                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        var encoding = new UTF8Encoding(false);

        await using var pipe = new NamedPipeClientStream(
            ".", NotifierProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await pipe.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds, ct).ConfigureAwait(false);

        _pipe = pipe;
        _writer = new StreamWriter(pipe, encoding, 8192, leaveOpen: true) { AutoFlush = true };
        SetConnected(true);

        using var reader = new StreamReader(pipe, encoding, false, 8192, leaveOpen: true);

        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) continue;

            NotificationEnvelope? envelope;
            try
            {
                envelope = JsonUtil.Deserialize<NotificationEnvelope>(line);
            }
            catch
            {
                continue;   // 깨진 줄은 버린다
            }

            if (envelope is not null) NotificationReceived?.Invoke(envelope);
        }
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;

        IsConnected = connected;
        if (!connected)
        {
            _writer = null;
            _pipe = null;
        }

        ConnectionChanged?.Invoke(connected);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* 종료 중 예외는 무시 */ }
        }

        _cts.Dispose();
        _writeLock.Dispose();
    }
}
