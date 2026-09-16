using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace WXDataStudio.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _logs = new();
    private readonly Dictionary<string, List<MessagePreview>> _demoMessages;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _logs;
        ConversationList.ItemsSource = new[] { "设备与适配器状态", "示例会话 A", "示例群聊" };
        _demoMessages = BuildDemoMessages();
        AddLog("WXDataStudio v0.1 UI shell started.");
        AddLog("Locked baseline: MIX 2S / Android 9 / MIUI 10.3.5 / WeChat 8.0.76 (3141).");
        AddLog("Original snapshots are read-only. Write-back remains disabled until integrity gates pass.");
    }

    private void OnRefreshDevice(object sender, RoutedEventArgs e)
    {
        AddLog("Device probe requested. ADB service wiring is the next implementation task.");
    }

    private void OnCreateSnapshot(object sender, RoutedEventArgs e)
    {
        AddLog("Snapshot requested. Snapshot engine is not enabled in this UI-shell commit.");
    }

    private void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        AddLog("Workspace requested. Editing remains disabled until a validated snapshot is loaded.");
    }

    private void OnRunIntegrityCheck(object sender, RoutedEventArgs e)
    {
        AddLog("Integrity check requested. Current status: UI shell only; no source database modified.");
    }

    private void OnConversationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is not string name)
        {
            return;
        }

        ConversationTitle.Text = name;
        MessageList.ItemsSource = _demoMessages.TryGetValue(name, out var messages)
            ? messages
            : Array.Empty<MessagePreview>();
    }

    private void OnMessageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItem is not MessagePreview message)
        {
            return;
        }

        PropertyType.Text = message.Type;
        PropertyTime.Text = message.Time;
        PropertySender.Text = message.Sender;
        PropertyContent.Text = message.Content;
        PropertyAttachment.Text = message.Attachment ?? string.Empty;
        ReadOnlyFlag.Visibility = message.Sensitive ? Visibility.Visible : Visibility.Collapsed;
        EditButton.IsEnabled = false;
        UndoButton.IsEnabled = false;
    }

    private void AddLog(string text)
    {
        _logs.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
        if (_logs.Count > 300)
        {
            _logs.RemoveAt(0);
        }
    }

    private static Dictionary<string, List<MessagePreview>> BuildDemoMessages() => new()
    {
        ["设备与适配器状态"] =
        [
            new("系统", "适配器", "wechat-android-8.0.76-3141 已锁定；真实解析尚未开启。", "当前", null, false)
        ],
        ["示例会话 A"] =
        [
            new("文字", "我", "这是工作副本界面的示例文字。", "2026-09-17 03:45:00", null, false),
            new("图片", "对方", "图片消息示例", "2026-09-17 03:46:10", "sample.jpg", false),
            new("转账", "系统", "交易类记录仅做只读解析，不提供伪造凭证写回。", "2026-09-17 03:47:20", null, true)
        ],
        ["示例群聊"] =
        [
            new("系统消息", "系统", "群系统消息示例", "2026-09-17 03:48:00", null, false),
            new("文件", "成员A", "附件消息示例", "2026-09-17 03:49:00", "example.pdf", false)
        ]
    };

    private sealed record MessagePreview(
        string Type,
        string Sender,
        string Content,
        string Time,
        string? Attachment,
        bool Sensitive)
    {
        public override string ToString() => $"[{Time}] {Sender} · {Type} · {Content}";
    }
}
