using System.IO.Pipes;
using System.Text;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;

namespace TimeBlocker.Shared.Ipc;

public interface IIpcClient
{
    Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// GUI -> 서비스 Named Pipe 클라이언트.
///
/// 프로토콜: 연결 -> 요청 JSON 1줄 -> 응답 JSON 1줄 -> 연결 종료.
/// Named Pipe 는 로컬 전용이므로 외부 네트워크에서 접근할 수 없다.
/// </summary>
public sealed class NamedPipeIpcClient : IIpcClient
{
    private readonly string _pipeName;
    private readonly int _connectTimeoutMs;

    public NamedPipeIpcClient(string? pipeName = null, int connectTimeoutMs = 3000)
    {
        _pipeName = pipeName ?? AppPaths.PipeName;
        _connectTimeoutMs = connectTimeoutMs;
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            await pipe.ConnectAsync(_connectTimeoutMs, cancellationToken).ConfigureAwait(false);

            var payload = JsonUtil.Serialize(request, indented: false);

            // StreamWriter/Reader 를 쓰되 파이프를 닫지 않도록 leaveOpen 을 사용한다.
            var encoding = new UTF8Encoding(false);
            await using (var writer = new StreamWriter(pipe, encoding, 8192, leaveOpen: true) { AutoFlush = false })
            {
                await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }

            using var reader = new StreamReader(pipe, encoding, false, 8192, leaveOpen: true);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(line))
            {
                return IpcResponse.Fail("서비스가 응답하지 않았습니다.");
            }

            return JsonUtil.Deserialize<IpcResponse>(line) ?? IpcResponse.Fail("응답을 해석하지 못했습니다.");
        }
        catch (TimeoutException)
        {
            return IpcResponse.Fail("서비스에 연결하지 못했습니다. TimeBlocker 서비스가 실행 중인지 확인하세요.");
        }
        catch (OperationCanceledException)
        {
            return IpcResponse.Fail("요청이 취소되었습니다.");
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail($"서비스 통신 오류: {ex.Message}");
        }
    }
}
