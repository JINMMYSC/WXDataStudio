using Microsoft.Data.Sqlite;
using WXDataStudio.App.Models;
using WXDataStudio.App.Services;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var root = Path.Combine(Path.GetTempPath(), "WXDataStudio-Smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "sample.db");

SQLitePCL.Batteries_V2.Init();
await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
{
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE rcontact(username TEXT PRIMARY KEY, conRemark TEXT, nickname TEXT);
        CREATE TABLE rconversation(username TEXT PRIMARY KEY, content TEXT, conversationTime INTEGER);
        CREATE TABLE message(msgId INTEGER PRIMARY KEY, msgSvrId INTEGER, talker TEXT, isSend INTEGER,
            type INTEGER, status INTEGER, createTime INTEGER, msgSeq INTEGER, content TEXT, imgPath TEXT, reserved TEXT);
        INSERT INTO rcontact VALUES ('alice','Alice Remark','Alice');
        INSERT INTO rconversation VALUES ('alice','last hello',1726500000000);
        INSERT INTO message VALUES (1,101,'alice',0,1,3,1726500000000,1,'hello',NULL,NULL);
        INSERT INTO message VALUES (2,102,'alice',1,3,2,1726500001000,2,'image','img_001',NULL);
        INSERT INTO message VALUES (3,103,'alice',1,49,2,1726500002000,3,
            '<msg><appmsg><type>5</type><title>OpenAI</title><url>https://openai.com</url></appmsg></msg>',NULL,NULL);
        """;
    await cmd.ExecuteNonQueryAsync();
}

var reader = new WeChatDatabaseReader();
var tables = await reader.ListTablesAsync(dbPath);
Assert(tables.Contains("message"), "message table missing");
Assert(tables.Contains("rconversation"), "rconversation table missing");

var conversations = await reader.LoadConversationsAsync(dbPath);
Assert(conversations.Count == 1, "conversation count mismatch");
Assert(conversations[0].EffectiveName == "Alice Remark", "effective name mismatch");

var messages = await reader.LoadMessagesAsync(dbPath, "alice");
Assert(messages.Count == 3, "message count mismatch");
Assert(messages[0].Kind == MessageKind.Text, "text classification mismatch");
Assert(messages[1].Kind == MessageKind.Image, "image classification mismatch");
Assert(messages[2].Kind == MessageKind.Link, "link classification mismatch");

var meta = MessageMetadataParser.Parse(messages[2].Kind, messages[2].Content);
Assert(meta.Title == "OpenAI", "card title parse mismatch");
Assert(meta.Url == "https://openai.com", "card url parse mismatch");

var groupEnvelope = GroupMessageParser.Parse("wxid_member_1:\nhello group");
Assert(groupEnvelope.Sender == "wxid_member_1", "group sender parse mismatch");
Assert(groupEnvelope.Body == "hello group", "group body parse mismatch");

const string quoteXml = "<msg><appmsg><type>57</type><refermsg><displayname>Bob</displayname><content><![CDATA[quoted hello]]></content></refermsg></appmsg></msg>";
Assert(MessageTypeClassifier.Classify(49, quoteXml) == MessageKind.Quote, "quote classification mismatch");
var quoteMeta = MessageMetadataParser.Parse(MessageKind.Quote, quoteXml);
Assert(quoteMeta.QuoteSender == "Bob", "quote sender parse mismatch");
Assert(quoteMeta.QuoteContent == "quoted hello", "quote content parse mismatch");

const string miniXml = "<msg><appmsg><type>33</type><weappinfo><username>gh_demo@app</username><pagepath>pages/home</pagepath></weappinfo></appmsg></msg>";
Assert(MessageTypeClassifier.Classify(49, miniXml) == MessageKind.MiniProgram, "mini program classification mismatch");
var miniMeta = MessageMetadataParser.Parse(MessageKind.MiniProgram, miniXml);
Assert(miniMeta.MiniProgramUserName == "gh_demo@app", "mini program username parse mismatch");
Assert(miniMeta.MiniProgramPath == "pages/home", "mini program path parse mismatch");

Assert(!MessageKindPolicy.IsEditable(MessageKind.Transfer), "transfer must stay read-only");
Assert(!MessageKindPolicy.IsEditable(MessageKind.RedPacket), "red packet must stay read-only");
Assert(MessageKindPolicy.IsEditable(MessageKind.Text), "text should be editable in workspace");

const string transferXml = "<msg><appmsg><type>2000</type><wcpayinfo><feedesc>¥1.00</feedesc><paysubtype>1</paysubtype></wcpayinfo></appmsg></msg>";
const string redPacketXml = "<msg><appmsg><type>2001</type><wcpayinfo><sendid>demo</sendid><feedesc>红包</feedesc></wcpayinfo></appmsg></msg>";
Assert(MessageTypeClassifier.Classify(49, transferXml) == MessageKind.Transfer, "transfer classification mismatch");
Assert(MessageTypeClassifier.Classify(49, redPacketXml) == MessageKind.RedPacket, "red packet classification mismatch");

var sensitiveSource = new WeChatMessage
{
    LocalId = 99,
    ConversationId = "alice",
    RawType = 49,
    CreateTime = messages[0].CreateTime + 5,
    Content = transferXml,
    Kind = MessageKind.Transfer
};
var sensitiveWorkspace = new WorkspaceService().Create(root, conversations[0], new[] { sensitiveSource });
var sensitiveEditBlocked = false;
try
{
    new WorkspaceService().EditContent(sensitiveWorkspace, 99, "changed");
}
catch (InvalidOperationException)
{
    sensitiveEditBlocked = true;
}
Assert(sensitiveEditBlocked, "existing sensitive record editing must be blocked");

var readinessDir = Path.Combine(root, "readiness");
Directory.CreateDirectory(readinessDir);
await File.WriteAllBytesAsync(Path.Combine(readinessDir, "EnMicroMsg.db"), new byte[] { 1 });
await File.WriteAllBytesAsync(Path.Combine(readinessDir, "EnMicroMsg.db-wal"), new byte[] { 1 });
await File.WriteAllBytesAsync(Path.Combine(readinessDir, "EnMicroMsg.db-shm"), new byte[] { 1 });
var readiness = new MigrationReadinessService().Evaluate(readinessDir, conversations, messages, Array.Empty<string>());
Assert(!readiness.IsBlocked, "readiness unexpectedly blocked");
Assert(readiness.Status == "Ready", "readiness status mismatch");
var unknownMessage = new WeChatMessage
{
    LocalId = 77,
    ConversationId = "alice",
    RawType = 999999,
    CreateTime = messages[^1].CreateTime + 1,
    Content = "unknown",
    Kind = MessageKind.Unknown
};
var reviewReadiness = new MigrationReadinessService().Evaluate(
    readinessDir, conversations, messages.Concat(new[] { unknownMessage }).ToArray(), Array.Empty<string>());
Assert(reviewReadiness.Status == "Review", "unknown message should require review");
Assert(reviewReadiness.UnknownMessageCount == 1, "unknown message count mismatch");
var reportFiles = await new MigrationReportExporter().ExportAsync(readiness, Path.Combine(root, "reports"));
Assert(File.Exists(reportFiles.JsonPath), "readiness JSON report missing");
Assert(File.Exists(reportFiles.TextPath), "readiness text report missing");

var legacyDbPath = Path.Combine(root, "legacy-encrypted.db");
await using (var connection = new SqliteConnection($"Data Source={legacyDbPath}"))
{
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "PRAGMA key='smoke-secret';PRAGMA cipher_use_hmac=OFF;PRAGMA cipher_page_size=1024;PRAGMA kdf_iter=4000;CREATE TABLE smoke(id INTEGER PRIMARY KEY, value TEXT);INSERT INTO smoke(value) VALUES('ok');";
    await cmd.ExecuteNonQueryAsync();
}
var legacyTables = await reader.ListTablesAsync(legacyDbPath,
    new DatabaseOpenOptions { Password = "smoke-secret", UseLegacyWeChatCipher = true });
Assert(legacyTables.Contains("smoke"), "legacy SQLCipher profile open failed");

var rawDbPath = Path.Combine(root, "raw-encrypted.db");
const string rawHex = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
await using (var connection = new SqliteConnection($"Data Source={rawDbPath}"))
{
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = $"PRAGMA key=\"x'{rawHex}'\";CREATE TABLE rawtest(id INTEGER PRIMARY KEY);";
    await cmd.ExecuteNonQueryAsync();
}
var rawTables = await reader.ListTablesAsync(rawDbPath,
    new DatabaseOpenOptions { RawKeyHex = rawHex, CipherCompatibility = 4 });
Assert(rawTables.Contains("rawtest"), "raw hex SQLCipher open failed");

var workspaceService = new WorkspaceService();
var workspace = workspaceService.Create(root, conversations[0], messages);
var addedText = workspaceService.AddMessage(workspace, MessageKind.Text, "new local text");
Assert(addedText.IsNew && addedText.CanEdit, "new workspace text message mismatch");

var mediaSource = Path.Combine(root, "sample-media.png");
await File.WriteAllBytesAsync(mediaSource, new byte[] { 1, 2, 3, 4 });
var importedMedia = await new WorkspaceMediaService(Path.Combine(root, "workspace-media"))
    .ImportAsync(workspace, mediaSource);
Assert(File.Exists(importedMedia), "workspace media import failed");
var addedImage = workspaceService.AddMessage(workspace, MessageKind.Image, "[image]", importedMedia);
Assert(addedImage.Attachment == importedMedia, "workspace image attachment mismatch");

var sensitiveCreateBlocked = false;
try
{
    workspaceService.AddMessage(workspace, MessageKind.Transfer, "not allowed");
}
catch (InvalidOperationException)
{
    sensitiveCreateBlocked = true;
}
Assert(sensitiveCreateBlocked, "sensitive workspace message creation must be blocked");

workspaceService.EditContent(workspace, 1, "edited hello");
workspaceService.EditTime(workspace, 1, messages[0].CreateTime + 60);
Assert(workspace.Audit.Count == 4, "workspace audit mismatch");

var diffService = new WorkspaceDiffService();
var diffs = diffService.GetDiffs(workspace);
Assert(diffs.Count >= 2, "workspace diff mismatch");

var catalogRoot = Path.Combine(root, "snapshots");
var incompleteDir = Path.Combine(catalogRoot, "20260101-000000");
var usableDir = Path.Combine(catalogRoot, "20260102-000000");
Directory.CreateDirectory(incompleteDir);
Directory.CreateDirectory(usableDir);
await File.WriteAllBytesAsync(Path.Combine(usableDir, "EnMicroMsg.db"), new byte[] { 1, 2, 3 });
var catalogManifest = new SnapshotManifest
{
    CreatedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
    Device = new DeviceInfo(),
    Files = new[] { new SnapshotFile("EnMicroMsg.db", 3, "catalog-test") }
};
await File.WriteAllTextAsync(Path.Combine(usableDir, "manifest.json"),
    System.Text.Json.JsonSerializer.Serialize(catalogManifest));
var catalog = new SnapshotCatalogService(catalogRoot);
var latestSnapshot = await catalog.FindLatestUsableAsync();
Assert(latestSnapshot is not null, "snapshot catalog did not find usable snapshot");
Assert(latestSnapshot!.DirectoryPath == usableDir, "snapshot catalog selected wrong directory");
Assert(latestSnapshot.DatabaseSize == 3, "snapshot catalog size mismatch");

var rollbackDir = Path.Combine(root, "rollback-source");
Directory.CreateDirectory(rollbackDir);
var rollbackBytes = new byte[] { 10, 20, 30, 40 };
await File.WriteAllBytesAsync(Path.Combine(rollbackDir, "EnMicroMsg.db"), rollbackBytes);
var rollbackHash = Convert.ToHexString(
    System.Security.Cryptography.SHA256.HashData(rollbackBytes)).ToLowerInvariant();
var rollbackManifest = new SnapshotManifest
{
    CreatedAt = DateTimeOffset.Now,
    Device = new DeviceInfo(),
    Files = new[] { new SnapshotFile("EnMicroMsg.db", rollbackBytes.Length, rollbackHash) }
};
await File.WriteAllTextAsync(Path.Combine(rollbackDir, "manifest.json"),
    System.Text.Json.JsonSerializer.Serialize(rollbackManifest));
var rollback = await new RollbackPackageService().CreateAsync(
    rollbackDir, Path.Combine(root, "rollback-output"));
Assert(File.Exists(rollback.PackagePath), "rollback package missing");
Assert(rollback.Size > 0 && rollback.Sha256.Length == 64, "rollback package metadata mismatch");
using (var archive = System.IO.Compression.ZipFile.OpenRead(rollback.PackagePath))
{
    Assert(archive.GetEntry("manifest.json") is not null, "rollback package manifest missing");
    Assert(archive.GetEntry("EnMicroMsg.db") is not null, "rollback package database missing");
}

var workspacePath = await workspaceService.SaveAsync(workspace, root);
var loaded = await workspaceService.LoadAsync(workspacePath);
Assert(loaded.Messages.Single(x => x.LocalId == 1).Content == "edited hello", "workspace persistence mismatch");

Console.WriteLine("WXDataStudio smoke tests passed.");
Console.WriteLine($"Tables={tables.Count}; Conversations={conversations.Count}; Messages={messages.Count}; Diffs={diffs.Count}");

try
{
    Directory.Delete(root, true);
}
catch
{
    // Best-effort cleanup only; smoke success should not depend on temp cleanup.
}
