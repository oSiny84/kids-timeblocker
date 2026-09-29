using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace TimeBlocker.Service.Blocking;

public interface IUserSessionNotifier
{
    /// <summary>로그인해 있는 사용자 화면에 경고창을 띄운다. 띄운 세션 수를 돌려준다.</summary>
    int Notify(string title, string message);
}

/// <summary>
/// 서비스는 세션 0(LocalSystem)에서 돌기 때문에 MessageBox 를 그냥 띄우면
/// 아무도 볼 수 없는 화면에 뜬다. 사용자 세션으로 보내려면 WTSSendMessage 를 써야 한다.
///
/// Windows 가 정식으로 제공하는 기능만 쓴다. 화면을 가로채거나 다른 창을 숨기지 않는다.
/// </summary>
public sealed class UserSessionNotifier : IUserSessionNotifier
{
    private const int MbIconExclamation = 0x00000030;
    private const int WtsCurrentServerHandleValue = 0;

    // WTS_CONNECTSTATE_CLASS.WTSActive
    private const int WtsActive = 0;

    private readonly ILogger<UserSessionNotifier> _logger;

    public UserSessionNotifier(ILogger<UserSessionNotifier> logger)
    {
        _logger = logger;
    }

    public int Notify(string title, string message)
    {
        var shown = 0;

        foreach (var sessionId in EnumerateActiveSessions())
        {
            try
            {
                // bWait=false: 사용자가 확인을 누를 때까지 서비스가 멈추면 안 된다.
                var ok = WTSSendMessageW(
                    (IntPtr)WtsCurrentServerHandleValue,
                    sessionId,
                    title, title.Length * 2,
                    message, message.Length * 2,
                    MbIconExclamation,
                    timeout: 0,
                    out _,
                    bWait: false);

                if (ok) shown++;
                else
                {
                    _logger.LogWarning(
                        "세션 {Session} 에 경고를 띄우지 못했습니다. (Win32 {Code})",
                        sessionId, Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "세션 {Session} 경고 표시 실패", sessionId);
            }
        }

        if (shown == 0)
        {
            _logger.LogWarning("경고를 띄울 활성 사용자 세션이 없습니다.");
        }

        return shown;
    }

    /// <summary>로그인해서 실제로 화면을 쓰고 있는 세션만 고른다. (세션 0 은 제외)</summary>
    private IEnumerable<int> EnumerateActiveSessions()
    {
        var buffer = IntPtr.Zero;
        var sessions = new List<int>();

        try
        {
            if (!WTSEnumerateSessionsW((IntPtr)WtsCurrentServerHandleValue, 0, 1, ref buffer, out var count))
            {
                _logger.LogWarning("세션 목록을 읽지 못했습니다. (Win32 {Code})", Marshal.GetLastWin32Error());
                return sessions;
            }

            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(buffer + i * size);

                // 세션 0 은 서비스 전용이라 사람이 보지 못한다.
                if (info.SessionId != 0 && info.State == WtsActive) sessions.Add(info.SessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "세션 열거 실패");
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }

        return sessions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSSendMessageW(
        IntPtr hServer,
        int sessionId,
        string pTitle,
        int titleLength,
        string pMessage,
        int messageLength,
        int style,
        int timeout,
        out int pResponse,
        bool bWait);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr hServer,
        int reserved,
        int version,
        ref IntPtr ppSessionInfo,
        out int pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
