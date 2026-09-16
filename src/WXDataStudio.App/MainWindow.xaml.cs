using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WXDataStudio.App.Models;
using WXDataStudio.App.Services;

namespace WXDataStudio.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _logs = new();
    private readonly Dictionary<string, List<MessagePreview>> _demoMessages;
    private readonly AdbService _adb = new();
    private readonly SnapshotService _snapshots;
    private readonly SnapshotIntegrityService _integrity = new();
    private readonly DatabaseInspectorService _dbInspector = new();
    private readonly WeChatDatabaseReader _dbReader = new();
    private readonly WorkspaceService _workspaceService = new();
    private readonly WorkspaceDiffService _diffService = new();
    private readonly MediaLocatorService _mediaLocator;
    private DeviceInfo? _device;
    private WorkspaceDocument? _workspace;
    private ConversationItem? _currentConversation;
    private IReadOnlyList<WeChatMessage> _currentMessages = Array.Empty<WeChatMessage>();
    private string? _currentDbPath;
    private string? _latestSnapshotDirectory;

    public MainWindow()
    {
        InitializeComponent();
        _snapshots = new SnapshotService(_adb);
        _mediaLocator = new MediaLocatorService(_adb);
        LogList.ItemsSource = _logs;
        ConversationList.ItemsSource = new[] { "Device & adapter", "Demo chat", "Demo group" };
        _demoMessages = BuildDemoMessages();
        AddLog("WXDataStudio v0.2 started.");
        AddLog("Locked baseline: MIX 2S / Android 9 / MIUI 10.3.5 / WeChat 8.0.76 (3141).");
        AddLog("Read-only snapshot parser and local workspace editor are enabled.");
        AddLog("Phone write-back remains disabled until validation and rollback gates pass.");
    }

    private async void OnRefreshDevice(object sender, RoutedEventArgs e)
    {
        try
        {
            DeviceBadge.Text = "正在检测设备...";
            AddLog($"ADB: {_adb.AdbPath}");
            if (!await _adb.IsAvailableAsync())
                throw new InvalidOperationException("ADB is not available.");
            _device = await _adb.ProbeAsync();
            DeviceBadge.Text = $"{_device.Model} / WeChat {_device.WeChatVersion}";
            AddLog($"Device: {_device.Model} ({_device.Codename}), Android {_device.AndroidVersion}, {_device.MiuiVersion}");
            AddLog($"Root: {_device.RootAvailable}; bootloader unlocked: {_device.BootloaderUnlocked}");
            AddLog($"WeChat: {_device.WeChatVersion} ({_device.WeChatVersionCode}); private account: {_device.AccountDirectory}");
            AddLog($"External media account: {_device.ExternalAccountDirectory}");
            AddLog(_device.MatchesLockedBaseline
                ? "Device matches the locked baseline."
                : "WARNING: device does not fully match the locked baseline.");
        }
        catch (Exception ex)
        {
            DeviceBadge.Text = "设备检测失败";
            AddLog($"Device detection failed: {ex.Message}");
            MessageBox.Show(ex.Message, "设备检测失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnCreateSnapshot(object sender, RoutedEventArgs e)
    {
        try
        {
            _device ??= await _adb.ProbeAsync();
            if (!_device.MatchesLockedBaseline)
                throw new InvalidOperationException("Connected device does not match the validated baseline.");
            var result = await _snapshots.CreateDatabaseSnapshotAsync(_device, AddLog);
            _latestSnapshotDirectory = result.DirectoryPath;
            AddLog($"Snapshot directory: {result.DirectoryPath}");
            MessageBox.Show("只读数据库快照完成。\n\n" + result.DirectoryPath,
                "快照完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AddLog($"Snapshot failed: {ex.Message}");
            MessageBox.Show(ex.Message, "快照失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnAnalyzeSnapshot(object sender, RoutedEventArgs e)
    {
        try
        {
            _latestSnapshotDirectory ??= FindLatestSnapshotDirectory();
            if (string.IsNullOrWhiteSpace(_latestSnapshotDirectory))
                throw new InvalidOperationException("还没有可解析的快照，请先创建快照。");
            var db = Path.Combine(_latestSnapshotDirectory, "EnMicroMsg.db");
            var info = await _dbInspector.InspectAsync(db);
            _currentDbPath = db;
            AddLog($"DB inspect: {info.Status}; {info.Size:N0} bytes.");
            AddLog($"DB header: {info.HeaderHex[..Math.Min(32, info.HeaderHex.Length)]}...");

            var password = Environment.GetEnvironmentVariable("WXDS_DB_PASSWORD");
            if (info.AppearsEncrypted && string.IsNullOrWhiteSpace(password))
            {
                AddLog("Encrypted WCDB detected. Read-only credential provider is not resolved yet.");
                MessageBox.Show("已确认这是加密 WCDB。\n当前 v0.2 已完成真实表解析器，但还没有取得本机 8.0.76 的数据库打开参数。\n不会猜测或改动原库。",
                    "数据库已识别", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var options = new DatabaseOpenOptions { Password = password, CipherCompatibility = 1 };
            var conversations = await _dbReader.LoadConversationsAsync(db, options);
            ConversationList.ItemsSource = conversations;
            _workspace = null;
            _currentConversation = null;
            _currentMessages = Array.Empty<WeChatMessage>();
            AddLog($"Loaded {conversations.Count:N0} conversations from the snapshot.");
        }
        catch (Exception ex)
        {
            AddLog($"Snapshot analysis failed: {ex.Message}");
            MessageBox.Show(ex.Message, "解析失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        if (_currentConversation is not null && _currentMessages.Count > 0 &&
            !string.IsNullOrWhiteSpace(_latestSnapshotDirectory))
        {
            _workspace = _workspaceService.Create(
                _latestSnapshotDirectory, _currentConversation, _currentMessages);
            MessageList.ItemsSource = _workspace.Messages;
            AddLog($"Workspace created for {_currentConversation.EffectiveName}: {_workspace.Messages.Count} messages.");
            return;
        }

        _latestSnapshotDirectory ??= FindLatestSnapshotDirectory();
        if (string.IsNullOrWhiteSpace(_latestSnapshotDirectory))
        {
            MessageBox.Show("还没有快照。", "工作副本", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", _latestSnapshotDirectory) { UseShellExecute = true });
        AddLog($"Opened snapshot directory: {_latestSnapshotDirectory}");
    }

    private async void OnRunIntegrityCheck(object sender, RoutedEventArgs e)
    {
        _latestSnapshotDirectory ??= FindLatestSnapshotDirectory();
        if (string.IsNullOrWhiteSpace(_latestSnapshotDirectory))
        {
            AddLog("Integrity check skipped: no snapshot available.");
            return;
        }
        try
        {
            var issues = await _integrity.CheckAsync(_latestSnapshotDirectory);
            if (issues.Count == 0)
            {
                AddLog("Integrity check passed: files, sizes and SHA-256 values match.");
                MessageBox.Show("快照完整性检查通过。", "完整性检查",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var issue in issues) AddLog("Integrity issue: " + issue);
            MessageBox.Show(string.Join(Environment.NewLine, issues),
                "完整性问题", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AddLog($"Integrity check failed: {ex.Message}");
        }
    }

    private void OnShowDiff(object sender, RoutedEventArgs e)
    {
        if (_workspace is null)
        {
            MessageBox.Show("当前没有工作副本。", "差异", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var diffs = _diffService.GetDiffs(_workspace);
        if (diffs.Count == 0)
        {
            MessageBox.Show("工作副本没有修改。", "差异", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var text = string.Join(Environment.NewLine,
            diffs.Take(100).Select(x => $"#{x.MessageId} {x.Field}: {Short(x.Before)} -> {Short(x.After)}"));
        if (diffs.Count > 100) text += $"\n... 还有 {diffs.Count - 100} 项";
        MessageBox.Show(text, $"差异（{diffs.Count} 项）", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnConversationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is ConversationItem conversation)
        {
            _currentConversation = conversation;
            ConversationTitle.Text = conversation.EffectiveName;
            if (string.IsNullOrWhiteSpace(_currentDbPath)) return;
            try
            {
                var password = Environment.GetEnvironmentVariable("WXDS_DB_PASSWORD");
                var options = new DatabaseOpenOptions { Password = password, CipherCompatibility = 1 };
                _currentMessages = await _dbReader.LoadMessagesAsync(
                    _currentDbPath, conversation.Username, options);
                MessageList.ItemsSource = _currentMessages;
                _workspace = null;
                AddLog($"Loaded {_currentMessages.Count:N0} messages: {conversation.EffectiveName}");
            }
            catch (Exception ex)
            {
                AddLog($"Message load failed: {ex.Message}");
            }
            return;
        }

        if (ConversationList.SelectedItem is not string name) return;
        ConversationTitle.Text = name;
        MessageList.ItemsSource = _demoMessages.TryGetValue(name, out var messages)
            ? messages
            : Array.Empty<MessagePreview>();
    }

    private async void OnMessageSelected(object sender, SelectionChangedEventArgs e)
    {
        switch (MessageList.SelectedItem)
        {
            case WeChatMessage message:
                FillEditor(message);
                SetEditorMode(false, message.Sensitive);
                if (_device is not null && message.Kind is MessageKind.Image or MessageKind.Video
                    or MessageKind.Voice or MessageKind.Emoji or MessageKind.File)
                {
                    try
                    {
                        var media = await _mediaLocator.ResolveAsync(_device, message);
                        PropertyAttachment.Text = media.Summary;
                        MediaOriginalPath.Text = media.Candidates.FirstOrDefault()?.RemotePath ?? message.ImgPath ?? "";
                    }
                    catch (Exception ex) { AddLog($"Media resolve failed: {ex.Message}"); }
                }
                break;
            case WorkspaceMessage edited:
                FillEditor(edited);
                SetEditorMode(edited.CanEdit, edited.Sensitive);
                break;
            case MessagePreview preview:
                FillEditor(preview);
                SetEditorMode(false, preview.Sensitive);
                break;
        }
    }

    private async void OnSaveWorkspaceMessage(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || MessageList.SelectedItem is not WorkspaceMessage msg || !msg.CanEdit)
            return;
        try
        {
            _workspaceService.EditContent(_workspace, msg.LocalId, PropertyContent.Text);
            _workspaceService.EditAttachment(_workspace, msg.LocalId,
                string.IsNullOrWhiteSpace(PropertyAttachment.Text) ? null : PropertyAttachment.Text);
            if (DateTimeOffset.TryParse(PropertyTime.Text, out var parsed))
                _workspaceService.EditTime(_workspace, msg.LocalId, parsed.ToUnixTimeSeconds());

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WXDataStudio", "workspaces");
            var path = await _workspaceService.SaveAsync(_workspace, root);
            MessageList.Items.Refresh();
            FillEditor(msg);
            AddLog($"Workspace saved: {path}");
        }
        catch (Exception ex)
        {
            AddLog($"Workspace save failed: {ex.Message}");
            MessageBox.Show(ex.Message, "保存工作副本失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnUndoWorkspaceMessage(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || MessageList.SelectedItem is not WorkspaceMessage msg) return;
        _workspaceService.Revert(_workspace, msg.LocalId);
        MessageList.Items.Refresh();
        FillEditor(msg);
        AddLog($"Workspace message reverted: {msg.LocalId}");
    }

    private void FillEditor(WeChatMessage m)
    {
        PropertyType.Text = $"{m.Kind} / raw {m.RawType}";
        PropertySender.Text = string.IsNullOrWhiteSpace(m.Sender) ? m.Direction : m.Sender;
        PropertyDirection.Text = m.Direction;
        PropertyStatus.Text = m.StatusText;
        PropertyLocalId.Text = m.LocalId.ToString();
        PropertyServerId.Text = m.ServerId?.ToString() ?? "";
        PropertyContent.Text = m.Content;
        PropertyAttachment.Text = m.ImgPath ?? m.Reserved ?? "";
        PropertyQuote.Text = "";
        PropertyAtInfo.Text = "";
        PropertyTime.Text = m.DisplayTime;
        DateDisplay.Text = m.DisplayTime;
        DateSort.Text = m.Sequence.ToString();
        DateTimezone.Text = TimeZoneInfo.Local.DisplayName;
        MediaType.Text = m.Kind.ToString();
        MediaOriginalPath.Text = m.ImgPath ?? "";
        MediaThumbPath.Text = "";
        MediaDimensions.Text = "";
        MediaSize.Text = "";
        MediaDuration.Text = "";
        FillMetadata(m.Kind, m.Content);
    }

    private void FillEditor(WorkspaceMessage m)
    {
        PropertyType.Text = $"{m.Kind} / raw {m.RawType}";
        PropertySender.Text = string.IsNullOrWhiteSpace(m.Sender) ? m.Direction : m.Sender;
        PropertyDirection.Text = m.Direction;
        PropertyStatus.Text = m.RawStatus.ToString();
        PropertyLocalId.Text = m.LocalId.ToString();
        PropertyServerId.Text = m.ServerId?.ToString() ?? "";
        PropertyContent.Text = m.Content;
        PropertyAttachment.Text = m.Attachment ?? "";
        PropertyQuote.Text = "";
        PropertyAtInfo.Text = "";
        PropertyTime.Text = m.DisplayTime;
        DateDisplay.Text = m.DisplayTime;
        DateSort.Text = m.Sequence.ToString();
        DateTimezone.Text = TimeZoneInfo.Local.DisplayName;
        MediaType.Text = m.Kind.ToString();
        MediaOriginalPath.Text = m.Attachment ?? "";
        MediaThumbPath.Text = "";
        MediaDimensions.Text = "";
        MediaSize.Text = "";
        MediaDuration.Text = "";
        FillMetadata(m.Kind, m.Content);
    }

    private void FillEditor(MessagePreview m)
    {
        PropertyType.Text = m.Type;
        PropertySender.Text = m.Sender;
        PropertyDirection.Text = "示例";
        PropertyStatus.Text = "示例";
        PropertyLocalId.Text = "";
        PropertyServerId.Text = "";
        PropertyContent.Text = m.Content;
        PropertyAttachment.Text = m.Attachment ?? "";
        PropertyTime.Text = m.Time;
        DateDisplay.Text = m.Time;
        MediaType.Text = m.Type;
        MediaOriginalPath.Text = m.Attachment ?? "";
        FillMetadata(ParseDemoKind(m.Type), m.Content);
    }

    private void FillMetadata(MessageKind kind, string content)
    {
        var meta = MessageMetadataParser.Parse(kind, content);
        CardTitle.Text = meta.Title;
        CardDescription.Text = meta.Description;
        CardUrl.Text = meta.Url;
        CardAppId.Text = meta.AppId;
        LocationAddress.Text = meta.Address;
        LocationLatLng.Text = string.IsNullOrWhiteSpace(meta.Latitude) && string.IsNullOrWhiteSpace(meta.Longitude)
            ? "" : $"{meta.Latitude}, {meta.Longitude}";
        TransactionType.Text = meta.TransactionType;
        TransactionAmount.Text = meta.TransactionAmount;
        TransactionStatus.Text = meta.TransactionStatus;
        TransactionMemo.Text = meta.TransactionMemo;
    }

    private void SetEditorMode(bool editable, bool sensitive)
    {
        ReadOnlyFlag.Visibility = sensitive ? Visibility.Visible : Visibility.Collapsed;
        var allow = editable && !sensitive;
        PropertyContent.IsReadOnly = !allow;
        PropertyTime.IsReadOnly = !allow;
        PropertyAttachment.IsReadOnly = !allow;
        EditButton.IsEnabled = allow;
        UndoButton.IsEnabled = allow;
    }

    private void AddLog(string text)
    {
        Dispatcher.Invoke(() =>
        {
            _logs.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
            if (_logs.Count > 300) _logs.RemoveAt(0);
            if (_logs.Count > 0) LogList.ScrollIntoView(_logs[^1]);
        });
    }

    private static string? FindLatestSnapshotDirectory()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WXDataStudio", "snapshots");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root)
            .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string Short(string value)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 70 ? oneLine : oneLine[..70] + "…";
    }

    private static MessageKind ParseDemoKind(string value) => value switch
    {
        "Text" => MessageKind.Text,
        "Image" => MessageKind.Image,
        "File" => MessageKind.File,
        "Transfer" => MessageKind.Transfer,
        "System" => MessageKind.System,
        _ => MessageKind.Unknown
    };

    private static Dictionary<string, List<MessagePreview>> BuildDemoMessages() => new()
    {
        ["Device & adapter"] =
        [
            new("System", "Adapter", "wechat-android-8.0.76-3141 locked; snapshot parser is wired.", "Now", null, false)
        ],
        ["Demo chat"] =
        [
            new("Text", "Me", "Sample workspace text message.", "2026-09-17 03:45:00", null, false),
            new("Image", "Peer", "Sample image message", "2026-09-17 03:46:10", "sample.jpg", false),
            new("Transfer", "System", "Transaction-class records stay read-only.", "2026-09-17 03:47:20", null, true)
        ],
        ["Demo group"] =
        [
            new("System", "System", "Sample group system message", "2026-09-17 03:48:00", null, false),
            new("File", "Member A", "Sample attachment message", "2026-09-17 03:49:00", "example.pdf", false)
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