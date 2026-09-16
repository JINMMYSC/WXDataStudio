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
    private DeviceInfo? _device;
    private string? _latestSnapshotDirectory;

    public MainWindow()
    {
        InitializeComponent();
        _snapshots = new SnapshotService(_adb);
        LogList.ItemsSource = _logs;
        ConversationList.ItemsSource = new[] { "Device & adapter", "Demo chat", "Demo group" };
        _demoMessages = BuildDemoMessages();
        AddLog("WXDataStudio v0.1 started.");
        AddLog("Locked baseline: MIX 2S / Android 9 / MIUI 10.3.5 / WeChat 8.0.76 (3141).");
        AddLog("Write-back is disabled. Snapshot and integrity work is read-only.");
    }
    private async void OnRefreshDevice(object sender, RoutedEventArgs e)
    {
        try
        {
            DeviceBadge.Text = "Detecting device...";
            AddLog($"ADB: {_adb.AdbPath}");
            if (!await _adb.IsAvailableAsync())
                throw new InvalidOperationException("ADB is not available.");

            _device = await _adb.ProbeAsync();
            DeviceBadge.Text = $"{_device.Model} / WeChat {_device.WeChatVersion}";
            AddLog($"Device: {_device.Model} ({_device.Codename}), Android {_device.AndroidVersion}, {_device.MiuiVersion}");
            AddLog($"Root: {_device.RootAvailable}; bootloader unlocked: {_device.BootloaderUnlocked}");
            AddLog($"WeChat: {_device.WeChatVersion} ({_device.WeChatVersionCode}); account: {_device.AccountDirectory}");
            AddLog(_device.MatchesLockedBaseline
                ? "Device matches the locked baseline."
                : "WARNING: device does not fully match the locked baseline.");
        }
        catch (Exception ex)
        {
            DeviceBadge.Text = "Device detection failed";
            AddLog($"Device detection failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Device detection failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnCreateSnapshot(object sender, RoutedEventArgs e)
    {
        try
        {
            _device ??= await _adb.ProbeAsync();
            if (!_device.MatchesLockedBaseline)
                throw new InvalidOperationException(
                    "The connected device does not match the validated baseline. Snapshot blocked.");

            var result = await _snapshots.CreateDatabaseSnapshotAsync(_device, AddLog);
            _latestSnapshotDirectory = result.DirectoryPath;
            AddLog($"Snapshot directory: {result.DirectoryPath}");
            MessageBox.Show("Read-only database snapshot completed.\n\n" + result.DirectoryPath,
                "Snapshot complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AddLog($"Snapshot failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Snapshot failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        _latestSnapshotDirectory ??= FindLatestSnapshotDirectory();
        if (string.IsNullOrWhiteSpace(_latestSnapshotDirectory))
        {
            MessageBox.Show("No snapshot is available yet.", "Workspace",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", _latestSnapshotDirectory)
        {
            UseShellExecute = true
        });
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
                MessageBox.Show("Snapshot integrity check passed.", "Integrity check",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var issue in issues) AddLog("Integrity issue: " + issue);
            MessageBox.Show(string.Join(Environment.NewLine, issues),
                "Integrity check issues", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AddLog($"Integrity check failed: {ex.Message}");
        }
    }

    private void OnConversationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is not string name) return;
        ConversationTitle.Text = name;
        MessageList.ItemsSource = _demoMessages.TryGetValue(name, out var messages)
            ? messages
            : Array.Empty<MessagePreview>();
    }

    private void OnMessageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItem is not MessagePreview message) return;

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

    private static Dictionary<string, List<MessagePreview>> BuildDemoMessages() => new()
    {
        ["Device & adapter"] =
        [
            new("System", "Adapter", "wechat-android-8.0.76-3141 locked; device probe and read-only snapshot are wired.", "Now", null, false)
        ],
        ["Demo chat"] =
        [
            new("Text", "Me", "Sample workspace text message.", "2026-09-17 03:45:00", null, false),
            new("Image", "Peer", "Sample image message", "2026-09-17 03:46:10", "sample.jpg", false),
            new("Transfer", "System", "Transaction-class records are parsed read-only and are not written back as real proof.", "2026-09-17 03:47:20", null, true)
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
