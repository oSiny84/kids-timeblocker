using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Core;
using TimeBlocker.Shared.Notifications;

namespace TimeBlocker.Service.Ipc;

public interface INotificationHub
{
    /// <summary>트레이 앱이 하나라도 붙어 있는지.</summary>
    bool HasClients { get; }

    /// <summary>붙어 있는 트레이 앱 전부에 알림을 보낸다. 보낸 수를 돌려준다.</summary>
    Task<int> BroadcastAsync(NotificationEnvelope notification, CancellationToken cancellationToken = default);
}

/// <summary>
/// 사용자 세션에서 도는 트레이 앱(TimeBlocker.Notifier)과 연결되는 서버.
///
/// 보안상 이 파이프로 할 수 있는 일은 둘뿐이다.
///   - 알림을 받는다
///   - 답장을 보낸다 (관리자 Telegram 으로 전달)
/// 설정 변경/차단 해제는 이 통로에 존재하지 않는다. 아이 계정이 연결하기 때문이다.
/// </summary>
public sealed class NotifierHub : BackgroundService, INotificationHub
{
    private const int MaxConcurrentClients = 8;

    // 즉시 받으면 순환 의존성이 된다: NotifierHub -> Telegram -> 명령 처리기 -> 사용자 알림 -> NotifierHub.
    // DI 컨테이너는 순환을 예외로 알리지 않고 그냥 멈춰 버리므로, 필요할 때 꺼내 쓴다.
    private readonly Func<IAdminNotifier> _adminsFactory;
    private readonly ISystemClock _clock;
    private readonly ILogger<NotifierHub> _logger;

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();

    // 답장 도배 방지. 파이프에 연결한 누구든 관리자에게 메시지를 보낼 수 있으므로 제한한다.
    private readonly ReplyRateLimiter _rateLimiter;

    /// <summary>권한 오류를 매번 찍지 않기 위한 플래그.</summary>
    private bool _accessDeniedLogged;

    public NotifierHub(Func<IAdminNotifier> adminsFactory, ISystemClock clock, ILogger<NotifierHub> logger)
    {
        _adminsFactory = adminsFactory;
        _clock = clock;
        _rateLimiter = new ReplyRateLimiter(clock);
        _logger = logger;
    }

    public bool HasClients => !_clients.IsEmpty;

    public async Task<int> BroadcastAsync(
        NotificationEnvelope notification,
        CancellationToken cancellationToken = default)
    {
        var line = JsonUtil.Serialize(notification, indented: false);
        var sent = 0;

        foreach (var (id, client) in _clients)
        {
            try
            {
                await client.SendAsync(line, cancellationToken).ConfigureAwait(false);
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "트레이 앱 전송 실패. 연결을 정리합니다.");
                Remove(id);
            }
        }

        return sent;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("알림 파이프 서버 시작: {Pipe}", NotifierProtocol.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);

                // 연결 처리는 떼어 놓고 곧바로 다음 연결을 기다린다.
                var accepted = pipe;
                pipe = null;
                _ = Task.Run(() => HandleClientAsync(accepted, stoppingToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (UnauthorizedAccessException ex)
            {
                // 여기서 멈추면 트레이 앱 알림이 영영 죽는다.
                // 이미 붙어 있는 연결은 살아 있으므로, 천천히 계속 재시도한다.
                pipe?.Dispose();

                if (!_accessDeniedLogged)
                {
                    _accessDeniedLogged = true;
                    _logger.LogError(ex, "알림 파이프를 만들 권한이 없습니다. 30초마다 다시 시도합니다.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _logger.LogError(ex, "알림 파이프 오류. 5초 뒤 다시 시도합니다.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var encoding = new UTF8Encoding(false);
        var writer = new StreamWriter(pipe, encoding, 8192, leaveOpen: true) { AutoFlush = true };
        var client = new Client(pipe, writer);

        _clients[id] = client;
        _logger.LogInformation("트레이 앱이 연결되었습니다. (연결 {Count}개)", _clients.Count);

        // 연결이 왜 끊겼는지 남긴다. 알림이 안 온다는 신고를 받았을 때 이것부터 본다.
        var reason = "정상 종료";

        try
        {
            using var reader = new StreamReader(pipe, encoding, false, 8192, leaveOpen: true);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    reason = "상대가 연결을 닫음";
                    break;
                }

                if (line.Length == 0) continue;

                await HandleReplyAsync(line, ct).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) reason = "서비스 종료";
        }
        catch (OperationCanceledException)
        {
            reason = "서비스 종료";
        }
        catch (IOException ex)
        {
            reason = $"연결 끊김 ({ex.Message})";
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            _logger.LogDebug(ex, "트레이 앱 연결 처리 중 오류");
        }
        finally
        {
            Remove(id);
            _logger.LogInformation(
                "트레이 앱 연결이 끊어졌습니다: {Reason} (남은 연결 {Count}개)", reason, _clients.Count);
        }
    }

    private async Task HandleReplyAsync(string line, CancellationToken ct)
    {
        NotifierReply? reply;
        try
        {
            reply = JsonUtil.Deserialize<NotifierReply>(line);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "답장을 해석하지 못했습니다.");
            return;
        }

        var text = reply?.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        text = ReplyRateLimiter.Truncate(text);

        if (!_rateLimiter.TryConsume(out var reason))
        {
            _logger.LogWarning("답장을 제한했습니다: {Reason}", reason);
            return;
        }

        var who = ReplyRateLimiter.Sanitize(reply!.UserName);
        var body = $"💬 PC 에서 온 메시지{(who.Length > 0 ? $" ({who})" : string.Empty)}\n\n{ReplyRateLimiter.Sanitize(text)}";

        var sent = await _adminsFactory().NotifyAdminsAsync(body, ct).ConfigureAwait(false);

        // 본문은 로그에 남기지 않는다. 아이의 사생활이고, 로그 인젝션 위험도 있다.
        _logger.LogInformation("PC 답장을 관리자 {Count}명에게 전달했습니다. ({Length}자)", sent, text.Length);
    }

    private void Remove(Guid id)
    {
        if (_clients.TryRemove(id, out var client)) client.Dispose();
    }

    public override void Dispose()
    {
        foreach (var id in _clients.Keys) Remove(id);
        base.Dispose();
    }

    /// <summary>연결된 트레이 앱 하나. 동시 쓰기를 막기 위해 자체 잠금을 둔다.</summary>
    private sealed class Client : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        private volatile bool _disposed;

        public Client(NamedPipeServerStream pipe, StreamWriter writer)
        {
            _pipe = pipe;
            _writer = writer;
        }

        public async Task SendAsync(string line, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Client));

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 기다리는 사이에 연결이 정리됐을 수 있다.
                if (_disposed) throw new ObjectDisposedException(nameof(Client));

                await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            try { _writer.Dispose(); } catch { /* 이미 끊긴 경우 */ }
            try { _pipe.Dispose(); } catch { /* 이미 끊긴 경우 */ }

            // _writeLock 은 일부러 Dispose 하지 않는다.
            // 보내는 중에 연결이 끊기면 Release 가 ObjectDisposedException 을 내고,
            // 그 예외가 정상 종료를 오류로 보이게 만든다.
            // SemaphoreSlim 은 비관리 자원을 쥐지 않으므로 GC 에 맡겨도 안전하다.
        }
    }

    /// <summary>
    /// 아이 계정(표준 사용자)도 연결할 수 있어야 하므로 인증된 사용자에게 읽기/쓰기를 준다.
    /// 이 파이프로는 알림 수신과 답장 전송만 가능하다.
    /// </summary>
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(authenticated, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            NotifierProtocol.PipeName,
            PipeDirection.InOut,
            MaxConcurrentClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }
}
