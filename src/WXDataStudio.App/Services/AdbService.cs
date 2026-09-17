using System.Diagnostics;
using System.IO;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed class AdbService
{
    private readonly string _adbPath;

    public AdbService(string? adbPath = null)
    {
        _adbPath = adbPath ?? ResolveAdbPath();
    }

    public string AdbPath => _adbPath;

    public async Task<bool> IsAvailableAsync()
    {
        if (_adbPath != "adb.exe" && !File.Exists(_adbPath)) return false;
        var result = await RunAsync("version");
        return result.ExitCode == 0;
    }

    public async Task<DeviceInfo> ProbeAsync()
    {
        var serial = (await RunAsync("get-serialno")).StdOut.Trim();
        if (string.IsNullOrWhiteSpace(serial) || serial == "unknown")
            throw new InvalidOperationException("No authorized Android device detected.");
        var model = await GetPropAsync("ro.product.model");
        var codename = await GetPropAsync("ro.product.device");
        var android = await GetPropAsync("ro.build.version.release");
        var sdk = await GetPropAsync("ro.build.version.sdk");
        var miui = await GetPropAsync("ro.build.version.incremental");
        var abi = await GetPropAsync("ro.product.cpu.abi");
        var root = await HasRootAsync();
        var boot = await GetPropAsync("ro.boot.verifiedbootstate");

        var dump = await ShellAsync("dumpsys package com.tencent.mm");
        var versionName = ExtractValue(dump.StdOut, "versionName=");
        var versionCode = ExtractVersionCode(dump.StdOut);
        var account = root ? await FindAccountDirectoryAsync() : "";
        var external = await FindExternalAccountDirectoryAsync();
        var db = string.IsNullOrWhiteSpace(account)
            ? ""
            : $"/data/user/0/com.tencent.mm/MicroMsg/{account}/EnMicroMsg.db";

        return new DeviceInfo
        {
            Serial = serial,
            Model = model,
            Codename = codename,
            AndroidVersion = android,
            Sdk = sdk,
            MiuiVersion = miui,
            Abi = abi,
            RootAvailable = root,
            BootloaderUnlocked = boot.Equals("orange", StringComparison.OrdinalIgnoreCase),
            WeChatVersion = versionName,
            WeChatVersionCode = versionCode,
            AccountDirectory = account,
            ExternalAccountDirectory = external,
            MainDatabasePath = db
        };
    }

    public Task<CommandResult> ForceStopWeChatAsync() =>
        ShellAsync("am force-stop com.tencent.mm");

    public Task<CommandResult> LaunchWeChatAsync() =>
        ShellAsync("monkey -p com.tencent.mm -c android.intent.category.LAUNCHER 1");

    public Task<CommandResult> PullAsync(string remotePath, string localPath) =>
        RunAsync($"pull \"{remotePath}\" \"{localPath}\"");

    public Task<CommandResult> ShellAsync(string command) =>
        RunAsync($"shell {command}");

    public async Task<CommandResult> RootPullFileAsync(string remotePath, string localPath)
    {
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("exec-out");
        psi.ArgumentList.Add("su");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"cat '{remotePath.Replace("'", "'\\''", StringComparison.Ordinal)}'");
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Unable to start adb exec-out.");
        await using (var file = File.Create(localPath))
            await process.StandardOutput.BaseStream.CopyToAsync(file);
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 && File.Exists(localPath)) File.Delete(localPath);
        return new CommandResult(process.ExitCode, "", stderr);
    }

    public Task<CommandResult> RootShellAsync(string command)
    {
        var escaped = command.Replace("\"", "\\\"");
        return RunAsync($"shell su -c \"{escaped}\"");
    }

    private async Task<string> GetPropAsync(string name)
    {
        var result = await ShellAsync($"getprop {name}");
        return result.StdOut.Trim();
    }

    private async Task<bool> HasRootAsync()
    {
        var result = await RunAsync("shell su -c id");
        return result.ExitCode == 0 && result.StdOut.Contains("uid=0(root)");
    }

    private async Task<string> FindAccountDirectoryAsync()
    {
        const string root = "/data/user/0/com.tencent.mm/MicroMsg";
        var result = await RootShellAsync(
            $"find {root} -maxdepth 2 -name EnMicroMsg.db 2>/dev/null | head -n 1");
        var path = result.StdOut.Trim();
        if (string.IsNullOrWhiteSpace(path)) return "";

        var slash = path.LastIndexOf('/');
        if (slash <= 0) return "";
        var parent = path[..slash];
        var parentSlash = parent.LastIndexOf('/');
        return parentSlash < 0 ? parent : parent[(parentSlash + 1)..];
    }
    private async Task<string> FindExternalAccountDirectoryAsync()
    {
        const string root = "/sdcard/Android/data/com.tencent.mm/MicroMsg";
        var result = await ShellAsync(
            $"find {root} -maxdepth 2 -type d -name image2 2>/dev/null | head -n 1");
        var path = result.StdOut.Trim();
        if (string.IsNullOrWhiteSpace(path)) return "";
        var parent = path[..path.LastIndexOf('/')];
        var slash = parent.LastIndexOf('/');
        return slash >= 0 ? parent[(slash + 1)..] : parent;
    }

    private static string ExtractValue(string text, string marker)
    {
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0) return line[(index + marker.Length)..].Trim();
        }
        return "";
    }

    private static string ExtractVersionCode(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf("versionCode=", StringComparison.Ordinal);
            if (index < 0) continue;
            var value = line[(index + "versionCode=".Length)..].Trim();
            return value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }
        return "";
    }

    private async Task<CommandResult> RunAsync(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Unable to start adb.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string ResolveAdbPath()
    {
        var appLocal = Path.Combine(
            AppContext.BaseDirectory, "tools", "android", "platform-tools", "adb.exe");
        if (File.Exists(appLocal)) return appLocal;

        var devLocal = @"C:\Users\Administrator\Documents\WXDataStudio\tools\android\platform-tools\adb.exe";
        if (File.Exists(devLocal)) return devLocal;
        return "adb.exe";
    }
}

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}
