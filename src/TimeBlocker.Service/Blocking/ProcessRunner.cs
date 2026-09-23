using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TimeBlocker.Service.Blocking;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// netsh 등 외부 명령 실행 헬퍼.
/// 실패해도 예외를 던지지 않고 결과를 반환한다. (방화벽 명령 실패로 서비스가 죽으면 안 된다)
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        ILogger logger,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, $"{fileName} 를 시작하지 못했습니다.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process, logger);
                return new ProcessResult(-1, string.Empty, $"{fileName} 실행이 시간 초과되었습니다.");
            }

            var stdout = await outputTask.ConfigureAwait(false);
            var stderr = await errorTask.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "외부 명령 실행 실패: {File} {Args}", fileName, arguments);
            return new ProcessResult(-1, string.Empty, ex.Message);
        }
    }

    private static void TryKill(Process process, ILogger logger)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "시간 초과된 프로세스를 종료하지 못했습니다.");
        }
    }
}
