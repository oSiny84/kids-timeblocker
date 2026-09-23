using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TimeBlocker.Service.Logging;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Ipc;
using TimeBlocker.Shared.Remote;

namespace TimeBlocker.Service.Ipc;

/// <summary>
/// 로컬 관리자 CLI 를 위한 Named Pipe 서버.
///
/// - Named Pipe 는 로컬 전용이므로 외부 네트워크에서 접근할 수 없다.
/// - ACL 로 Administrators 와 SYSTEM 만 연결할 수 있게 한다.
///   (일반 사용자 계정에서는 CLI 로 설정을 바꿀 수 없다)
/// - 핵심 관리 수단은 Telegram 이고, 이 파이프는 로컬 점검용이다.
/// </summary>
public sealed class NamedPipeIpcServer : BackgroundService
{
    private const int MaxConcurrentConnections = 4;

    private readonly IRemoteCommandHandler _handler;
    private readonly FileLogWriter _logWriter;
    private readonly ILogger<NamedPipeIpcServer> _logger;

    public NamedPipeIpcServer(
        IRemoteCommandHandler handler,
        FileLogWriter logWriter,
        ILogger<NamedPipeIpcServer> logger)
    {
        _handler = handler;
        _logWriter = logWriter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Named Pipe 서버 시작: {Pipe}", AppPaths.PipeName);

        // 여러 CLI 가 동시에 붙어도 되도록 몇 개의 수신 루프를 병렬로 돌린다.
        var loops = Enumerable
            .Range(0, MaxConcurrentConnections)
            .Select(_ => AcceptLoopAsync(stoppingToken))
            .ToArray();

        await Task.WhenAll(loops).ConfigureAwait(false);

        _logger.LogInformation("Named Pipe 서버를 중지했습니다.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleConnectionAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (UnauthorizedAccessException ex)
            {
                // 파이프를 만들 권한 자체가 없는 상황(비관리자 콘솔 실행 등).
                // 재시도해도 상황이 바뀌지 않으므로 한 번만 남기고 이 루프를 끝낸다.
                // 로컬 CLI 만 사용할 수 없을 뿐, 차단 기능과 Telegram 제어는 그대로 동작한다.
                _logger.LogWarning(ex,
                    "Named Pipe 를 만들 수 없어 로컬 CLI 연결을 사용할 수 없습니다. " +
                    "서비스를 관리자(LocalSystem) 권한으로 실행하세요.");
                return;
            }
            catch (IOException ex)
            {
                // 클라이언트가 중간에 끊은 경우 등. 다음 연결을 계속 받는다.
                _logger.LogDebug(ex, "Named Pipe 연결이 끊어졌습니다.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Named Pipe 처리 중 오류");

                try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                try { pipe?.Dispose(); } catch { /* 무시 */ }
            }
        }
    }

    /// <summary>SYSTEM + Administrators(+ 실행 계정) 만 접근할 수 있는 파이프를 만든다.</summary>
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        security.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));

        // Windows 는 파이프를 만드는 계정이 DACL 에서 접근 권한을 가지고 있어야 생성을 허용한다.
        // 서비스는 LocalSystem 으로 도므로 보통 위 규칙으로 충분하지만,
        // 콘솔 모드로 직접 실행하는 경우를 위해 실행 계정도 명시적으로 추가한다.
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is { } currentUser && currentUser != system)
            {
                security.AddAccessRule(
                    new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
        }

        return NamedPipeServerStreamAcl.Create(
            AppPaths.PipeName,
            PipeDirection.InOut,
            MaxConcurrentConnections,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var encoding = new UTF8Encoding(false);

        using var reader = new StreamReader(pipe, encoding, false, 8192, leaveOpen: true);
        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);

        IpcResponse response;
        if (string.IsNullOrWhiteSpace(line))
        {
            response = IpcResponse.Fail("빈 요청입니다.");
        }
        else
        {
            response = await ProcessRequestAsync(line, ct).ConfigureAwait(false);
        }

        await using var writer = new StreamWriter(pipe, encoding, 8192, leaveOpen: true) { AutoFlush = false };
        await writer.WriteLineAsync(JsonUtil.Serialize(response, indented: false).AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);

        // 클라이언트가 응답을 다 읽을 때까지 기다린다.
        try { pipe.WaitForPipeDrain(); } catch (IOException) { /* 이미 끊긴 경우 */ }
    }

    private async Task<IpcResponse> ProcessRequestAsync(string line, CancellationToken ct)
    {
        IpcRequest? request;
        try
        {
            request = JsonUtil.Deserialize<IpcRequest>(line);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IPC 요청을 해석하지 못했습니다.");
            return IpcResponse.Fail("요청 형식이 잘못되었습니다.");
        }

        if (request is null) return IpcResponse.Fail("요청이 비어 있습니다.");

        switch (request.Command)
        {
            case IpcCommand.Ping:
                return IpcResponse.Ok("PONG");

            case IpcCommand.GetLogTail:
                var count = Math.Clamp(request.LineCount, 1, 2000);
                return new IpcResponse
                {
                    Success = true,
                    Message = $"최근 로그 {count}줄",
                    LogLines = _logWriter.ReadTail(count)
                };

            case IpcCommand.ExecuteText:
                var source = string.IsNullOrWhiteSpace(request.Source) ? "CLI" : request.Source!;
                var text = await _handler.ExecuteTextAsync(request.Text, source, ct).ConfigureAwait(false);
                return new IpcResponse
                {
                    Success = !text.StartsWith("ERROR", StringComparison.Ordinal),
                    Message = text
                };

            default:
                return IpcResponse.Fail($"지원하지 않는 명령입니다: {request.Command}");
        }
    }
}
