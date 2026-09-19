using System.IO;
using System.Security.Cryptography;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed record PhoneRestoreRequest(
    DeviceInfo Device,
    string SnapshotDirectory,
    string PlaintextDatabasePath,
    string SourceEncryptedDatabasePath,
    string? Password,
    string? RawKeyHex,
    WorkspaceDocument Workspace);

public sealed record PhoneRestoreStep(string Name, bool Success, string Detail);

public sealed record PhoneRestoreResult(
    bool Success,
    string Message,
    IReadOnlyList<PhoneRestoreStep> Steps,
    string WorkingDirectory,
    string? RestorePackagePath,
    string? RollbackPackagePath,
    string? DeviceBackupPath,
    string? VerifiedPlaintextPath,
    int UpdatedRows,
    int InsertedRows,
    int VerifiedRows);

/// <summary>
/// Writes workspace changes into a copy of the plaintext database, re-encrypts it
/// with the phone's own salt and key, backs up what is on the device, pushes the
/// new database, fixes ownership/permissions/context, restarts WeChat and reads
/// the result back for verification.
///
/// Nothing is overwritten on the phone before the device-side backup exists.
/// </summary>
public sealed class PhoneRestoreService
{
    private readonly AdbService _adb;
    private readonly LegacySc1DatabaseService _sc1 = new();
    private readonly WorkspaceDatabaseWriter _writer = new();
    private readonly RollbackPackageService _rollback = new();
    private readonly WeChatDatabaseReader _reader = new();

    public PhoneRestoreService(AdbService adb)
    {
        _adb = adb;
    }

    public async Task<PhoneRestoreResult> RestoreAsync(
        PhoneRestoreRequest request,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<PhoneRestoreStep>();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var working = Path.Combine(request.SnapshotDirectory, "derived", "restore-" + stamp);
        string? deviceBackupPath = null;
        Directory.CreateDirectory(working);

        void Step(string name, bool success, string detail)
        {
            steps.Add(new PhoneRestoreStep(name, success, detail));
            log?.Invoke($"{name}: {detail}");
        }

        try
        {
            var editedPlain = Path.Combine(working, "EnMicroMsg.edited.db");
            File.Copy(request.PlaintextDatabasePath, editedPlain, overwrite: true);
            Step("prepare", true, "Working copy of the plaintext database created.");

            var write = await _writer.ApplyAsync(editedPlain, request.Workspace, cancellationToken);
            Step("apply-workspace", true,
                $"updated={write.Updated}, inserted={write.Inserted}, deleted={write.Deleted}, " +
                $"warnings={write.Warnings.Count}");

            var plainIntegrity = await WorkspaceDatabaseWriter.IntegrityCheckAsync(
                editedPlain, cancellationToken);
            if (!plainIntegrity.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                Step("integrity", false, plainIntegrity);
                return Failure("Edited database failed its integrity check.", steps, working, deviceBackupPath);
            }
            Step("integrity", true, "ok");

            var restorePackage = Path.Combine(working, "EnMicroMsg.restore.db");
            if (!string.IsNullOrWhiteSpace(request.RawKeyHex))
            {
                await _sc1.EncryptWithRawKeyAsync(
                    editedPlain, request.SourceEncryptedDatabasePath, request.RawKeyHex, restorePackage);
            }
            else if (!string.IsNullOrWhiteSpace(request.Password))
            {
                await _sc1.EncryptWithPasswordAsync(
                    editedPlain, request.SourceEncryptedDatabasePath, request.Password, restorePackage);
            }
            else
            {
                return Failure("No database credential is available for re-encryption.", steps, working, deviceBackupPath);
            }
            Step("encrypt", true, $"Restore image written ({new FileInfo(restorePackage).Length:N0} bytes).");

            // Round-trip proof: decrypting the restore image must reproduce the
            // edited plaintext byte for byte, otherwise the phone would see
            // different data than the workspace.
            var roundTrip = Path.Combine(working, "EnMicroMsg.roundtrip.db");
            if (!string.IsNullOrWhiteSpace(request.RawKeyHex))
                await _sc1.DecryptWithRawKeyAsync(restorePackage, request.RawKeyHex, roundTrip, foldWal: false);
            else
                await _sc1.DecryptWithPasswordAsync(restorePackage, request.Password!, roundTrip, foldWal: false);
            var identical = await LegacySc1DatabaseService.PayloadsMatchAsync(
                editedPlain, roundTrip, cancellationToken);
            Step("roundtrip", identical,
                identical ? "Re-encrypted image decrypts back to the edited database." :
                "Re-encrypted image does not match the edited database.");
            if (!identical)
            return Failure("Round-trip verification failed; nothing was written to the phone.",
                steps, working, deviceBackupPath);

            var rollbackPath = (await _rollback.CreateAsync(
                request.SnapshotDirectory,
                Path.Combine(working, "rollback"))).PackagePath;
            Step("rollback-package", true, "Local rollback package created.");

            var databasePath = request.Device.MainDatabasePath;
            var deviceWork = "/data/local/tmp/wxds-restore.db";
            var stop = await _adb.ForceStopWeChatAsync();
            Step("stop-wechat", stop.Success || stop.StdOut.Length == 0,
                "WeChat stopped so the database file is not in use.");
            var push = await _adb.PushAsync(restorePackage, deviceWork);
            if (!push.Success)
            {
                Step("upload", false, Detail(push));
                return Failure("Could not upload the restore image to the phone.", steps, working, deviceBackupPath);
            }
            Step("upload", true, "Restore image uploaded to the phone.");

            var backupResult = await _adb.RootShellAsync(
                $"cd {Quote(RemoteDirectory(databasePath))} && " +
                $"cp -a {Quote(RemoteFileName(databasePath))} " +
                $"{Quote(RemoteFileName(databasePath) + ".wxds-backup-" + stamp)} && " +
                $"ls -l {Quote(RemoteFileName(databasePath) + ".wxds-backup-" + stamp)}");
            if (!backupResult.Success)
            {
                Step("device-backup", false, Detail(backupResult));
                return Failure("Device-side backup failed; nothing was replaced.", steps, working, deviceBackupPath);
            }
            deviceBackupPath = databasePath + ".wxds-backup-" + stamp;
            Step("device-backup", true, "Original database copied aside on the phone.");

            var owner = await ReadOwnerAsync(databasePath);
            var install = await _adb.RootShellAsync(string.Join(" && ",
                $"cp {Quote(deviceWork)} {Quote(databasePath)}",
                $"chown {owner} {Quote(databasePath)}",
                $"chmod 600 {Quote(databasePath)}",
                $"rm -f {Quote(databasePath + "-wal")} {Quote(databasePath + "-shm")}",
                $"restorecon {Quote(databasePath)} 2>/dev/null || true",
                $"sha256sum {Quote(databasePath)}"));
            if (!install.Success)
                return Failure("Installing the restored database failed.", steps, working, deviceBackupPath);
            Step("install", true, "Restored database installed and permissions restored.");

            var launch = await _adb.LaunchWeChatAsync();
            Step("launch", launch.Success, launch.Success
                ? "WeChat started."
                : "WeChat did not report a successful start; check the phone.");

            await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var verifyDirectory = Path.Combine(working, "verify");
            Directory.CreateDirectory(verifyDirectory);
            var pulledPath = Path.Combine(verifyDirectory, "EnMicroMsg.db");
            var pull = await _adb.RootPullFileAsync(databasePath, pulledPath);
            if (!pull.Success)
                return Failure("Could not read the restored database back for verification.",
                    steps, working, deviceBackupPath);

            // The phone stores the database encrypted again: decrypt the pulled
            // copy before checking it, exactly like a normal snapshot read.
            var verifyPlain = Path.Combine(verifyDirectory, "EnMicroMsg.pulled.plain.db");
            if (!string.IsNullOrWhiteSpace(request.RawKeyHex))
                await _sc1.DecryptWithRawKeyAsync(
                    pulledPath, request.RawKeyHex, verifyPlain, foldWal: false);
            else
                await _sc1.DecryptWithPasswordAsync(
                    pulledPath, request.Password!, verifyPlain, foldWal: false);

            var verifyIntegrity = await WorkspaceDatabaseWriter.IntegrityCheckAsync(
                verifyPlain, cancellationToken);
            Step("verify-integrity", verifyIntegrity.Equals("ok", StringComparison.OrdinalIgnoreCase),
                verifyIntegrity);
            if (!verifyIntegrity.Equals("ok", StringComparison.OrdinalIgnoreCase))
                return Failure("The database on the phone does not pass its integrity check.",
                    steps, working, deviceBackupPath);

            var (verified, failures) = await VerifyWorkspaceRowsAsync(
                verifyPlain, request.Workspace, cancellationToken);
            Step("verify-content", verified == request.Workspace.Messages.Count,
                $"verifiedRows={verified}/{request.Workspace.Messages.Count}" +
                (failures.Count == 0 ? "" : "; not kept: " + string.Join(',', failures.Take(6))));

            var success = verified == request.Workspace.Messages.Count;
            return new PhoneRestoreResult(
                success,
                success
                    ? "Restore completed and verified on the phone."
                    : "Restore completed but only part of the workspace could be verified.",
                steps, working, restorePackage, rollbackPath,
                deviceBackupPath,
                verifyPlain,
                write.Updated, write.Inserted, verified);
        }
        catch (Exception ex)
        {
            Step("error", false, ex.GetType().Name + ": " + ex.Message);
            return Failure(ex.Message, steps, working);
        }
    }

    private async Task<(int Verified, IReadOnlyList<string> Failures)> VerifyWorkspaceRowsAsync(
        string plaintextDatabasePath,
        WorkspaceDocument workspace,
        CancellationToken cancellationToken)
    {
        var options = new DatabaseOpenOptions { ReadOnly = true };
        var messages = await _reader.LoadMessagesAsync(
            plaintextDatabasePath, workspace.ConversationId, options, 100000);
        var byId = messages.ToDictionary(x => x.LocalId);
        var verified = 0;
        var failures = new List<string>();
        foreach (var expected in workspace.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expected.IsNew)
            {
                if (messages.Any(x => x.Content == expected.Content)) verified++;
                else failures.Add($"{expected.Kind}#new");
                continue;
            }
            if (expected.IsDeleted)
            {
                // A deletion is verified when the row is gone from the phone.
                if (messages.All(x => x.LocalId != expected.LocalId)) verified++;
                else failures.Add($"{expected.Kind}#{expected.LocalId}(delete)");
                continue;
            }
            if (byId.TryGetValue(expected.LocalId, out var actual) &&
                actual.Content == expected.Content)
                verified++;
            else
                failures.Add($"{expected.Kind}#{expected.LocalId}");
        }
        return (verified, failures);
    }

    private async Task<string> ReadOwnerAsync(string databasePath)
    {
        var stat = await _adb.RootShellAsync($"stat -c '%u:%g' {Quote(databasePath)}");
        var owner = stat.StdOut.Trim();
        return string.IsNullOrWhiteSpace(owner) ? "1000:1000" : owner;
    }

    private static async Task<bool> FilesEqualAsync(
        string left, string right, CancellationToken cancellationToken)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        return await HashAsync(left, cancellationToken) == await HashAsync(right, cancellationToken);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string Quote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Device paths must be split on '/' only: Windows path helpers rewrite the
    /// separators and would produce an unusable Android path.
    /// </summary>
    private static string RemoteDirectory(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static string RemoteFileName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string Detail(CommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        text = (text ?? "").Replace('\n', ' ').Trim();
        if (text.Length > 240) text = text[..240];
        return $"exit={result.ExitCode}; {text}";
    }

    private static PhoneRestoreResult Failure(
        string message,
        IReadOnlyList<PhoneRestoreStep> steps,
        string working,
        string? deviceBackupPath = null) =>
        new(false, message, steps, working, null, null, deviceBackupPath, null, 0, 0, 0);
}
