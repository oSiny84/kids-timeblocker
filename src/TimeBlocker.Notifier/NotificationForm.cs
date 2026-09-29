using System.Drawing;
using System.Windows.Forms;
using TimeBlocker.Shared.Notifications;

namespace TimeBlocker.Notifier;

/// <summary>
/// 알림 하나를 보여주는 창. 답장 칸이 함께 있다.
///
/// 항상 위에 뜨지만 화면을 잠그거나 닫기를 막지는 않는다.
/// 아이가 창을 닫아도 차단 자체는 서비스가 하므로 문제없다.
/// </summary>
public sealed class NotificationForm : Form
{
    private readonly Func<string, Task<bool>> _sendReply;
    private readonly TextBox _replyBox = new();
    private readonly Button _sendButton = new();
    private readonly Label _resultLabel = new();

    public NotificationForm(NotificationEnvelope notification, Func<string, Task<bool>> sendReply)
    {
        _sendReply = sendReply;

        Text = notification.Title;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(440, notification.AllowReply ? 320 : 200);
        Font = new Font("Segoe UI", 10F);

        var accent = notification.Kind switch
        {
            NotificationKind.Warning => Color.FromArgb(200, 80, 0),
            NotificationKind.Terminated => Color.FromArgb(170, 40, 40),
            _ => Color.FromArgb(30, 90, 170)
        };

        var header = new Label
        {
            Text = notification.Kind switch
            {
                NotificationKind.Warning => "잠깐만요",
                NotificationKind.Terminated => "종료되었습니다",
                _ => "부모님 메시지"
            },
            ForeColor = accent,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            Location = new Point(16, 14),
            Size = new Size(408, 28)
        };

        var body = new Label
        {
            Text = notification.Body,
            Location = new Point(16, 48),
            Size = new Size(408, 100),
            AutoSize = false
        };

        Controls.Add(header);
        Controls.Add(body);

        if (notification.AllowReply) AddReplyControls();

        var close = new Button
        {
            Text = "닫기",
            Location = new Point(344, ClientSize.Height - 40),
            Size = new Size(80, 28),
            DialogResult = DialogResult.Cancel
        };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        CancelButton = close;
    }

    private void AddReplyControls()
    {
        var prompt = new Label
        {
            Text = "답장 (부모님 휴대폰으로 전달됩니다)",
            Location = new Point(16, 156),
            Size = new Size(408, 22),
            ForeColor = Color.DimGray
        };

        _replyBox.Location = new Point(16, 180);
        _replyBox.Size = new Size(408, 60);
        _replyBox.Multiline = true;
        _replyBox.MaxLength = NotifierProtocol.MaxReplyLength;
        _replyBox.ScrollBars = ScrollBars.Vertical;

        _sendButton.Text = "보내기";
        _sendButton.Location = new Point(344, 246);
        _sendButton.Size = new Size(80, 28);
        _sendButton.Click += async (_, _) => await SendAsync();

        _resultLabel.Location = new Point(16, 250);
        _resultLabel.Size = new Size(320, 22);
        _resultLabel.ForeColor = Color.DimGray;

        Controls.Add(prompt);
        Controls.Add(_replyBox);
        Controls.Add(_sendButton);
        Controls.Add(_resultLabel);
    }

    private async Task SendAsync()
    {
        var text = _replyBox.Text.Trim();
        if (text.Length == 0)
        {
            _resultLabel.ForeColor = Color.DimGray;
            _resultLabel.Text = "보낼 내용을 적어주세요.";
            return;
        }

        _sendButton.Enabled = false;
        _resultLabel.ForeColor = Color.DimGray;
        _resultLabel.Text = "보내는 중...";

        var ok = await _sendReply(text);

        if (ok)
        {
            _resultLabel.ForeColor = Color.SeaGreen;
            _resultLabel.Text = "보냈습니다.";
            _replyBox.ReadOnly = true;
        }
        else
        {
            // 서비스와 연결이 끊긴 상태다. 다시 눌러볼 수 있게 열어둔다.
            _resultLabel.ForeColor = Color.Firebrick;
            _resultLabel.Text = "보내지 못했습니다. 잠시 뒤 다시 시도하세요.";
            _sendButton.Enabled = true;
        }
    }
}
