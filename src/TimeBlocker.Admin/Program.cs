using System.Security.Principal;
using TimeBlocker.Shared.Ipc;

namespace TimeBlocker.Admin;

/// <summary>
/// 로컬 관리자용 CLI.
///
/// 핵심 관리 수단은 Telegram 이고, 이 CLI 는 PC 앞에서 점검할 때 쓰는 보조 도구다.
/// 실제 상태 변경은 항상 Windows Service 가 수행한다. (CLI 는 명령을 전달만 한다)
///
/// 사용 예:
///   TimeBlocker.Admin.exe status
///   TimeBlocker.Admin.exe youtube 30
///   TimeBlocker.Admin.exe schedule mon 21:00 07:00
///   TimeBlocker.Admin.exe logs 100
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var first = args[0].TrimStart('-', '/').ToLowerInvariant();

        if (first is "?" or "usage")
        {
            PrintUsage();
            return 0;
        }

        // Named Pipe 는 Administrators/SYSTEM 만 접근할 수 있게 되어 있다.
        // 먼저 확인해서 사용자에게 분명한 안내를 준다.
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("관리자 권한이 필요합니다. 관리자 권한 터미널에서 다시 실행하세요.");
            return 2;
        }

        var client = new NamedPipeIpcClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            if (first == "logs")
            {
                var count = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 100;
                return await ShowLogsAsync(client, count, cts.Token).ConfigureAwait(false);
            }

            // 나머지는 모두 서비스의 명령 처리기로 넘긴다. (Telegram 과 동일한 문법)
            var text = string.Join(' ', args);
            var response = await client.SendAsync(new IpcRequest
            {
                Command = IpcCommand.ExecuteText,
                Text = text,
                Source = $"CLI:{Environment.UserName}"
            }, cts.Token).ConfigureAwait(false);

            Console.WriteLine(response.Message);
            return response.Success ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("서비스 응답이 없어 시간 초과되었습니다.");
            return 3;
        }
    }

    private static async Task<int> ShowLogsAsync(NamedPipeIpcClient client, int count, CancellationToken ct)
    {
        var response = await client.SendAsync(new IpcRequest
        {
            Command = IpcCommand.GetLogTail,
            LineCount = Math.Clamp(count, 1, 2000)
        }, ct).ConfigureAwait(false);

        if (!response.Success)
        {
            Console.Error.WriteLine(response.Message);
            return 1;
        }

        foreach (var line in response.LogLines ?? new List<string>())
        {
            Console.WriteLine(line);
        }

        return 0;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            TimeBlocker.Admin - 로컬 관리자 CLI (관리자 권한 필요)

            상태
              TimeBlocker.Admin.exe status
              TimeBlocker.Admin.exe targets
              TimeBlocker.Admin.exe schedule
              TimeBlocker.Admin.exe ping
              TimeBlocker.Admin.exe version
              TimeBlocker.Admin.exe logs 100

            일시 허용 / 차단
              TimeBlocker.Admin.exe youtube 30
              TimeBlocker.Admin.exe roblox 60
              TimeBlocker.Admin.exe all 20
              TimeBlocker.Admin.exe lock
              TimeBlocker.Admin.exe lock youtube

            설정
              TimeBlocker.Admin.exe schedule mon 21:00 07:00
              TimeBlocker.Admin.exe schedule mon-thu 21:00 07:00
              TimeBlocker.Admin.exe schedule sat off
              TimeBlocker.Admin.exe enable youtube
              TimeBlocker.Admin.exe disable roblox
              TimeBlocker.Admin.exe maxpermit 120
              TimeBlocker.Admin.exe reload

            명령 문법은 Telegram 과 동일합니다. 'help' 로 전체 목록을 볼 수 있습니다.
            Telegram Bot Token / 관리자 User ID 는 보안상 CLI 로 바꿀 수 없고
            TimeBlocker.Service.exe set-token / set-admin 으로만 설정합니다.
            """);
    }
}
