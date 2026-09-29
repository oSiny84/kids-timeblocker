using System.Windows.Forms;

namespace TimeBlocker.Notifier;

/// <summary>
/// 사용자 세션에서 도는 알림 트레이 앱.
///
/// 서비스(세션 0)는 화면에 창을 띄울 수 없으므로 이 앱이 대신 보여주고,
/// 아이의 답장을 서비스로 되돌려 보낸다.
/// 차단 정책은 전혀 건드리지 않는다. 이 앱을 꺼도 차단은 그대로 동작한다.
/// </summary>
internal static class Program
{
    /// <summary>로그인 계정마다 하나만 뜨게 한다.</summary>
    private const string SingleInstanceMutexName = "Local\\TimeBlocker.Notifier.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance) return;

        ApplicationConfiguration.Initialize();

        // 파이프 스레드에서 UI 로 넘어올 때 쓴다.
        TrayApplicationContext.UiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(TrayApplicationContext.UiContext);

        using var context = new TrayApplicationContext();
        Application.Run(context);
    }
}
