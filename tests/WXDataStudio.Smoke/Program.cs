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
Assert(conversations[0].LastTime == 1726500000, "conversation timestamp normalization mismatch");
Assert(!string.IsNullOrWhiteSpace(conversations[0].LastDisplayTime), "conversation display time missing");
var invalidTimeMessage = new WeChatMessage
{
    LocalId = 404,
    ConversationId = "alice",
    CreateTime = long.MaxValue,
    Kind = MessageKind.Text,
    Content = "bad time"
};
Assert(invalidTimeMessage.DisplayTime == "", "invalid message timestamp should not crash rendering");
Assert(WorkspaceMessage.From(invalidTimeMessage).DisplayTime == "",
    "invalid workspace timestamp should not crash rendering");

var altDbPath = Path.Combine(root, "alternate-schema.db");
await using (var connection = new SqliteConnection($"Data Source={altDbPath}"))
{
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE ContactBook(userName TEXT PRIMARY KEY, nickName TEXT, remark TEXT);
        CREATE TABLE ChatRows(localId INTEGER PRIMARY KEY, serverId INTEGER, conversationId TEXT,
            isOutgoing INTEGER, msgType INTEGER, msgStatus INTEGER, time INTEGER, sequence INTEGER,
            body TEXT, imagePath TEXT, extra TEXT);
        INSERT INTO ContactBook VALUES ('bob','Bob Nick','Bob Remark');
        INSERT INTO ChatRows VALUES (11,201,'bob',0,1,3,1726600000000,1,'alt hello',NULL,NULL);
        """;
    await cmd.ExecuteNonQueryAsync();
}
var altSchema = await reader.DetectSchemaAsync(altDbPath);
Assert(altSchema.MessageTable == "ChatRows", "alternate message table detection failed");
Assert(altSchema.ConversationTable is null, "alternate schema should use message fallback");
Assert(altSchema.ContactTable == "ContactBook", "alternate contact table detection failed");
Assert(altSchema.UsesConversationFallback, "alternate conversation fallback flag mismatch");
var altConversations = await reader.LoadConversationsAsync(altDbPath);
Assert(altConversations.Count == 1, "alternate conversation fallback count mismatch");
Assert(altConversations[0].EffectiveName == "Bob Remark", "alternate contact join mismatch");
Assert(altConversations[0].LastTime == 1726600000, "alternate conversation time normalization mismatch");
var altMessages = await reader.LoadMessagesAsync(altDbPath, "bob");
Assert(altMessages.Count == 1 && altMessages[0].Content == "alt hello",
    "alternate message schema mapping mismatch");

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

const string flaggedLinkXml = "<msg><appmsg><type>5</type><appattach><fileext></fileext></appattach></appmsg></msg>";
Assert(MessageTypeClassifier.Classify(285212721, flaggedLinkXml) == MessageKind.Link,
    "flagged app-message link classification mismatch");
const string finderShareXml = "<msg><appmsg><type>51</type><finder_feed/><appattach><fileext></fileext></appattach></appmsg></msg>";
Assert(MessageTypeClassifier.Classify(754974769, finderShareXml) == MessageKind.Link,
    "finder share must not be classified as a file attachment");
const string flaggedFileXml = "<msg><appmsg><type>6</type><title>report.pdf</title><appattach><fileext>pdf</fileext><md5>00112233445566778899aabbccddeeff</md5></appattach></appmsg></msg>";
Assert(MessageTypeClassifier.Classify(285212721, flaggedFileXml) == MessageKind.File,
    "flagged app-message file classification mismatch");
Assert(MessageTypeClassifier.Classify(285212714, "<msg/>") == MessageKind.ContactCard,
    "flagged fallback message type must use the normalized type");
const string flaggedTransferXml = "<msg><appmsg><type>2000</type><wcpayinfo><paysubtype>1</paysubtype></wcpayinfo></appmsg></msg>";
Assert(MessageTypeClassifier.Classify(285212721, flaggedTransferXml) == MessageKind.Transfer,
    "flagged transfer message must remain read-only classified");

Assert(!MessageKindPolicy.IsEditable(MessageKind.Transfer), "transfer must stay read-only");
Assert(!MessageKindPolicy.IsEditable(MessageKind.RedPacket), "red packet must stay read-only");
Assert(MessageKindPolicy.IsEditable(MessageKind.Text), "text should be editable in workspace");

var mediaDevice = new DeviceInfo
{
    AccountDirectory = "private-account",
    ExternalAccountDirectory = "external-account"
};
var imageSearchLocations = MediaLocatorService.GetSearchLocations(mediaDevice, MessageKind.Image);
Assert(imageSearchLocations.Any(x => x.RequiresRoot &&
    x.Directory == "/data/user/0/com.tencent.mm/MicroMsg/private-account/image2"),
    "image search must include the private WeChat account directory");
Assert(imageSearchLocations.Any(x => !x.RequiresRoot &&
    x.Directory == "/sdcard/Android/data/com.tencent.mm/MicroMsg/external-account/image2"),
    "image search must retain the external WeChat account directory fallback");
var unsafeMediaDevice = mediaDevice with { AccountDirectory = "bad';id" };
Assert(!MediaLocatorService.GetSearchLocations(unsafeMediaDevice, MessageKind.Image)
        .Any(x => x.RequiresRoot),
    "unsafe device-provided account directories must never enter root shell commands");
var fileSearchLocations = MediaLocatorService.GetSearchLocations(mediaDevice, MessageKind.File);
Assert(fileSearchLocations.Any(x =>
        x.Directory == "/sdcard/Android/data/com.tencent.mm/MicroMsg/Download"),
    "file search must include the shared WeChat download directory");
Assert(fileSearchLocations.Any(x => x.Directory == "/sdcard/Download/WeiXin"),
    "file search must include the user-visible WeChat download directory");
var fileTokenMessage = new WeChatMessage
{
    Kind = MessageKind.File,
    Content = "<msg><appmsg><title>report.pdf</title><appattach><md5>00112233445566778899aabbccddeeff</md5></appattach></appmsg></msg>"
};
var fileTokens = MediaLocatorService.GetSearchTokens(fileTokenMessage);
Assert(fileTokens.Contains("00112233445566778899aabbccddeeff"),
    "file media tokens must include the attachment MD5");
Assert(!fileTokens.Contains("report"),
    "file media tokens must prefer MD5 over a broad filename fallback");
var titleOnlyFileTokenMessage = new WeChatMessage
{
    Kind = MessageKind.File,
    Content = "<msg><appmsg><title>report.pdf</title></appmsg></msg>"
};
Assert(MediaLocatorService.GetSearchTokens(titleOnlyFileTokenMessage).Contains("report"),
    "file media tokens must use the filename when no valid MD5 exists");
var unsafeFileTokenMessage = new WeChatMessage
{
    Kind = MessageKind.File,
    Content = "<msg><appmsg><title>a.pdf</title><appattach><md5>0</md5></appattach></appmsg></msg>"
};
Assert(MediaLocatorService.GetSearchTokens(unsafeFileTokenMessage).Count == 0,
    "short or malformed attachment identifiers must not trigger broad searches");
var encodedFileTokenMessage = new WeChatMessage
{
    Kind = MessageKind.File,
    Content = "<msg><appmsg><title><![CDATA[annual &amp; tax.pdf]]></title></appmsg></msg>"
};
Assert(MediaLocatorService.GetSearchTokens(encodedFileTokenMessage).Contains("annual & tax"),
    "file title tokens must decode entities and preserve safe spaces");

var sourceFingerprint = new DeviceFileFingerprint(8192, "aabbcc");
Assert(sourceFingerprint.Matches(new DeviceFileFingerprint(8192, "AABBCC")),
    "device fingerprints should compare SHA-256 case-insensitively");
Assert(!sourceFingerprint.Matches(new DeviceFileFingerprint(8193, "aabbcc")),
    "device fingerprints must reject size mismatches");
Assert(!sourceFingerprint.Matches(new DeviceFileFingerprint(8192, "ddeeff")),
    "device fingerprints must reject SHA-256 mismatches");

Assert(LegacyWeChatKeyCandidateService.BuildLegacyKey("", "12345").Length == 7,
    "empty-device legacy key candidate should be supported");
var compatibleCandidates = LegacyWeChatKeyCandidateService.ExtractCompatibleInfoCandidates(
    "noise\n867530912345678\nother 0123456789abcdef end");
Assert(compatibleCandidates.Contains("867530912345678"), "CompatibleInfo IMEI candidate parse failed");
Assert(compatibleCandidates.Contains("0123456789abcdef"), "CompatibleInfo hex candidate parse failed");
var uinCandidates = LegacyWeChatKeyCandidateService.ParseUins(
    "<map><int name=\"default_uin\" value=\"123456\"/><string name=\"last_login_uin\">-987654</string><int name=\"_auth_uin\" value=\"123456\"/></map>");
Assert(uinCandidates.Count == 2, "UIN candidate deduplication mismatch");
Assert(uinCandidates.Contains("123456"), "default_uin parse failed");
Assert(uinCandidates.Contains("-987654"), "last_login_uin parse failed");

var offlineResolverSnapshot = Path.Combine(root, "offline-resolver-snapshot");
var offlineSupport = Path.Combine(offlineResolverSnapshot, "support");
Directory.CreateDirectory(offlineSupport);
await File.WriteAllTextAsync(Path.Combine(offlineSupport, "auth_info_key_prefs.xml"),
    "<map><int name=\"_auth_uin\" value=\"123456\"/></map>");
await File.WriteAllTextAsync(Path.Combine(offlineSupport, "device_tokens.json"),
    "{\"persist.radio.imei\":\"867530912345678\"}");
var offlineCandidatesService = new LegacyWeChatKeyCandidateService(
    new AdbService(Path.Combine(root, "missing-adb.exe")));
var offlineCandidates = await offlineCandidatesService.BuildAsync(offlineResolverSnapshot);
var expectedOfflineKey = LegacyWeChatKeyCandidateService.BuildLegacyKey(
    "867530912345678", "123456");
Assert(offlineCandidates.Any(x => x.Password == expectedOfflineKey),
    "offline snapshot resolver did not build expected device+uin key");
var offlineDiagnostics = await offlineCandidatesService.DiagnoseAsync(offlineResolverSnapshot);
Assert(offlineDiagnostics.UinFound, "offline snapshot UIN diagnostics failed");
Assert(offlineDiagnostics.TokenSources.Any(x => x.Contains("snapshot:", StringComparison.Ordinal)),
    "offline snapshot token source diagnostics failed");

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

var sc1EncryptedPath = Path.Combine(root, "sc1-encrypted.db");
await using (var bootstrap = new SqliteConnection("Data Source=:memory:;Pooling=False"))
{
    await bootstrap.OpenAsync();
    await using var defaultCompat = bootstrap.CreateCommand();
    defaultCompat.CommandText = "PRAGMA cipher_default_page_size=1024;" +
                                "PRAGMA cipher_default_kdf_iter=4000;" +
                                "PRAGMA cipher_default_use_hmac=OFF;" +
                                "PRAGMA cipher_default_kdf_algorithm=PBKDF2_HMAC_SHA1;" +
                                "PRAGMA cipher_default_hmac_algorithm=HMAC_SHA1;";
    await defaultCompat.ExecuteNonQueryAsync();
}
await using (var connection = new SqliteConnection($"Data Source={sc1EncryptedPath};Pooling=False"))
{
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "PRAGMA key='sc1-secret';CREATE TABLE sc1test(id INTEGER PRIMARY KEY, value TEXT);INSERT INTO sc1test(value) VALUES('ok');";
    await cmd.ExecuteNonQueryAsync();
}
var sc1CompatTables = await reader.ListTablesAsync(sc1EncryptedPath,
    new DatabaseOpenOptions { Password = "sc1-secret", CipherCompatibility = 1 });
Assert(sc1CompatTables.Contains("sc1test"), "SQLCipher compatibility 1 smoke database did not open");

var sc1 = new LegacySc1PageDecryptService();
Assert(sc1.MatchesPassword(sc1EncryptedPath, "sc1-secret"), "SC1 password first-page match failed");
Assert(!sc1.MatchesPassword(sc1EncryptedPath, "wrong-secret"), "SC1 wrong password unexpectedly matched");
var sc1PlainPath = Path.Combine(root, "sc1-plain.db");
await sc1.DecryptWithPasswordAsync(sc1EncryptedPath, "sc1-secret", sc1PlainPath);
var sc1PlainTables = await reader.ListTablesAsync(
    sc1PlainPath, new DatabaseOpenOptions { ReadOnly = true });
Assert(sc1PlainTables.Contains("sc1test"), "SC1 page decrypt output did not open as plain SQLite");

var sc1EncryptedBytes = await File.ReadAllBytesAsync(sc1EncryptedPath);
var sc1RawKey = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
    System.Text.Encoding.UTF8.GetBytes("sc1-secret"),
    sc1EncryptedBytes.AsSpan(0, 16),
    4000,
    System.Security.Cryptography.HashAlgorithmName.SHA1,
    32);
var sc1RawHex = Convert.ToHexString(sc1RawKey).ToLowerInvariant();
Assert(sc1.MatchesRawKey(sc1EncryptedPath, sc1RawHex), "SC1 raw AES key match failed");
var sc1RawPlainPath = Path.Combine(root, "sc1-raw-plain.db");
await sc1.DecryptWithRawKeyAsync(sc1EncryptedPath, sc1RawHex, sc1RawPlainPath);
var sc1RawTables = await reader.ListTablesAsync(
    sc1RawPlainPath, new DatabaseOpenOptions { ReadOnly = true });
Assert(sc1RawTables.Contains("sc1test"), "SC1 raw AES key decrypt output did not open");

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

var textAttachmentBlocked = false;
try
{
    workspaceService.EditAttachment(workspace, addedText.LocalId, importedMedia);
}
catch (InvalidOperationException)
{
    textAttachmentBlocked = true;
}
Assert(textAttachmentBlocked, "text message media replacement must be blocked");

var addedTextId = addedText.LocalId;
workspaceService.Revert(workspace, addedTextId);
Assert(workspace.Messages.All(x => x.LocalId != addedTextId), "new workspace message revert must remove the message");

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
Assert(workspace.Audit.Count == 5, "workspace audit mismatch");

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
