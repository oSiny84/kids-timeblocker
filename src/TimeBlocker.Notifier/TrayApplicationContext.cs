using System.Drawing;
using System.Windows.Forms;
using TimeBlocker.Shared.Notifications;

namespace TimeBlocker.Notifier;

/// <summary>
/// 트레이에 살면서 알림을 기다린다.
///
/// 아이가 종료할 수 있게 그대로 둔다. 숨기거나 종료를 막지 않는다.
/// 트레이 앱이 없어도 서비스가 Windows 기본 메시지 창으로 경고하므로
/// 차단 기능 자체는 영향받지 않는다.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifierClient _client = new();
    private readonly NotifyIcon _tray;

    /// <summary>열려 있는 알림 창. 여러 개가 쌓이지 않게 한 번에 하나만 띄운다.</summary>
    private NotificationForm? _openForm;

    public TrayApplicationContext()
    {
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "TimeBlocker (연결 중...)",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _tray.DoubleClick += (_, _) => ShowStatusBalloon();

        _client.NotificationReceived += OnNotification;
        _client.ConnectionChanged += OnConnectionChanged;
        _client.Start();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("상태 보기", null, (_, _) => ShowStatusBalloon());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitThread());

        return menu;
    }

    private void ShowStatusBalloon()
    {
        _tray.BalloonTipTitle = "TimeBlocker";
        _tray.BalloonTipText = _client.IsConnected
            ? "TimeBlocker 서비스에 연결되어 있습니다."
            : "TimeBlocker 서비스에 연결되어 있지 않습니다.\n서비스가 시작되면 자동으로 다시 연결됩니다.";
        _tray.ShowBalloonTip(4000);
    }

    private void OnConnectionChanged(bool connected)
    {
        // 파이프 읽기 스레드에서 온다. UI 는 반드시 UI 스레드에서 건드려야 한다.
        RunOnUiThread(() => _tray.Text = connected ? "TimeBlocker" : "TimeBlocker (연결 끊김)");
    }

    private void OnNotification(NotificationEnvelope notification)
    {
        RunOnUiThread(() =>
        {
            // 이전 알림이 떠 있으면 닫고 새 것을 보여준다.
            // 경고가 여러 번 오면 최신 내용이 가장 중요하다.
            if (_openForm is { IsDisposed: false })
            {
                _openForm.Close();
                _openForm = null;
            }

            var form = new NotificationForm(notification, _client.SendReplyAsync);
            form.FormClosed += (_, _) => _openForm = null;
            _openForm = form;

            form.Show();
            form.Activate();
        });
    }

    /// <summary>트레이 아이콘의 핸들을 빌려 UI 스레드로 넘어간다.</summary>
    private void RunOnUiThread(Action action)
    {
        var form = _openForm;

        try
        {
            if (form is { IsDisposed: false, IsHandleCreated: true } && form.InvokeRequired)
            {
                form.BeginInvoke(action);
                return;
            }

            // 창이 없을 때를 위해 동기화 컨텍스트를 쓴다.
            if (SynchronizationContext.Current is null && UiContext is not null)
            {
                UiContext.Post(_ => action(), null);
                return;
            }

            action();
        }
        catch (ObjectDisposedException)
        {
            // 종료 중이다.
        }
        catch (InvalidOperationException)
        {
            // 핸들이 아직 없다.
        }
    }

    /// <summary>Program 이 UI 스레드에서 설정해 준다.</summary>
    public static SynchronizationContext? UiContext { get; set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }
}
