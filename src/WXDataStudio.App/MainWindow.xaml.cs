using System.Collections.ObjectModel;
using System.Diagnostics;
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
        AddLog("WXDataStudio v0.3 started.");
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
                DeviceBadge.Text = "离线模式 / 手机未连接";
                SnapshotBadge.Text = "暂无可用本地快照";
                DeviceIndicator.Fill = Brushes.Gray;
                AddLog("Offline mode ready. No usable local snapshot was found.");
                return;
            }

            SelectSnapshot(latest);
            DeviceBadge.Text = "离线模式 / 可解析本地快照";
            DeviceIndicator.Fill = Brushes.DarkOrange;
            ConversationList.ItemsSource = new[] { "本地快照已就绪 · 点击“解析快照”" };
            AddLog($"Offline snapshot ready: {latest.DirectoryPath}");
        }
        catch (Exception ex)
        {
            SnapshotBadge.Text = "本地快照检查失败";
            DeviceIndicator.Fill = Brushes.Gray;
            AddLog($"Offline snapshot initialization failed: {ex.Message}");
        }
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

    private async Task<DatabaseOpenOptions?> ResolveDatabaseOptionsAsync(string db, bool encrypted)
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
                var derivedDir = Path.Combine(Path.GetDirectoryName(db) ?? ".", "derived");
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
        _credential = await _credentialResolver.ResolveAsync(db);
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
                throw new InvalidOperationException("还没有可解析的快照，请先创建快照。");
            var integrityIssues = await _integrity.CheckAsync(_latestSnapshotDirectory);
            if (integrityIssues.Count > 0)
                throw new InvalidDataException("快照完整性检查未通过：" + string.Join("；", integrityIssues));
            var db = Path.Combine(_latestSnapshotDirectory, "EnMicroMsg.db");
            var info = await _dbInspector.InspectAsync(db);
            _currentDbPath = db;
            AddLog($"DB inspect: {info.Status}; {info.Size:N0} bytes.");
            AddLog($"DB header: {info.HeaderHex[..Math.Min(32, info.HeaderHex.Length)]}...");

            var options = await ResolveDatabaseOptionsAsync(db, info.AppearsEncrypted);
            if (options is null)
            {
                AddLog("Automatic credential resolution did not open this WCDB snapshot.");
                MessageBox.Show("已确认这是加密 WCDB。自动只读解析未能打开数据库。\n你可以点击“数据库密钥”输入已知的文本口令或 Raw Hex；密钥只保存在本次进程内存中。\n原库没有被修改。",
                    "数据库仍为加密状态", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _currentDbOptions = options;
            var activeDb = _currentDbPath ?? db;
            var schema = await _dbReader.DetectSchemaAsync(activeDb, options);
            AddLog($"Schema: message={schema.MessageTable ?? "(none)"}; conversation={schema.ConversationTable ?? "(fallback)"}; contact={schema.ContactTable ?? "(none)"}.");
            var conversations = await _dbReader.LoadConversationsAsync(activeDb, options);
            _currentConversations = conversations;
            ConversationList.ItemsSource = conversations;
            _workspace = null;
            _currentConversation = null;
            _currentMessages = Array.Empty<WeChatMessage>();
            SetAddButtonsEnabled(false);
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
        if (_currentConversation is not null &&
            !string.IsNullOrWhiteSpace(_latestSnapshotDirectory))
        {
            _workspace = _workspaceService.Create(
                _latestSnapshotDirectory, _currentConversation, _currentMessages);
            SetAddButtonsEnabled(true);
            ApplyMessageFilter();
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

            var db = Path.Combine(snapshot.DirectoryPath, "EnMicroMsg.db");
            var info = await _dbInspector.InspectAsync(db);
            _currentDbPath = db;
            var options = await ResolveDatabaseOptionsAsync(db, info.AppearsEncrypted);
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
                "Phone write-back: DISABLED\n";
            await File.WriteAllTextAsync(reportPath, report);
            AddLog($"Stage3 acceptance passed: conversations={conversations.Count}; sampleMessages={sampleMessages}.");
            MessageBox.Show(
                $"阶段三只读验收通过。\n\n会话：{conversations.Count}\n抽样消息：{sampleMessages}\n抽样文字：{sampleTextMessages}\n\n报告：\n{reportPath}",
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
                $"交易/红包/收付款只读：{report.SensitiveMessageCount}\n" +
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
                _workspace = null;
                SetAddButtonsEnabled(false);
                ApplyMessageFilter();
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

    private void OnMessageFilterChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyMessageFilter();

    private void OnMessageSearchChanged(object sender, TextChangedEventArgs e) =>
        ApplyMessageFilter();

    private void ApplyMessageFilter()
    {
        var index = MessageFilter?.SelectedIndex ?? 0;
        var query = MessageSearchBox?.Text.Trim() ?? "";
        if (_workspace is not null)
        {
            MessageList.ItemsSource = _workspace.Messages
                .Where(x => MatchesFilter(x.Kind, index))
                .Where(x => MatchesSearch(
                    query, x.Content, x.Sender, x.Attachment, x.LocalId.ToString()))
                .ToArray();
            return;
        }
        if (_currentMessages.Count > 0)
            MessageList.ItemsSource = _currentMessages
                .Where(x => MatchesFilter(x.Kind, index))
                .Where(x => MatchesSearch(
                    query, x.Content, x.Sender, x.ImgPath, x.LocalId.ToString()))
                .ToArray();
    }

    private static bool MatchesSearch(string query, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return values.Any(x => !string.IsNullOrWhiteSpace(x) &&
            x.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesFilter(MessageKind kind, int index) => index switch
    {
        1 => kind == MessageKind.Text,
        2 => MessageKindPolicy.HasExternalMedia(kind),
        3 => kind is MessageKind.Location or MessageKind.Link or MessageKind.MiniProgram
            or MessageKind.ContactCard or MessageKind.Quote,
        4 => kind is MessageKind.System or MessageKind.Call,
        5 => MessageKindPolicy.IsSensitive(kind),
        _ => true
    };

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

        var message = _workspaceService.AddMessage(_workspace, kind, content, attachment);
        MessageFilter.SelectedIndex = 0;
        ApplyMessageFilter();
        MessageList.Items.Refresh();
        MessageList.SelectedItem = message;
        MessageList.ScrollIntoView(message);
        AddLog($"Workspace message added: {kind}, id={message.LocalId}.");
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
        if (_workspace is null || MessageList.SelectedItem is not WorkspaceMessage msg ||
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

    private void SetEditorMode(bool editable, bool sensitive)
    {
        ReadOnlyFlag.Visibility = sensitive ? Visibility.Visible : Visibility.Collapsed;
        var allow = editable && !sensitive;
        PropertyContent.IsReadOnly = !allow;
        PropertyTime.IsReadOnly = !allow;
        PropertyAttachment.IsReadOnly = !allow;
        EditButton.IsEnabled = allow;
        UndoButton.IsEnabled = allow;
        ReplaceMediaButton.IsEnabled = allow &&
            MessageList.SelectedItem is WorkspaceMessage workspaceMessage &&
            MessageKindPolicy.HasExternalMedia(workspaceMessage.Kind);
        PreviewMediaButton.IsEnabled = File.Exists(MediaOriginalPath.Text);
        ValidateTimelineButton.IsEnabled = _workspace is not null || _currentMessages.Count > 0;
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
        bool Sensitive)
    {
        public override string ToString() => $"[{Time}] {Sender} · {Type} · {Content}";
    }
}