using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
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
    private readonly WorkspaceMediaService _workspaceMedia = new();
    private readonly MigrationReadinessService _migrationReadiness = new();
    private readonly TimelineValidationService _timelineValidation = new();
    private readonly MigrationReportExporter _migrationExporter = new();
    private readonly SnapshotCatalogService _snapshotCatalog = new();
    private readonly RollbackPackageService _rollbackPackages = new();
    private readonly LegacySc1PageDecryptService _sc1Decrypt = new();
    private readonly MessageCensusService _messageCensus = new();
    private readonly ChatExportService _chatExport = new();
    private readonly MediaLocatorService _mediaLocator;
    private readonly LegacyWeChatKeyCandidateService _legacyKeyCandidates;
    private readonly DatabaseCredentialResolver _credentialResolver;
    private DeviceInfo? _device;
    private WorkspaceDocument? _workspace;
    private ConversationItem? _currentConversation;
    private IReadOnlyList<ConversationItem> _currentConversations = Array.Empty<ConversationItem>();
    private IReadOnlyList<WeChatMessage> _currentMessages = Array.Empty<WeChatMessage>();
    private string? _currentDbPath;
    private string? _latestSnapshotDirectory;
    private DatabaseCredentialResolution? _credential;
    private DatabaseOpenOptions? _currentDbOptions;
    private string? _manualDatabaseKey;
    private bool _manualDatabaseKeyIsRawHex;
    private SnapshotReadSession? _readSession;

    public MainWindow()
    {
        InitializeComponent();
        _snapshots = new SnapshotService(_adb);
        _mediaLocator = new MediaLocatorService(_adb);
        _legacyKeyCandidates = new LegacyWeChatKeyCandidateService(_adb);
        _credentialResolver = new DatabaseCredentialResolver(_dbReader, _legacyKeyCandidates);
        LogList.ItemsSource = _logs;
        ConversationList.ItemsSource = new[] { "Device & adapter", "Demo chat", "Demo group" };
        _demoMessages = BuildDemoMessages();
        BuildStampText.Text = BuildStamp();
        AddLog($"WXDataStudio started ({BuildStamp()}).");
        AddLog("Locked baseline: MIX 2S / Android 9 / MIUI 10.3.5 / WeChat 8.0.76 (3141).");
        AddLog("Read-only snapshot parser and local workspace editor are enabled.");
        AddLog("Phone write-back remains disabled until validation and rollback gates pass.");
        Loaded += async (_, _) => await InitializeOfflineStateAsync();
    }

    private async Task InitializeOfflineStateAsync()
    {
        try
        {
            var latest = await _snapshotCatalog.FindLatestUsableAsync();
            if (latest is null)
            {
                DeviceBadge.Text = "手机未连接";
                SnapshotBadge.Text = "";
                DeviceIndicator.Fill = Brushes.Gray;
                SetEmptyState(true);
                SetStatus("还没有本地备份。连上手机点【读取微信记录】就能开始。");
                AddLog("Offline mode ready. No usable local snapshot was found.");
                if (!HasSeenGuide()) _ = Dispatcher.BeginInvoke(new Action(ShowGuide));
                return;
            }

            SelectSnapshot(latest);
            DeviceBadge.Text = "已找到电脑上的备份";
            DeviceIndicator.Fill = Brushes.DarkOrange;
            ConversationList.ItemsSource = new[] { "电脑上已有备份 · 点【重新读取当前备份】" };
            SetEmptyState(true);
            SetStatus("电脑上已有备份，点中间区域的按钮或右上角【更多 → 重新读取当前备份】。");
            AddLog($"Offline snapshot ready: {latest.DirectoryPath}");
        }
        catch (Exception ex)
        {
            SnapshotBadge.Text = "备份检查失败";
            DeviceIndicator.Fill = Brushes.Gray;
            SetStatus("本地备份检查失败：" + ex.Message);
            AddLog($"Offline snapshot initialization failed: {ex.Message}");
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void ShowDetails()
    {
        DetailColumn.Width = new GridLength(400);
        DetailPanel.Visibility = Visibility.Visible;
    }

    private void OnHideDetails(object sender, RoutedEventArgs e)
    {
        DetailColumn.Width = new GridLength(0);
        DetailPanel.Visibility = Visibility.Collapsed;
    }

    private bool ComposerSendsOutgoing => SendAsBox?.SelectedIndex != 1;

    private int PendingChangeCount =>
        _workspace is null ? 0 : _diffService.GetDiffs(_workspace).Count;

    /// <summary>
    /// The write-back button shows how many changes are waiting, and stays
    /// disabled when there is nothing to send, so "no reaction" cannot happen
    /// silently.
    /// </summary>
    private void UpdateWriteBackState()
    {
        if (HeaderWriteBackButton is null) return;
        var pending = PendingChangeCount;
        HeaderWriteBackButton.Content = pending > 0 ? $"写回手机（{pending}）" : "写回手机";
        HeaderWriteBackButton.IsEnabled = _workspace is not null && pending > 0;
    }

    private string ComposerSender =>
        ComposerSendsOutgoing ? "" : (SenderNameBox?.Text ?? "").Trim();

    private void OnSendBoxChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SenderNameBox is null) return;
        SenderNameBox.Visibility = ComposerSendsOutgoing ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Puts the current editable copy on disk without bothering the user.</summary>
    private async Task AutoSaveWorkspaceAsync()
    {
        if (_workspace is null) return;
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WXDataStudio", "workspaces");
            await _workspaceService.SaveAsync(_workspace, root);
        }
        catch (Exception ex)
        {
            AddLog($"Auto-save failed: {ex.Message}");
        }
    }

    /// <summary>Types straight into the chat, like WeChat's composer.</summary>
    private async void OnSendMessage(object sender, RoutedEventArgs e)
    {
        var text = ComposerBox.Text.Trim();
        if (text.Length == 0)
        {
            SetStatus("先在上面输入内容，再点【发送】。");
            return;
        }
        if (_workspace is null)
        {
            SetStatus("先在左边点一个聊天，再输入内容。");
            return;
        }

        var message = _workspaceService.AddMessage(
            _workspace, MessageKind.Text, text, null, null,
            ComposerSendsOutgoing, ComposerSender);
        await AutoSaveWorkspaceAsync();
        ComposerBox.Clear();
        MessageFilter.SelectedIndex = 0;
        ApplyMessageFilter();
        SelectBubbleFor(message);
        UpdateWriteBackState();
        SetStatus("已添加一条记录；确认没问题后点右上角【写回手机】。");
        UpdateGuideHint("新记录已经在聊天里了。要继续改别的，点那条消息就行；改完点【写回手机】。");
    }

    private void SelectBubbleFor(WorkspaceMessage message)
    {
        if (MessageList.ItemsSource is IEnumerable<ChatBubble> bubbles)
            MessageList.SelectedItem = bubbles.FirstOrDefault(x => ReferenceEquals(x.Source, message));
        if (MessageList.SelectedItem is not null) MessageList.ScrollIntoView(MessageList.SelectedItem);
    }

    private static ChatBubble? BubbleOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ChatBubble;

    /// <summary>
    /// Rebuilds the editable copy from the database that is now on the phone, so
    /// the screen shows the real phone state and nothing looks "pending".
    /// </summary>
    private async Task AdoptPhoneStateAsync(string verifiedPlaintextPath, ConversationItem conversation)
    {
        try
        {
            var options = new DatabaseOpenOptions { ReadOnly = true };
            var messages = await _dbReader.LoadMessagesAsync(
                verifiedPlaintextPath, conversation.Username, options, 100000);
            if (messages.Count == 0) return;
            _currentMessages = messages;
            _workspace = _workspaceService.Create(
                _latestSnapshotDirectory ?? "", conversation, messages);
            await AutoSaveWorkspaceAsync();
            ApplyMessageFilter();
            UpdateWriteBackState();
            SetStatus($"手机上的这个聊天现在有 {messages.Count:N0} 条记录，和这里显示的一致。");
        }
        catch (Exception ex)
        {
            AddLog($"Adopting phone state failed: {ex.Message}");
        }
    }

    /// <summary>Transfer / red packet / payment entry with amount and note fields.</summary>
    private async void OnAddMoneyMessage(object sender, RoutedEventArgs e)
    {
        if (_workspace is null)
        {
            SetStatus("先在左边点一个聊天，再添加转账或红包。");
            return;
        }
        var kind = ((sender as MenuItem)?.Tag as string) switch
        {
            "redpacket" => MessageKind.RedPacket,
            "payment" => MessageKind.Payment,
            _ => MessageKind.Transfer
        };
        var input = MoneyMessageDialog.Show(this, kind, "", "", "");
        if (input is null) return;
        var content = TransactionMessageTemplate.Build(
            input.Kind, input.Amount, input.Note, input.Status);
        var message = _workspaceService.AddMessage(
            _workspace, input.Kind, content, null, null,
            ComposerSendsOutgoing, ComposerSender);
        await AutoSaveWorkspaceAsync();
        ApplyMessageFilter();
        SelectBubbleFor(message);
        UpdateWriteBackState();
        SetStatus("已添加一条交易类记录。注意：改的只是聊天里显示的文字，点进转账详情的金额由微信服务器决定，不会跟着变。");
        UpdateGuideHint("交易类记录只是聊天里显示的文字；转账详情页来自微信服务器，本地改不了。");
    }

    private async void OnBubbleMoney(object sender, RoutedEventArgs e)
    {
        if (BubbleOf(sender) is not { } bubble ||
            bubble.Source is not WorkspaceMessage message || _workspace is null)
            return;
        var current = TransactionMessageTemplate.Read(message.Kind, message.Content);
        var input = MoneyMessageDialog.Show(this, message.Kind, current.Amount, current.Note, current.Status);
        if (input is null) return;
        // Existing records are edited in place so transaction ids and usernames
        // survive; only a brand-new record is built from scratch.
        var content = TransactionMessageTemplate.Update(
            message.Content, input.Kind, input.Amount, input.Note, input.Status);
        _workspaceService.EditContent(_workspace, message.LocalId, content);
        bubble.Body = ChatExportService.Describe(new WeChatMessage
        {
            Kind = input.Kind,
            Content = content,
            IsOutgoing = message.IsOutgoing
        });
        bubble.IsEditing = false;
        await AutoSaveWorkspaceAsync();
        UpdateWriteBackState();
        SetStatus("聊天里显示的金额已更新。注意：点进转账详情的金额来自微信服务器，不会跟着变。");
        UpdateGuideHint("金额文字已改。转账详情页由微信服务器提供，可能提示失败或显示原金额，这是正常现象。");
    }

    private async void OnBubbleSave(object sender, RoutedEventArgs e)
    {
        if (BubbleOf(sender) is not { } bubble ||
            bubble.Source is not WorkspaceMessage message || _workspace is null)
            return;

        // Time is optional; a wrong entry is reported instead of being ignored.
        long? newTime = null;
        if (!string.IsNullOrWhiteSpace(bubble.EditTime) &&
            !string.Equals(bubble.EditTime.Trim(), ChatBubble.FormatTime(message.CreateTime),
                StringComparison.Ordinal))
        {
            if (!ChatBubble.TryParseTime(bubble.EditTime, out var parsed))
            {
                MessageBox.Show(
                    "时间看不懂，请按下面的样子填：\n\n2026-09-20 14:30\n\n也可以只写 09-20 14:30（默认今年）。",
                    "时间格式", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            newTime = parsed;
        }

        _workspaceService.EditContent(_workspace, message.LocalId, bubble.EditText);
        if (newTime is not null)
        {
            _workspaceService.EditTime(_workspace, message.LocalId, newTime.Value);
            bubble.Time = ChatBubble.FormatTime(newTime.Value)[11..16];
        }
        bubble.Body = bubble.EditText;
        bubble.IsEditing = false;
        FillEditor(message);
        await AutoSaveWorkspaceAsync();
        if (newTime is not null)
        {
            // Re-sorting keeps the conversation in time order after a time edit.
            ApplyMessageFilter();
            SelectBubbleFor(message);
        }
        UpdateWriteBackState();
            SetStatus(newTime is null
            ? "已保存这条修改（还在电脑上，点【写回手机】才会同步到微信）。"
            : "已保存，并按新的时间重新排好顺序。点【写回手机】同步到微信。");
    }

    private void OnBubbleCancel(object sender, RoutedEventArgs e)
    {
        if (BubbleOf(sender) is { } bubble) bubble.IsEditing = false;
    }

    private async void OnBubbleDelete(object sender, RoutedEventArgs e)
    {
        if (BubbleOf(sender) is not { } bubble ||
            bubble.Source is not WorkspaceMessage message || _workspace is null)
            return;
        var confirm = MessageBox.Show(
            "确定删除这条记录吗？\n\n电脑上的副本会立刻删掉，手机要等点【写回手机】之后才会同步删除。",
            "删除记录", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        if (_workspaceService.Delete(_workspace, message.LocalId))
        {
            await AutoSaveWorkspaceAsync();
            ApplyMessageFilter();
            UpdateWriteBackState();
            SetStatus("已删除这条记录。点【写回手机】后手机上也会删除。");
        }
    }

    private async void OnBubbleAttach(object sender, RoutedEventArgs e)
    {
        if (BubbleOf(sender) is not { } bubble ||
            bubble.Source is not WorkspaceMessage message || _workspace is null)
            return;
        var dialog = new OpenFileDialog { Filter = "所有文件|*.*", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = await _workspaceMedia.ImportAsync(_workspace, dialog.FileName);
            _workspaceService.EditAttachment(_workspace, message.LocalId, imported);
            await AutoSaveWorkspaceAsync();
            ApplyMessageFilter();
            SetStatus("附件已换好。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "换附件失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBubbleDetails(object sender, RoutedEventArgs e)
    {
        ShowDetails();
        switch (BubbleOf(sender)?.Source)
        {
            case WeChatMessage message: FillEditor(message); break;
            case WorkspaceMessage workspaceMessage: FillEditor(workspaceMessage); break;
            case MessagePreview preview: FillEditor(preview); break;
        }
    }

    private void SetEmptyState(bool visible) =>
        EmptyState.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnToggleLog(object sender, RoutedEventArgs e)
    {
        var show = LogToggle.IsChecked == true;
        LogExpander.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LogExpander.IsExpanded = show;
    }

    private void SelectSnapshot(SnapshotCatalogItem item)
    {
        _latestSnapshotDirectory = item.DirectoryPath;
        _currentDbPath = null;
        _currentDbOptions = null;
        _credential = null;
        _workspace = null;
        _currentConversation = null;
        _currentConversations = Array.Empty<ConversationItem>();
        _currentMessages = Array.Empty<WeChatMessage>();
        SetAddButtonsEnabled(false);
        SnapshotBadge.Text = $"快照：{item.DisplayName}";
        UpdateGuideHint();
    }

    private void OnShowGuide(object sender, RoutedEventArgs e) => ShowGuide();

    private void ShowGuide()
    {
        new HelpWindow { Owner = this }.ShowDialog();
        MarkGuideSeen();
    }

    private static string GuideMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WXDataStudio", "guide-seen.flag");

    private static bool HasSeenGuide() => File.Exists(GuideMarkerPath);

    private static void MarkGuideSeen()
    {
        try
        {
            var directory = Path.GetDirectoryName(GuideMarkerPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(GuideMarkerPath, DateTimeOffset.Now.ToString("O"));
        }
        catch
        {
            // A marker that cannot be written only means the guide may open again.
        }
    }

    /// <summary>
    /// Keeps the hint strip pointing at the next actionable step instead of
    /// leaving the user to guess which button is enabled in the current state.
    /// </summary>
    private void UpdateGuideHint(string? overrideHint = null)
    {
        if (GuideHint is null) return;
        if (!string.IsNullOrWhiteSpace(overrideHint))
        {
            GuideHint.Text = overrideHint;
            return;
        }

        GuideHint.Text = _workspace is not null
            ? "改内容：点中间一条消息 → 右边修改 → 【保存修改】；补记录用【＋ 添加消息】；完成后点【写回手机】。"
            : _currentConversation is not null
                ? "在中间点一条消息，右边就能修改。"
                : !string.IsNullOrWhiteSpace(_currentDbPath)
                    ? "在左边点一个聊天，就能看到里面的消息。"
                    : !string.IsNullOrWhiteSpace(_latestSnapshotDirectory)
                        ? "点【更多 → 重新读取当前备份】，把聊天读出来。"
                        : _device is not null
                            ? "点【读取微信记录】，把手机上的聊天读到电脑上。"
                            : "第 1 步：连接手机，点右上角【读取微信记录】。";
    }

    private async void OnSelectSnapshot(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 WXDataStudio 快照目录",
            InitialDirectory = Directory.Exists(_snapshotCatalog.RootDirectory)
                ? _snapshotCatalog.RootDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var item = await _snapshotCatalog.InspectDirectoryAsync(dialog.FolderName);
        if (item is null || !item.IsUsable)
        {
            MessageBox.Show("这个目录不是可用的 WXDataStudio 快照，或缺少有效 manifest/EnMicroMsg.db。",
                "快照不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectSnapshot(item);
        ConversationList.ItemsSource = new[] { "已选择本地快照 · 点击“解析快照”" };
        AddLog($"Snapshot selected: {item.DirectoryPath}");
    }

    private async void OnDatabaseDiagnostics(object sender, RoutedEventArgs e)
    {
        _latestSnapshotDirectory ??= FindLatestSnapshotDirectory();
        if (string.IsNullOrWhiteSpace(_latestSnapshotDirectory))
        {
            MessageBox.Show("没有可诊断的本地快照。", "数据库诊断",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var issues = await _integrity.CheckAsync(_latestSnapshotDirectory);
        var db = Path.Combine(_latestSnapshotDirectory, "EnMicroMsg.db");
        var info = await _dbInspector.InspectAsync(db);
        var wal = File.Exists(Path.Combine(_latestSnapshotDirectory, "EnMicroMsg.db-wal"));
        var shm = File.Exists(Path.Combine(_latestSnapshotDirectory, "EnMicroMsg.db-shm"));
        var report = $"快照：{_latestSnapshotDirectory}\n" +
                     $"主库：{info.Size:N0} bytes\n" +
                     $"数据库：{info.Status}\n" +
                     $"WAL：{(wal ? "存在" : "缺失")}\n" +
                     $"SHM：{(shm ? "存在" : "缺失")}\n" +
                     $"完整性：{(issues.Count == 0 ? "通过" : string.Join("；", issues))}";
        MessageBox.Show(report, "数据库诊断", MessageBoxButton.OK,
            issues.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
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
            DeviceIndicator.Fill = Brushes.ForestGreen;
            AddLog($"Device: {_device.Model} ({_device.Codename}), Android {_device.AndroidVersion}, {_device.MiuiVersion}");
            AddLog($"Root: {_device.RootAvailable}; bootloader unlocked: {_device.BootloaderUnlocked}");
            AddLog($"WeChat: {_device.WeChatVersion} ({_device.WeChatVersionCode}); private account: {_device.AccountDirectory}");
            AddLog($"External media account: {_device.ExternalAccountDirectory}");
            AddLog(_device.MatchesLockedBaseline
                ? "Device matches the locked baseline."
                : "WARNING: device does not fully match the locked baseline.");
            UpdateGuideHint();
        }
        catch (Exception ex)
        {
            DeviceBadge.Text = "离线模式 / 手机未连接";
            DeviceIndicator.Fill = Brushes.Gray;
            AddLog($"Device unavailable: {ex.Message}");
            AddLog("Local snapshot analysis remains available while the phone is offline.");
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
            var snapshotItem = await _snapshotCatalog.InspectDirectoryAsync(result.DirectoryPath);
            if (snapshotItem is not null && snapshotItem.IsUsable) SelectSnapshot(snapshotItem);
            AddLog($"Snapshot directory: {result.DirectoryPath}");
            MessageBox.Show("只读数据库快照完成。\n\n" + result.DirectoryPath,
                "快照完成", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateGuideHint();
        }
        catch (Exception ex)
        {
            AddLog($"Snapshot failed: {ex.Message}");
            MessageBox.Show(ex.Message, "快照失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDatabaseKey(object sender, RoutedEventArgs e)
    {
        var input = DatabaseKeyDialog.Show(this, _manualDatabaseKeyIsRawHex);
        if (input is null) return;
        _manualDatabaseKey = string.IsNullOrWhiteSpace(input.Value) ? null : input.Value;
        _manualDatabaseKeyIsRawHex = input.IsRawHex;
        _currentDbOptions = null;
        AddLog(_manualDatabaseKey is null
            ? "Session database key cleared."
            : "Session database key supplied (content not logged)." );
    }

    private async void OnKeyDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            _latestSnapshotDirectory ??= FindLatestSnapshotDirectory();
            var diagnostics = await _legacyKeyCandidates.DiagnoseAsync(_latestSnapshotDirectory);
            var sources = diagnostics.TokenSources.Count == 0
                ? "无"
                : string.Join("、", diagnostics.TokenSources);
            var report =
                $"UIN：{(diagnostics.UinFound ? "已找到" : "未找到")}\n" +
                $"设备标识来源：{sources}\n" +
                $"受限候选数量：{diagnostics.CandidateCount}\n\n" +
                "诊断不会显示 UIN、IMEI、数据库密钥或 Raw Key 的实际值。";
            AddLog($"Key diagnostics: uin={diagnostics.UinFound}; sources={diagnostics.TokenSources.Count}; candidates={diagnostics.CandidateCount}.");
            MessageBox.Show(report, "数据库密钥诊断",
                MessageBoxButton.OK, diagnostics.UinFound ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AddLog($"Key diagnostics unavailable: {ex.Message}");
            MessageBox.Show("当前无法从本地快照或已连接手机读取密钥诊断信息。\n\n" + ex.Message,
                "数据库密钥诊断", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// Returns the database path that all reads must use. Reading a pristine
    /// snapshot would let SQLite rewrite the -shm bookkeeping file, so reads go
    /// through a verified working copy instead.
    /// </summary>
    private async Task<string> OpenSnapshotDatabaseAsync(string snapshotDirectory)
    {
        if (_readSession is null ||
            !string.Equals(_readSession.SnapshotDirectory, snapshotDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            if (_readSession is not null) await _readSession.DisposeAsync();
            _readSession = await SnapshotReadSession.OpenAsync(snapshotDirectory);
            AddLog("Snapshot read session prepared; database reads use a verified working copy.");
        }

        return _readSession.DatabasePath;
    }

    private async Task<DatabaseOpenOptions?> ResolveDatabaseOptionsAsync(
        string db, bool encrypted, string derivedDirectory)
    {
        if (!encrypted)
        {
            _credential = new DatabaseCredentialResolution(true, null, 0, "plain", "Plain SQLite");
            return new DatabaseOpenOptions { ReadOnly = true };
        }

        if (!string.IsNullOrWhiteSpace(_manualDatabaseKey))
        {
            var profiles = new List<DatabaseOpenOptions>();
            foreach (var legacy in new[] { true, false })
            foreach (var compat in legacy ? new[] { 0 } : new[] { 1, 3, 4 })
            {
                profiles.Add(new DatabaseOpenOptions
                {
                    Password = _manualDatabaseKeyIsRawHex ? null : _manualDatabaseKey,
                    RawKeyHex = _manualDatabaseKeyIsRawHex ? _manualDatabaseKey : null,
                    CipherCompatibility = compat == 0 ? 1 : compat,
                    UseLegacyWeChatCipher = legacy,
                    ReadOnly = true
                });
            }
            foreach (var profile in profiles)
            {
                try
                {
                    var schema = await _dbReader.DetectSchemaAsync(db, profile);
                    if (schema.MessageTable is not null || schema.ConversationTable is not null)
                    {
                        AddLog($"Manual session key opened database read-only; profile={(profile.UseLegacyWeChatCipher ? "wechat-legacy" : "compat-" + profile.CipherCompatibility)}.");
                        return profile;
                    }
                }
                catch { }
            }

            try
            {
                var derivedDir = derivedDirectory;
                Directory.CreateDirectory(derivedDir);
                var derived = Path.Combine(derivedDir, "EnMicroMsg.manual.sc1.decrypted.db");
                var matched = _manualDatabaseKeyIsRawHex
                    ? _sc1Decrypt.MatchesRawKey(db, _manualDatabaseKey)
                    : _sc1Decrypt.MatchesPassword(db, _manualDatabaseKey);
                if (matched)
                {
                    if (_manualDatabaseKeyIsRawHex)
                        await _sc1Decrypt.DecryptWithRawKeyAsync(db, _manualDatabaseKey, derived);
                    else
                        await _sc1Decrypt.DecryptWithPasswordAsync(db, _manualDatabaseKey, derived);

                    var schema = await _dbReader.DetectSchemaAsync(
                        derived, new DatabaseOpenOptions { ReadOnly = true });
                    if (schema.MessageTable is not null || schema.ConversationTable is not null)
                    {
                        _currentDbPath = derived;
                        _credential = new DatabaseCredentialResolution(
                            true,
                            _manualDatabaseKeyIsRawHex ? null : _manualDatabaseKey,
                            0,
                            _manualDatabaseKeyIsRawHex ? "manual-raw-sc1" : "manual-pass-sc1",
                            "Manual key opened a derived read-only SC1 plaintext copy.",
                            derived);
                        AddLog("Manual session key matched the SC1 page profile; using a derived read-only plaintext copy.");
                        return new DatabaseOpenOptions { ReadOnly = true };
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"Manual SC1 fallback did not open the database: {ex.Message}");
            }

            throw new InvalidOperationException("本次会话输入的数据库密钥无法以受支持的只读配置打开该快照。");
        }

        AddLog("Encrypted WCDB detected. Resolving bounded local read-only key candidates...");
        _credential = await _credentialResolver.ResolveAsync(db, derivedDirectory: derivedDirectory);
        if (!_credential.Success) return null;
        if (!string.IsNullOrWhiteSpace(_credential.DecryptedPath))
        {
            _currentDbPath = _credential.DecryptedPath;
            AddLog($"Database opened through derived read-only plaintext copy; source={_credential.Source}.");
            return new DatabaseOpenOptions { ReadOnly = true };
        }
        if (string.IsNullOrWhiteSpace(_credential.Password)) return null;
        AddLog($"Database opened read-only using source {_credential.Source}; profile={(_credential.CipherCompatibility == 0 ? "wechat-legacy" : "compat-" + _credential.CipherCompatibility)}.");
        return new DatabaseOpenOptions
        {
            Password = _credential.Password,
            CipherCompatibility = _credential.CipherCompatibility == 0 ? 1 : _credential.CipherCompatibility,
            UseLegacyWeChatCipher = _credential.CipherCompatibility == 0,
            ReadOnly = true
        };
    }

    private async void OnAnalyzeSnapshot(object sender, RoutedEventArgs e)
    {
        try
        {
            _latestSnapshotDirectory ??= FindLatestSnapshotDirectory();
            if (string.IsNullOrWhiteSpace(_latestSnapshotDirectory))
                throw new InvalidOperationException("还没有可读取的备份。请先点【读取微信记录】，或用【更多 → 选择电脑上的备份】。");
            await LoadSnapshotAsync(_latestSnapshotDirectory);
        }
        catch (Exception ex)
        {
            AddLog($"Snapshot analysis failed: {ex.Message}");
            SetStatus("读取失败：" + ex.Message);
            MessageBox.Show(ex.Message, "读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Reads a local backup: verify it, open the database read-only, load the
    /// contact/chat list and show the chat view.
    /// </summary>
    private async Task LoadSnapshotAsync(string snapshotDirectory)
    {
        SetStatus("正在检查备份是否完整…");
        var integrityIssues = await _integrity.CheckAsync(snapshotDirectory);
        if (integrityIssues.Count > 0)
            throw new InvalidDataException("备份文件不完整：" + string.Join("；", integrityIssues));
        var pristineDb = Path.Combine(snapshotDirectory, "EnMicroMsg.db");
        var info = await _dbInspector.InspectAsync(pristineDb);
        AddLog($"DB inspect: {info.Status}; {info.Size:N0} bytes.");

        SetStatus("正在打开聊天数据库…");
        var db = await OpenSnapshotDatabaseAsync(snapshotDirectory);
        _currentDbPath = db;
        var options = await ResolveDatabaseOptionsAsync(
            db, info.AppearsEncrypted, Path.Combine(snapshotDirectory, "derived"));
        if (options is null)
        {
            SetStatus("这个备份里的数据库需要密钥，才能读取。");
            AddLog("Automatic credential resolution did not open this database.");
            MessageBox.Show(
                "这份备份里的聊天数据库是加密的，软件自动尝试没有成功。\n\n" +
                "你可以点【更多 → 手动输入数据库密钥】填入已知口令；密钥只留在本次运行内存里，不会写进日志或文件。",
                "需要数据库密钥", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _currentDbOptions = options;
        var activeDb = _currentDbPath ?? db;
        var schema = await _dbReader.DetectSchemaAsync(activeDb, options);
        AddLog($"Schema: message={schema.MessageTable ?? "(none)"}; conversation={schema.ConversationTable ?? "(fallback)"}; contact={schema.ContactTable ?? "(none)"}.");
        SetStatus("正在读取联系人和聊天列表…");
        var conversations = await _dbReader.LoadConversationsAsync(activeDb, options);
        _currentConversations = conversations;
        ConversationList.ItemsSource = conversations;
        _workspace = null;
        _currentConversation = null;
        _currentMessages = Array.Empty<WeChatMessage>();
        SetAddButtonsEnabled(false);
        SetEmptyState(conversations.Count == 0);
        AddLog($"Loaded {conversations.Count:N0} conversations from the snapshot.");

        var postIssues = await _integrity.CheckAsync(snapshotDirectory);
        AddLog(postIssues.Count == 0
            ? "Snapshot stayed read-only: manifest, sizes and SHA-256 values still match."
            : "WARNING: snapshot files changed during analysis: " + string.Join("; ", postIssues));

        SetStatus(conversations.Count == 0
            ? "这份备份里没有读到聊天，可以换一份备份试试。"
            : $"读到了 {conversations.Count} 个聊天。在左边点一个，就能看内容。");
        UpdateGuideHint(conversations.Count == 0
            ? null
            : "在左边点一个聊天 → 再点中间一条消息，右边就能改。");
    }

    /// <summary>One-click flow: connect the phone, snapshot it and show the chats.</summary>
    private async void OnLoadFromPhone(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("正在连接手机…");
            _device = await _adb.ProbeAsync();
            DeviceBadge.Text = $"{_device.Model} 已连接";
            DeviceIndicator.Fill = Brushes.ForestGreen;
            if (!_device.MatchesLockedBaseline)
            {
                var proceed = MessageBox.Show(
                    $"当前手机是 {_device.Model} / Android {_device.AndroidVersion} / 微信 {_device.WeChatVersion}，\n" +
                    "不在已验证的机型列表里，读取可能失败。要继续吗？",
                    "机型未验证", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (proceed != MessageBoxResult.OK)
                {
                    SetStatus("已取消。");
                    return;
                }
            }

            SetStatus("正在给手机上的记录做只读备份（会先暂停微信）…");
            var snapshot = await _snapshots.CreateDatabaseSnapshotAsync(_device, AddLog);
            _latestSnapshotDirectory = snapshot.DirectoryPath;
            var item = await _snapshotCatalog.InspectDirectoryAsync(snapshot.DirectoryPath);
            if (item is not null && item.IsUsable) SelectSnapshot(item);
            AddLog($"Snapshot directory: {snapshot.DirectoryPath}");

            await LoadSnapshotAsync(snapshot.DirectoryPath);
        }
        catch (Exception ex)
        {
            AddLog($"Load from phone failed: {ex.Message}");
            SetStatus("读取失败：" + ex.Message);
            MessageBox.Show(
                "没能从手机读到记录。\n\n" + ex.Message +
                "\n\n请确认：数据线已连接、手机上允许了 USB 调试、微信版本与机型符合要求。",
                "读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        if (_currentConversation is not null &&
            !string.IsNullOrWhiteSpace(_latestSnapshotDirectory))
        {
            _workspace = _workspaceService.Create(
                _latestSnapshotDirectory, _currentConversation, _currentMessages);
            SetAddButtonsEnabled(true);
            ApplyMessageFilter();
            AddLog($"Workspace created for {_currentConversation.EffectiveName}: {_workspace.Messages.Count} messages.");
            SetStatus($"已打开可编辑副本（{_workspace.Messages.Count} 条）。可以改内容、加消息，然后写回手机。");
            UpdateGuideHint("改内容或点【＋ 添加消息】补记录；改完再点右上角【写回手机】同步到微信。");
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

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_currentConversations.Count == 0) return;
        var q = SearchBox.Text.Trim();
        ConversationList.ItemsSource = string.IsNullOrWhiteSpace(q)
            ? _currentConversations
            : _currentConversations.Where(x =>
                x.EffectiveName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.Remark.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.LastContent.Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private async void OnCreateRollbackPackage(object sender, RoutedEventArgs e)
    {
        _latestSnapshotDirectory ??= FindLatestSnapshotDirectory();
        if (string.IsNullOrWhiteSpace(_latestSnapshotDirectory))
        {
            MessageBox.Show("还没有可打包的完整快照。", "回滚包",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var result = await _rollbackPackages.CreateAsync(_latestSnapshotDirectory);
            AddLog($"Rollback package created: {result.PackagePath}; sha256={result.Sha256}.");
            MessageBox.Show(
                $"回滚包已生成。\n\n{result.PackagePath}\n\nSHA-256:\n{result.Sha256}",
                "回滚包完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AddLog($"Rollback package failed: {ex.Message}");
            MessageBox.Show(ex.Message, "回滚包失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnStage3Acceptance(object sender, RoutedEventArgs e)
    {
        try
        {
            AddLog("Stage3 acceptance started (read-only).");
            _device = await _adb.ProbeAsync();
            if (!_device.MatchesLockedBaseline)
                throw new InvalidOperationException(
                    "连接设备不符合锁定基线：MIX 2S / Android 9 / WeChat 8.0.76 (3141)。");

            var snapshot = await _snapshots.CreateDatabaseSnapshotAsync(_device, AddLog);
            var catalogItem = await _snapshotCatalog.InspectDirectoryAsync(snapshot.DirectoryPath)
                ?? throw new InvalidDataException("新快照目录无法建立目录索引。");
            if (!catalogItem.IsUsable)
                throw new InvalidDataException("新快照不可用。");
            SelectSnapshot(catalogItem);

            var integrity = await _integrity.CheckAsync(snapshot.DirectoryPath);
            if (integrity.Count > 0)
                throw new InvalidDataException(
                    "新快照完整性检查失败：" + string.Join("；", integrity));

            var pristineDb = Path.Combine(snapshot.DirectoryPath, "EnMicroMsg.db");
            var info = await _dbInspector.InspectAsync(pristineDb);
            var db = await OpenSnapshotDatabaseAsync(snapshot.DirectoryPath);
            _currentDbPath = db;
            var options = await ResolveDatabaseOptionsAsync(
                db, info.AppearsEncrypted, Path.Combine(snapshot.DirectoryPath, "derived"));
            if (options is null)
                throw new InvalidOperationException(
                    "数据库仍未能以受支持的只读方式打开。快照已保留，可继续密钥诊断。");

            _currentDbOptions = options;
            var activeDb = _currentDbPath ?? db;
            var schema = await _dbReader.DetectSchemaAsync(activeDb, options);
            if (schema.MessageTable is null && schema.ConversationTable is null)
                throw new InvalidDataException("数据库已打开，但没有识别到兼容的聊天表结构。");

            var conversations = await _dbReader.LoadConversationsAsync(activeDb, options);
            if (conversations.Count == 0)
                throw new InvalidDataException("数据库已打开，但没有读取到会话记录。");

            var sampleMessages = 0;
            var sampleTextMessages = 0;
            foreach (var conversation in conversations.Take(5))
            {
                try
                {
                    var sample = await _dbReader.LoadMessagesAsync(
                        activeDb, conversation.Username, options, 50);
                    sampleMessages += sample.Count;
                    sampleTextMessages += sample.Count(x => x.Kind == MessageKind.Text);
                }
                catch
                {
                    // One unusual conversation should not hide overall adapter acceptance.
                }
            }
            if (sampleMessages == 0)
                throw new InvalidDataException("已读取会话，但抽样会话中没有成功读取到任何消息。");
            if (sampleTextMessages == 0)
                throw new InvalidDataException("已读取消息，但抽样中没有识别到文字消息，阶段三验收不能通过。");

            // Broad census pass: every conversation, bounded page size, aggregate counts only.
            var censusMessages = new List<WeChatMessage>();
            var censusFailures = 0;
            foreach (var conversation in conversations)
            {
                try
                {
                    var loaded = await _dbReader.LoadMessagesAsync(
                        activeDb, conversation.Username, options, 200);
                    censusMessages.AddRange(loaded);
                }
                catch
                {
                    censusFailures++;
                }
            }
            var census = _messageCensus.Build(conversations, censusMessages);
            AddLog(
                $"Message census: conversations={census.ConversationCount}; messages={census.MessageCount}; " +
                $"groups={census.GroupConversationCount}; groupSenders={census.GroupSenderResolvedCount}; " +
                $"unknown={census.UnknownMessageCount}; transactions={census.TransactionCount}; " +
                $"failedConversations={censusFailures}.");
            if (census.UnknownRawTypes.Count > 0)
                AddLog("Unclassified raw types: " + string.Join(',',
                    census.UnknownRawTypes.Take(10).Select(x => $"{x.RawType}x{x.Count}")));
            if (census.MissingKinds.Count > 0)
                AddLog("Message classes absent from this snapshot: " +
                       string.Join(',', census.MissingKinds));

            var postReadIssues = await _integrity.CheckAsync(snapshot.DirectoryPath);
            AddLog(postReadIssues.Count == 0
                ? "Snapshot stayed read-only after parsing: manifest, sizes and SHA-256 values still match."
                : "WARNING: snapshot files changed during parsing: " + string.Join("; ", postReadIssues));

            _currentConversations = conversations;
            ConversationList.ItemsSource = conversations;
            _workspace = null;
            _currentConversation = null;
            _currentMessages = Array.Empty<WeChatMessage>();
            SetAddButtonsEnabled(false);
            DeviceBadge.Text = $"{_device.Model} / WeChat {_device.WeChatVersion}";
            DeviceIndicator.Fill = Brushes.ForestGreen;

            var output = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WXDataStudio", "reports");
            Directory.CreateDirectory(output);
            var reportPath = Path.Combine(output,
                $"stage3-acceptance-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var censusPath = Path.ChangeExtension(reportPath, ".census.json");
            var report =
                "WXDataStudio Stage 3 Read-only Acceptance\n" +
                $"Time: {DateTimeOffset.Now:O}\n" +
                $"Device: {_device.Model} / Android {_device.AndroidVersion} / WeChat {_device.WeChatVersion} ({_device.WeChatVersionCode})\n" +
                $"Snapshot: {snapshot.DirectoryPath}\n" +
                $"Database: {info.Status}\n" +
                $"Credential source: {_credential?.Source ?? "plain"}\n" +
                $"Message table: {schema.MessageTable ?? "(none)"}\n" +
                $"Conversation table: {schema.ConversationTable ?? "(message fallback)"}\n" +
                $"Contact table: {schema.ContactTable ?? "(none)"}\n" +
                $"Conversations: {conversations.Count}\n" +
                $"Sample messages (first 5 conversations, max 50 each): {sampleMessages}\n" +
                $"Sample text messages: {sampleTextMessages}\n" +
                "Census (all conversations, max 200 messages each):\n" +
                census.ToText() +
                $"Census conversation read failures: {censusFailures}\n" +
                $"Snapshot integrity after parsing: " +
                $"{(postReadIssues.Count == 0 ? "unchanged" : string.Join("; ", postReadIssues))}\n" +
                "Phone write-back: DISABLED\n";
            await File.WriteAllTextAsync(reportPath, report);
            await File.WriteAllTextAsync(censusPath, census.ToJson());
            AddLog($"Stage3 acceptance passed: conversations={conversations.Count}; sampleMessages={sampleMessages}.");
            MessageBox.Show(
                $"阶段三只读验收通过。\n\n会话：{conversations.Count}" +
                $"\n抽样消息：{sampleMessages}（文字 {sampleTextMessages}）" +
                $"\n普查消息：{census.MessageCount}" +
                $"\n未知类型：{census.UnknownMessageCount}" +
                $"\n交易类记录：{census.TransactionCount}" +
                $"\n\n报告：\n{reportPath}\n{censusPath}",
                "阶段三验收通过", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AddLog($"Stage3 acceptance blocked: {ex.Message}");
            MessageBox.Show(
                "阶段三验收尚未通过，但不会写回或修改手机数据库。\n\n" + ex.Message,
                "阶段三验收", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnMigrationCheck(object sender, RoutedEventArgs e)
    {
        _latestSnapshotDirectory ??= FindLatestSnapshotDirectory();
        if (string.IsNullOrWhiteSpace(_latestSnapshotDirectory))
        {
            MessageBox.Show("还没有可检查的快照。", "迁移检查",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var integrity = await _integrity.CheckAsync(_latestSnapshotDirectory);
            var report = _migrationReadiness.Evaluate(
                _latestSnapshotDirectory, _currentConversations, _currentMessages, integrity);
            var output = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WXDataStudio", "reports");
            var files = await _migrationExporter.ExportAsync(report, output);
            var summary =
                $"状态：{report.Status}\n" +
                $"会话：{report.ConversationCount}\n" +
                $"当前已加载消息：{report.MessageCount}\n" +
                $"未知类型：{report.UnknownMessageCount}\n" +
                $"交易类记录：{report.TransactionMessageCount}\n" +
                $"警告：{report.WarningCount}\n\n" +
                $"报告：\n{files.TextPath}\n{files.JsonPath}";
            AddLog($"Migration readiness: {report.Status}; warnings={report.WarningCount}; blocked={report.IsBlocked}.");
            MessageBox.Show(summary, "迁移检查（当前已加载数据）",
                MessageBoxButton.OK, report.IsBlocked ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AddLog($"Migration readiness failed: {ex.Message}");
            MessageBox.Show(ex.Message, "迁移检查失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Writes the workspace back to the phone: edited plaintext database,
    /// re-encrypted with the phone's own salt and key, device-side backup,
    /// install, WeChat restart and read-back verification.
    /// </summary>
    private async void OnWriteBackToPhone(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_workspace is null)
            {
                MessageBox.Show("请先在左边点一个聊天，再写回手机。", "写回手机",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _device ??= await _adb.ProbeAsync();
            if (_device is null || !_device.RootAvailable ||
                string.IsNullOrWhiteSpace(_device.MainDatabasePath))
            {
                MessageBox.Show("请先点【设备】检测手机，写回需要 Root 与数据库路径。", "写回手机",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_latestSnapshotDirectory))
            {
                MessageBox.Show("还没有可用快照。", "写回手机",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var encryptedPath = Path.Combine(_latestSnapshotDirectory, "EnMicroMsg.db");
            var derivedDirectory = Path.Combine(_latestSnapshotDirectory, "derived");
            Directory.CreateDirectory(derivedDirectory);
            var plaintextPath = _credential?.DecryptedPath;
            if (string.IsNullOrWhiteSpace(plaintextPath))
            {
                plaintextPath = Path.Combine(derivedDirectory, "EnMicroMsg.restore-source.db");
                var sc1 = new LegacySc1DatabaseService();
                if (_manualDatabaseKeyIsRawHex && !string.IsNullOrWhiteSpace(_manualDatabaseKey))
                    await sc1.DecryptWithRawKeyAsync(encryptedPath, _manualDatabaseKey, plaintextPath);
                else if (!string.IsNullOrWhiteSpace(_credential?.Password))
                    await sc1.DecryptWithPasswordAsync(encryptedPath, _credential!.Password!, plaintextPath);
                else if (!string.IsNullOrWhiteSpace(_manualDatabaseKey))
                    await sc1.DecryptWithPasswordAsync(encryptedPath, _manualDatabaseKey, plaintextPath);
                else
                {
                    MessageBox.Show(
                        "没有可用于重新加密的数据库凭据，请先解析快照或输入数据库密钥。",
                        "写回手机", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var changes = _diffService.GetDiffs(_workspace);
            var transactionChanges = _workspace.Messages.Count(x =>
                x.IsTransaction && (x.IsNew || x.IsDirty || x.IsDeleted));
            var confirmation = MessageBox.Show(
                $"即将把工作副本写回手机：\n\n" +
                $"设备：{_device.Model}\n" +
                $"会话：{_workspace.ConversationName}\n" +
                $"消息：{_workspace.Messages.Count} 条\n" +
                $"改动：{changes.Count} 项\n\n" +
                (transactionChanges > 0
                    ? $"其中 {transactionChanges} 条是交易类记录：聊天里显示的文字会同步，但转账/红包的详情页由微信服务器提供，" +
                      "点进去可能提示失败或仍显示原来的金额。\n\n"
                    : "") +
                "流程：生成加密数据库 → 手机侧备份原库 → 覆盖写入 → 修正权限 → 重启微信 → 回读校验。\n" +
                "过程中微信会被强制停止。手机上的原库会以 .wxds-backup 前缀保留一份，可随时还原。\n\n" +
                "确定继续吗？",
                "写回手机确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.OK) return;

            var request = new PhoneRestoreRequest(
                _device,
                _latestSnapshotDirectory,
                plaintextPath,
                encryptedPath,
                _credential?.Password ?? (_manualDatabaseKeyIsRawHex ? null : _manualDatabaseKey),
                _manualDatabaseKeyIsRawHex ? _manualDatabaseKey : null,
                _workspace);

            SetStatus("正在写回手机：备份原库 → 写入 → 重启微信 → 校验，请勿断开数据线…");
            UpdateGuideHint("正在写回手机：备份原库 → 写入 → 重启微信 → 校验，请勿断开数据线。");
            HeaderWriteBackButton.IsEnabled = false;
            var result = await new PhoneRestoreService(_adb).RestoreAsync(request, AddLog);
            AddLog($"Write-back finished: success={result.Success}; updated={result.UpdatedRows}; " +
                   $"inserted={result.InsertedRows}; verified={result.VerifiedRows}.");
            WriteLogFile("write-back summary: " + string.Join(" | ",
                result.Steps.Select(x => $"{(x.Success ? "OK" : "FAIL")} {x.Name}: {x.Detail}")));
            UpdateGuideHint(result.Success
                ? "已经同步到手机微信了，打开这个聊天就能看到。"
                : "写回没完全成功；手机侧备份和电脑上的回滚包都在，可以重试或还原。");
            SetStatus(result.Success
                ? $"写回完成：改了 {result.UpdatedRows} 条、新增 {result.InsertedRows} 条，手机回读校验 {result.VerifiedRows}/{_workspace.Messages.Count}。"
                : "写回未完全成功，详见弹窗里的步骤说明。");
            if (result.Success && result.VerifiedPlaintextPath is not null && _currentConversation is not null)
                await AdoptPhoneStateAsync(result.VerifiedPlaintextPath, _currentConversation);
            MessageBox.Show(
                (result.Success ? "写回完成并通过回读校验。\n\n" : "写回未完全成功。\n\n") +
                $"更新行：{result.UpdatedRows}\n新增行：{result.InsertedRows}\n" +
                $"回读校验：{result.VerifiedRows}/{_workspace.Messages.Count}\n\n" +
                $"工作目录：\n{result.WorkingDirectory}\n\n" +
                (result.RollbackPackagePath is null ? "" : $"本地回滚包：\n{result.RollbackPackagePath}\n\n") +
                string.Join(Environment.NewLine, result.Steps.Select(x => $"{(x.Success ? "OK" : "!!")} {x.Name}: {x.Detail}")),
                "写回手机", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AddLog($"Write-back failed: {ex.Message}");
            MessageBox.Show(ex.Message, "写回手机失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateWriteBackState();
        }
    }

    private async void OnExportConversation(object sender, RoutedEventArgs e)
    {
        try
        {
            string conversationName;
            IReadOnlyList<WeChatMessage> messages;
            if (_workspace is not null)
            {
                conversationName = _workspace.ConversationName;
                messages = _workspace.Messages.Select(x => new WeChatMessage
                {
                    LocalId = x.LocalId,
                    ServerId = x.ServerId,
                    ConversationId = x.ConversationId,
                    Sender = x.Sender,
                    IsOutgoing = x.IsOutgoing,
                    RawType = x.RawType,
                    RawStatus = x.RawStatus,
                    Sequence = x.Sequence,
                    Kind = x.Kind,
                    Content = x.Content,
                    ImgPath = x.Attachment,
                    CreateTime = x.CreateTime
                }).ToArray();
            }
            else if (_currentConversation is not null && _currentMessages.Count > 0)
            {
                conversationName = _currentConversation.EffectiveName;
                messages = _currentMessages;
            }
            else
            {
                MessageBox.Show("请先在左侧选择一个会话并等消息加载完成，再导出。", "导出会话",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var defaultRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WXDataStudio", "exports");
            Directory.CreateDirectory(defaultRoot);
            var dialog = new OpenFolderDialog
            {
                Title = "选择导出目录（导出内容含个人聊天记录，只保存在本机）",
                InitialDirectory = defaultRoot,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;

            var summary = await _chatExport.ExportAsync(
                dialog.FolderName, conversationName, messages, _latestSnapshotDirectory);
            AddLog($"Conversation exported: messages={summary.MessageCount}; " +
                   $"localAssets={summary.LocalAssetCount}; deviceOnly={summary.DeviceOnlyAttachmentCount}; " +
                   $"missing={summary.MissingAttachmentCount}.");
            UpdateGuideHint("已导出会话：HTML 可直接双击查看，JSON 是完整数据。导出文件含个人聊天内容，请勿上传。");
            MessageBox.Show(
                $"导出完成。\n\n会话：{conversationName}\n消息：{summary.MessageCount}\n" +
                $"本地附件复制：{summary.LocalAssetCount}\n" +
                $"仅手机端附件：{summary.DeviceOnlyAttachmentCount}\n" +
                $"缺失附件：{summary.MissingAttachmentCount}\n\n" +
                $"HTML：\n{summary.HtmlPath}\n\nJSON：\n{summary.JsonPath}\n\n" +
                "导出文件包含个人聊天内容，只保存在本机，请勿上传到公开仓库。",
                "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AddLog($"Conversation export failed: {ex.Message}");
            MessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                var options = _currentDbOptions ?? new DatabaseOpenOptions { ReadOnly = true };
                _currentMessages = await _dbReader.LoadMessagesAsync(
                    _currentDbPath, conversation.Username, options);
                // Editing is the point of this tool, so the editable copy is
                // opened automatically. Earlier edits for this conversation are
                // reloaded so switching chats never loses work.
                var workspaceRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "WXDataStudio", "workspaces");
                var saved = await _workspaceService.FindSavedAsync(
                    workspaceRoot, _latestSnapshotDirectory, conversation.Username);
                if (saved is not null)
                {
                    _workspace = saved;
                    AddLog($"Restored saved edits for {conversation.EffectiveName}.");
                }
                else
                {
                    _workspace = _workspaceService.Create(
                        _latestSnapshotDirectory ?? "", conversation, _currentMessages);
                }
                SetAddButtonsEnabled(true);
                ApplyMessageFilter();
                UpdateWriteBackState();
                AddLog($"Loaded {_currentMessages.Count:N0} messages: {conversation.EffectiveName}");
                SetEmptyState(false);
                var pending = PendingChangeCount;
                SetStatus(pending > 0
                    ? $"这个聊天有 {_currentMessages.Count:N0} 条记录，其中 {pending} 处改动还没写回手机。"
                    : $"这个聊天有 {_currentMessages.Count:N0} 条记录。点任意一条就能就地修改。");
                UpdateGuideHint(pending > 0
                    ? $"有 {pending} 处改动在电脑上，点右上角【写回手机（{pending}）】同步到微信。"
                    : "点中间任意一条消息就能就地改；改完点右上角【写回手机】同步到微信。");
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

    private void OnMessageFilterChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyMessageFilter();

    private void OnMessageSearchChanged(object sender, TextChangedEventArgs e) =>
        ApplyMessageFilter();

    private void ApplyMessageFilter()
    {
        if (MessageList is null) return;
        var index = MessageFilter?.SelectedIndex ?? 0;
        var query = MessageSearchBox?.Text.Trim() ?? "";
        if (_workspace is not null)
        {
            // Always show the conversation in time order, so editing a message's
            // time immediately moves it to the right place.
            MessageList.ItemsSource = BuildWorkspaceBubbles(_workspace.Messages
                .Where(x => MatchesFilter(x.Kind, index))
                .Where(x => MatchesSearch(
                    query, x.Content, x.Sender, x.Attachment, x.LocalId.ToString()))
                .OrderBy(x => x.CreateTime)
                .ThenBy(x => x.LocalId));
            return;
        }
        if (_currentMessages.Count > 0)
            MessageList.ItemsSource = BuildBubbles(_currentMessages
                .Where(x => MatchesFilter(x.Kind, index))
                .Where(x => MatchesSearch(
                    query, x.Content, x.Sender, x.ImgPath, x.LocalId.ToString()))
                .OrderBy(x => x.CreateTime)
                .ThenBy(x => x.LocalId));
    }

    private static bool MatchesSearch(string query, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return values.Any(x => !string.IsNullOrWhiteSpace(x) &&
            x.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Chat rows wrap the original message so selection and editing keep working.</summary>
    private static object? Unwrap(object? item) => item is ChatBubble bubble ? bubble.Source : item;

    private WorkspaceMessage? SelectedWorkspaceMessage =>
        Unwrap(MessageList.SelectedItem) as WorkspaceMessage;

    /// <summary>Shows only the editor blocks that make sense for this message.</summary>
    private void SetEditorGroup(MessageKind kind)
    {
        if (MediaGroup is null) return;
        MediaGroup.Visibility = MessageKindPolicy.HasExternalMedia(kind)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static IReadOnlyList<ChatBubble> BuildBubbles(IEnumerable<WeChatMessage> messages)
    {
        var bubbles = new List<ChatBubble>();
        string? previousDay = null;
        foreach (var message in messages)
        {
            var day = DayLabelOf(message.CreateTime);
            bubbles.Add(ChatBubble.FromMessage(message, day != previousDay, day));
            previousDay = day;
        }
        return bubbles;
    }

    private static IReadOnlyList<ChatBubble> BuildWorkspaceBubbles(IEnumerable<WorkspaceMessage> messages)
    {
        var bubbles = new List<ChatBubble>();
        string? previousDay = null;
        foreach (var message in messages)
        {
            var day = DayLabelOf(message.CreateTime);
            bubbles.Add(ChatBubble.FromWorkspace(message, day != previousDay, day));
            previousDay = day;
        }
        return bubbles;
    }

    private static string DayLabelOf(long unixSeconds)
    {
        if (unixSeconds <= 0) return "时间未知";
        try
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
            var today = DateTime.Today;
            if (local.Date == today) return "今天";
            if (local.Date == today.AddDays(-1)) return "昨天";
            return local.ToString("yyyy-MM-dd dddd", CultureInfo.GetCultureInfo("zh-CN"));
        }
        catch (ArgumentOutOfRangeException)
        {
            return "时间未知";
        }
    }

    private static bool MatchesFilter(MessageKind kind, int index) => index switch
    {
        1 => kind == MessageKind.Text,
        2 => MessageKindPolicy.HasExternalMedia(kind),
        3 => kind is MessageKind.Location or MessageKind.Link or MessageKind.MiniProgram
            or MessageKind.ContactCard or MessageKind.Quote,
        4 => kind is MessageKind.System or MessageKind.Call,
        5 => MessageKindPolicy.IsTransaction(kind),
        _ => true
    };

    private async void OnMessageSelected(object sender, SelectionChangedEventArgs e)
    {
        switch (Unwrap(MessageList.SelectedItem))
        {
            case WeChatMessage message:
                FillEditor(message);
                SetEditorMode(false, message.IsTransaction);
                SetEditorGroup(message.Kind);
                UpdateGuideHint("这是电脑上的备份，只读。点【工作副本】打开可编辑副本后就能改。");
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
                SetEditorMode(edited.CanEdit, edited.IsTransaction);
                SetEditorGroup(edited.Kind);
                // Tapping a message opens its in-place editor, like WeChat.
                if (MessageList.SelectedItem is ChatBubble selected && edited.CanEdit)
                {
                    selected.EditText = edited.Content;
                    selected.EditTime = ChatBubble.FormatTime(edited.CreateTime);
                    selected.IsEditing = true;
                }
                UpdateGuideHint(edited.IsTransaction
                    ? "这条是交易类记录，可以直接改；请只填真实发生过的内容。"
                    : "直接在这个气泡里改，改完点【保存】。");
                break;
            case MessagePreview preview:
                FillEditor(preview);
                SetEditorMode(false, preview.IsTransaction);
                SetEditorGroup(ParseDemoKind(preview.Type));
                UpdateGuideHint("当前显示的是内置示例数据；连接设备并解析快照后会显示真实记录。");
                break;
        }
    }

    private void SetAddButtonsEnabled(bool enabled)
    {
        AddTextButton.IsEnabled = enabled;
        AddImageButton.IsEnabled = enabled;
        AddVideoButton.IsEnabled = enabled;
        AddVoiceButton.IsEnabled = enabled;
        AddFileButton.IsEnabled = enabled;
        AddCardButton.IsEnabled = enabled;
        AddEmojiButton.IsEnabled = enabled;
        AddContactButton.IsEnabled = enabled;
        AddLinkButton.IsEnabled = enabled;
        AddQuoteButton.IsEnabled = enabled;
    }

    private async Task AddWorkspaceMessageAsync(
        MessageKind kind, string content, string? fileFilter = null)
    {
        if (_workspace is null)
        {
            MessageBox.Show("请先选择会话并点击“工作副本”。", "新增消息",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? attachment = null;
        if (!string.IsNullOrWhiteSpace(fileFilter))
        {
            var dialog = new OpenFileDialog { Filter = fileFilter, Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            attachment = await _workspaceMedia.ImportAsync(_workspace, dialog.FileName);
        }

        var message = _workspaceService.AddMessage(
            _workspace, kind, content, attachment, null,
            ComposerSendsOutgoing, ComposerSender);
        await AutoSaveWorkspaceAsync();
        MessageFilter.SelectedIndex = 0;
        ApplyMessageFilter();
        MessageList.Items.Refresh();
        SelectBubbleFor(message);
        AddLog($"Workspace message added: {kind}, id={message.LocalId}.");
        SetStatus("已添加一条记录。点它就能就地改内容。");
        UpdateGuideHint("新记录已加好：点这条气泡就能直接改内容。");
    }

    private async void OnAddText(object sender, RoutedEventArgs e) =>
        await AddWorkspaceMessageAsync(MessageKind.Text, "新文字消息");

    private async void OnAddImage(object sender, RoutedEventArgs e) =>
        await AddWorkspaceMessageAsync(MessageKind.Image, "[图片]",
            "图片|*.jpg;*.jpeg;*.png;*.webp;*.gif|所有文件|*.*");

    private async void OnAddVideo(object sender, RoutedEventArgs e) =>
        await AddWorkspaceMessageAsync(MessageKind.Video, "[视频]",
            "视频|*.mp4;*.mov;*.m4v|所有文件|*.*");

    private async void OnAddVoice(object sender, RoutedEventArgs e) =>
        await AddWorkspaceMessageAsync(MessageKind.Voice, "[语音]",
            "音频|*.amr;*.silk;*.mp3;*.m4a;*.wav|所有文件|*.*");

    private async void OnAddFile(object sender, RoutedEventArgs e) =>
        await AddWorkspaceMessageAsync(MessageKind.File, "[文件]", "所有文件|*.*");

    private async void OnAddEmoji(object sender, RoutedEventArgs e) =>
        await AddWorkspaceMessageAsync(MessageKind.Emoji, "[表情]",
            "表情/图片|*.gif;*.png;*.webp;*.jpg;*.jpeg|所有文件|*.*");

    private async void OnAddCard(object sender, RoutedEventArgs e) =>
        await AddWorkspaceMessageAsync(MessageKind.Location,
            "<msg location x=\"\" y=\"\" label=\"\" poiname=\"\" />");

    private async void OnAddContact(object sender, RoutedEventArgs e) =>
        await AddWorkspaceMessageAsync(MessageKind.ContactCard,
            "<msg username=\"\" nickname=\"\" />");

    private async void OnAddLink(object sender, RoutedEventArgs e) =>
        await AddWorkspaceMessageAsync(MessageKind.Link,
            "<msg><appmsg><type>5</type><title>新链接</title><url></url></appmsg></msg>");

    private async void OnAddQuote(object sender, RoutedEventArgs e) =>
        await AddWorkspaceMessageAsync(MessageKind.Quote,
            "<msg><appmsg><type>57</type><refermsg><displayname></displayname><content></content></refermsg></appmsg></msg>");

    private async void OnReplaceMedia(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || SelectedWorkspaceMessage is not { } msg ||
            !msg.CanEdit || !MessageKindPolicy.HasExternalMedia(msg.Kind))
            return;
        var dialog = new OpenFileDialog { Filter = "所有文件|*.*", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = await _workspaceMedia.ImportAsync(_workspace, dialog.FileName);
            _workspaceService.EditAttachment(_workspace, msg.LocalId, imported);
            PropertyAttachment.Text = imported;
            MediaOriginalPath.Text = imported;
            PreviewMediaButton.IsEnabled = true;
            AddLog($"Workspace media replaced: {msg.LocalId}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "媒体导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPreviewMedia(object sender, RoutedEventArgs e)
    {
        var path = MediaOriginalPath.Text;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show("当前媒体不是本地可预览文件。", "媒体预览",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OnValidateTimeline(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<WeChatMessage> source = _currentMessages;
        if (_workspace is not null)
        {
            source = _workspace.Messages.Select(x => new WeChatMessage
            {
                LocalId = x.LocalId,
                ServerId = x.ServerId,
                ConversationId = x.ConversationId,
                Sender = x.Sender,
                IsOutgoing = x.IsOutgoing,
                RawType = x.RawType,
                RawStatus = x.RawStatus,
                Sequence = x.Sequence,
                Kind = x.Kind,
                Content = x.Content,
                ImgPath = x.Attachment,
                CreateTime = x.CreateTime
            }).ToArray();
        }
        var issues = _timelineValidation.Validate(source);
        MessageBox.Show(
            issues.Count == 0 ? "时间线检查通过。" :
                string.Join(Environment.NewLine, issues.Select(x => $"[{x.Severity}] {x.Message}")),
            "时间线检查",
            MessageBoxButton.OK,
            issues.Any(x => x.Severity == MigrationCheckSeverity.Error)
                ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private async void OnSaveWorkspaceMessage(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || SelectedWorkspaceMessage is not { } msg || !msg.CanEdit)
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
            SetStatus("已保存到电脑上的副本。要同步到手机微信，点右上角【写回手机】。");
            UpdateGuideHint("改好了。点右上角【写回手机】，确认后就会同步回微信。");
        }
        catch (Exception ex)
        {
            AddLog($"Workspace save failed: {ex.Message}");
            MessageBox.Show(ex.Message, "保存工作副本失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnUndoWorkspaceMessage(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || SelectedWorkspaceMessage is not { } msg) return;
        var wasNew = msg.IsNew;
        _workspaceService.Revert(_workspace, msg.LocalId);
        if (wasNew)
        {
            ApplyMessageFilter();
            MessageList.SelectedItem = null;
        }
        else
        {
            MessageList.Items.Refresh();
            FillEditor(msg);
        }
        AddLog($"Workspace message reverted: {msg.LocalId}");
        SetStatus("已把这条恢复成原来的样子。");
        UpdateGuideHint();
    }

    private void OnShiftTimeline(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || SelectedWorkspaceMessage is not { } anchor)
        {
            MessageBox.Show(
                "请先在列表中选中一条消息。批量顺延会作用于这条消息以及它之后的所有消息。",
                "批量顺延", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        const int stepSeconds = 60;
        var (shifted, skipped) = _workspaceService.ShiftTimeline(
            _workspace, anchor.LocalId, stepSeconds);

        MessageList.Items.Refresh();
        FillEditor(anchor);
        AddLog($"Workspace timeline shifted +{stepSeconds}s on {shifted} message(s); read-only skipped={skipped}.");
        MessageBox.Show(
            $"已把选中消息及其之后的 {shifted} 条消息整体推后 {stepSeconds} 秒。" +
            (skipped > 0 ? $"\n跳过了 {skipped} 条只读交易类记录。" : "") +
            "\n\n记得点【保存工作副本】写入磁盘。",
            "批量顺延", MessageBoxButton.OK, MessageBoxImage.Information);
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
        QuoteSenderBox.Text = meta.QuoteSender;
        QuoteContentBox.Text = meta.QuoteContent;
        MiniProgramUserNameBox.Text = meta.MiniProgramUserName;
        MiniProgramPathBox.Text = meta.MiniProgramPath;
        ContactUserNameBox.Text = meta.ContactUserName;
        ContactNickNameBox.Text = meta.ContactNickName;
        TransactionType.Text = meta.TransactionType;
        TransactionAmount.Text = meta.TransactionAmount;
        TransactionStatus.Text = meta.TransactionStatus;
        TransactionMemo.Text = meta.TransactionMemo;
    }

    private void SetEditorMode(bool editable, bool transactionClass)
    {
        ReadOnlyFlag.Visibility = transactionClass ? Visibility.Visible : Visibility.Collapsed;
        var allow = editable;
        PropertyContent.IsReadOnly = !allow;
        PropertyTime.IsReadOnly = !allow;
        PropertyAttachment.IsReadOnly = !allow;
        EditButton.IsEnabled = allow;
        UndoButton.IsEnabled = allow;
        UpdateWriteBackState();
        ReplaceMediaButton.IsEnabled = allow &&
            SelectedWorkspaceMessage is { } workspaceMessage &&
            MessageKindPolicy.HasExternalMedia(workspaceMessage.Kind);
        PreviewMediaButton.IsEnabled = File.Exists(MediaOriginalPath.Text);
        ValidateTimelineButton.IsEnabled = _workspace is not null || _currentMessages.Count > 0;
        ShiftTimelineButton.IsEnabled = allow && SelectedWorkspaceMessage is not null;
    }

    private void AddLog(string text)
    {
        WriteLogFile(text);
        Dispatcher.Invoke(() =>
        {
            _logs.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
            if (_logs.Count > 300) _logs.RemoveAt(0);
            if (_logs.Count > 0) LogList.ScrollIntoView(_logs[^1]);
        });
    }

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "WXDataStudio", "logs");

    /// <summary>
    /// Every action is also written to a log file, so a failed write-back can be
    /// diagnosed afterwards without guessing.
    /// </summary>
    private static void WriteLogFile(string text)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the UI.
        }
    }

    private static string BuildStamp()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "?";
        var exe = Environment.ProcessPath;
        var built = exe is not null && File.Exists(exe)
            ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm")
            : "unknown";
        return $"v{version} 构建于 {built}";
    }

    private static string? FindLatestSnapshotDirectory()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WXDataStudio", "snapshots");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root)
            .Where(x => File.Exists(Path.Combine(x, "manifest.json")))
            .Where(x =>
            {
                var db = Path.Combine(x, "EnMicroMsg.db");
                return File.Exists(db) && new FileInfo(db).Length > 0;
            })
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
        bool IsTransaction)
    {
        public override string ToString() => $"[{Time}] {Sender} · {Type} · {Content}";
    }
}
