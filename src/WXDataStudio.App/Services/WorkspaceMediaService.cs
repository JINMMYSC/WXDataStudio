using System.IO;
using System.Security.Cryptography;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed class WorkspaceMediaService
{
    private readonly string _root;

    public WorkspaceMediaService(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WXDataStudio", "workspace-media");
    }

    public async Task<string> ImportAsync(WorkspaceDocument workspace, string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Selected media file was not found.", sourcePath);

        var workspaceDir = Path.Combine(_root, workspace.WorkspaceId);
        Directory.CreateDirectory(workspaceDir);
        var bytes = await File.ReadAllBytesAsync(sourcePath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..12];
        var safeName = Path.GetFileName(sourcePath);
        var destination = Path.Combine(workspaceDir, $"{hash}-{safeName}");
        if (!File.Exists(destination))
            await File.WriteAllBytesAsync(destination, bytes);
        return destination;
    }
}
