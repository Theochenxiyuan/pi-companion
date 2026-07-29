using System.Drawing;
using System.Media;
using Forms = System.Windows.Forms;

namespace PiCompanion.Desktop.Tray;

using PiCompanion.Desktop.Branding;
using PiCompanion.Desktop.Localization;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly Forms.ToolStripItem _openChatItem;
    private readonly Forms.ToolStripItem _toggleMonitorItem;
    private readonly Forms.ToolStripItem _exitItem;
    private bool _disposed;

    public TrayIconService(
        Action openChat,
        Action toggleMonitor,
        Action exit)
    {
        _icon = PiAppIcon.CreateTrayIcon();
        var menu = new Forms.ContextMenuStrip();
        _openChatItem = menu.Items.Add(string.Empty, null, (_, _) => openChat());
        _toggleMonitorItem = menu.Items.Add(string.Empty, null, (_, _) => toggleMonitor());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _exitItem = menu.Items.Add(string.Empty, null, (_, _) => exit());

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Pi Companion — Explorer to Composer",
            Icon = _icon,
            ContextMenuStrip = menu,
        };
        RefreshLocalization();
        _notifyIcon.DoubleClick += (_, _) => openChat();
        _notifyIcon.BalloonTipClicked += (_, _) => openChat();
    }

    public void RefreshLocalization()
    {
        _openChatItem.Text = DesktopLocalizer.Text("打开智能体对话", "Open Agent Chat");
        _toggleMonitorItem.Text = DesktopLocalizer.Text("显示 / 隐藏任务监视器", "Show / hide Monitor");
        _exitItem.Text = DesktopLocalizer.Text("退出 Pi Companion", "Exit Pi Companion");
    }

    public void Show() => _notifyIcon.Visible = true;

    public void ShowNotification(string title, string message, bool isFailure, bool playSound)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(
            5000,
            title,
            message,
            isFailure ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);
        if (playSound)
        {
            (isFailure ? SystemSounds.Hand : SystemSounds.Asterisk).Play();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

}
