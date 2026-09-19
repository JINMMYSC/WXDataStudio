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

Assert(MessageKindPolicy.IsEditable(MessageKind.Transfer), "transfer records must be editable for recovery");
Assert(MessageKindPolicy.IsEditable(MessageKind.RedPacket), "red packet records must be editable for recovery");
Assert(MessageKindPolicy.IsTransaction(MessageKind.Transfer), "transfer must be labelled transaction-class");
Assert(MessageKindPolicy.IsTransaction(MessageKind.RedPacket), "red packet must be labelled transaction-class");
Assert(!MessageKindPolicy.IsTransaction(MessageKind.Text), "text must not be labelled transaction-class");
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
Assert(sensitiveWorkspace.Messages[0].IsTransaction, "transfer record must be labelled transaction-class");
Assert(sensitiveWorkspace.Messages[0].CanEdit, "transaction records must be editable for recovery");
new WorkspaceService().EditContent(sensitiveWorkspace, 99, "recovered transfer note");
Assert(sensitiveWorkspace.Messages[0].Content == "recovered transfer note",
    "transaction record content edit did not apply");

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

var createdTransfer = workspaceService.AddMessage(workspace, MessageKind.Transfer, "recovered transfer");
Assert(createdTransfer.Kind == MessageKind.Transfer && createdTransfer.IsTransaction,
    "transaction-class message creation must be allowed and labelled");
Assert(createdTransfer.CanEdit, "created transaction message must stay editable");
workspaceService.Revert(workspace, createdTransfer.LocalId);

workspaceService.EditContent(workspace, 1, "edited hello");
workspaceService.EditTime(workspace, 1, messages[0].CreateTime + 60);
Assert(workspace.Audit.Any(x => x.Field == "content"), "workspace audit must record content edits");
Assert(workspace.Audit.Any(x => x.Field == "createTime"), "workspace audit must record time edits");
Assert(workspace.Audit.Any(x => x.Field == "create"), "workspace audit must record created messages");
Assert(workspace.Audit.Count(x => x.Field == "delete-new") == 2,
    "workspace audit must record both removed new messages");

var diffService = new WorkspaceDiffService();
var diffs = diffService.GetDiffs(workspace);
Assert(diffs.Count >= 2, "workspace diff mismatch");

// Batch timeline shift used by the editor's "shift 60s" action: the anchor and
// later messages move together, earlier messages stay put.
var shiftWorkspace = workspaceService.Create(root, conversations[0], new[]
{
    new WeChatMessage { LocalId = 11, ConversationId = "alice", Kind = MessageKind.Text, CreateTime = 1_726_600_000 },
    new WeChatMessage { LocalId = 12, ConversationId = "alice", Kind = MessageKind.Text, CreateTime = 1_726_600_100 },
    new WeChatMessage { LocalId = 13, ConversationId = "alice", Kind = MessageKind.Text, CreateTime = 1_726_600_200 }
});
var packet = WorkspaceMessage.From(new WeChatMessage
{
    LocalId = 90,
    ConversationId = "alice",
    Kind = MessageKind.RedPacket,
    CreateTime = 1_726_600_150,
    Content = "<msg><appmsg><type>2001</type></appmsg></msg>"
});
Assert(packet.IsTransaction && packet.CanEdit, "red packet record must be labelled but editable");
shiftWorkspace.Messages.Add(packet);
shiftWorkspace.Messages.Sort((a, b) => a.CreateTime.CompareTo(b.CreateTime));

var beforeShift = shiftWorkspace.Messages.ToDictionary(x => x.LocalId, x => x.CreateTime);
var (shifted, skipped) = workspaceService.ShiftTimeline(shiftWorkspace, 12, 60);
Assert(shifted == 3 && skipped == 0, "batch shift must move the whole tail");
Assert(shiftWorkspace.Messages.Single(x => x.LocalId == 11).CreateTime == beforeShift[11],
    "batch shift must not touch messages before the anchor");
Assert(shiftWorkspace.Messages.Single(x => x.LocalId == 12).CreateTime == beforeShift[12] + 60,
    "batch shift must move the anchor message");
Assert(shiftWorkspace.Messages.Single(x => x.LocalId == 13).CreateTime == beforeShift[13] + 60,
    "batch shift must move later messages");
Assert(shiftWorkspace.Messages.Single(x => x.LocalId == 90).CreateTime == beforeShift[90] + 60,
    "batch shift must move transaction-class records too");

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

var censusConversations = new[]
{
    new ConversationItem { Username = "alice", Remark = "Alice" },
    new ConversationItem { Username = "team@chatroom", Remark = "Team" }
};
var censusMessages = new[]
{
    new WeChatMessage { LocalId = 1, ConversationId = "alice", Kind = MessageKind.Text, Content = "census-secret-text" },
    new WeChatMessage { LocalId = 2, ConversationId = "alice", Kind = MessageKind.Image, ImgPath = "census-secret-image" },
    new WeChatMessage { LocalId = 3, ConversationId = "team@chatroom", Kind = MessageKind.Text, Sender = "wxid_sender", Content = "census-secret-group" },
    new WeChatMessage { LocalId = 4, ConversationId = "team@chatroom", IsOutgoing = true, Kind = MessageKind.RedPacket, Content = "census-secret-packet" },
    new WeChatMessage { LocalId = 5, ConversationId = "alice", RawType = 318767153, Kind = MessageKind.Unknown, Content = "census-secret-unknown" }
};
var census = new MessageCensusService().Build(censusConversations, censusMessages);
Assert(census.ConversationCount == 2, "census conversation count mismatch");
Assert(census.GroupConversationCount == 1, "census group conversation count mismatch");
Assert(census.MessageCount == 5, "census message count mismatch");
Assert(census.IncomingCount == 4 && census.OutgoingCount == 1, "census direction count mismatch");
Assert(census.GroupMessageCount == 2, "census group message count mismatch");
Assert(census.GroupSenderResolvedCount == 1, "census group sender resolution mismatch");
Assert(census.TransactionCount == 1, "census transaction count mismatch");
Assert(census.CountOf(MessageKind.Text) == 2, "census text count mismatch");
Assert(census.UnknownMessageCount == 1, "census unknown count mismatch");
Assert(census.UnknownRawTypes.Single().RawType == 318767153, "census unknown raw type mismatch");
Assert(census.MissingKinds.Contains(MessageKind.Voice), "census missing kind detection mismatch");
Assert(!census.MissingKinds.Contains(MessageKind.Text), "census flagged a present kind as missing");
var censusText = census.ToText();
var censusJson = census.ToJson();
Assert(censusText.Contains("kind-text=2"), "census text report mismatch");
Assert(censusJson.Contains("\"rawType\": 318767153"), "census json raw type mismatch");
Assert(!censusText.Contains("census-secret", StringComparison.Ordinal), "census text leaked message content");
Assert(!censusJson.Contains("census-secret", StringComparison.Ordinal), "census json leaked message content");
Assert(!censusJson.Contains("alice", StringComparison.OrdinalIgnoreCase), "census json leaked conversation ids");

// Regression: SQLite rewrites -shm bookkeeping even for read-only WAL opens, so
// snapshot reads must happen on a verified working copy.
var liveRoot = Path.Combine(root, "live-wal");
Directory.CreateDirectory(liveRoot);
var walSnapshotDir = Path.Combine(root, "wal-snapshot");
Directory.CreateDirectory(walSnapshotDir);
var liveDbPath = Path.Combine(liveRoot, "EnMicroMsg.db");
await using (var live = new SqliteConnection($"Data Source={liveDbPath}"))
{
    await live.OpenAsync();
    await using (var pragma = live.CreateCommand())
    {
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        Assert(Convert.ToString(await pragma.ExecuteScalarAsync()) == "wal",
            "wal mode was not enabled for the regression fixture");
    }
    await using (var seed = live.CreateCommand())
    {
        seed.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);" +
                           "INSERT INTO t VALUES (1,'wal-row');";
        await seed.ExecuteNonQueryAsync();
    }

    // Copy the live file set while the connection is open so the snapshot holds
    // a real -wal/-shm pair, mirroring a captured phone snapshot.
    foreach (var name in new[] { "EnMicroMsg.db", "EnMicroMsg.db-wal", "EnMicroMsg.db-shm" })
    {
        var source = Path.Combine(liveRoot, name);
        if (File.Exists(source)) File.Copy(source, Path.Combine(walSnapshotDir, name), true);
    }
}
var walManifestFiles = new List<SnapshotFile>();
foreach (var name in new[] { "EnMicroMsg.db", "EnMicroMsg.db-wal", "EnMicroMsg.db-shm" })
{
    var path = Path.Combine(walSnapshotDir, name);
    if (!File.Exists(path)) continue;
    var bytes = await File.ReadAllBytesAsync(path);
    walManifestFiles.Add(new SnapshotFile(name, bytes.LongLength,
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()));
}
await File.WriteAllTextAsync(Path.Combine(walSnapshotDir, "manifest.json"),
    System.Text.Json.JsonSerializer.Serialize(new SnapshotManifest
    {
        CreatedAt = DateTimeOffset.Now,
        Device = new DeviceInfo(),
        Files = walManifestFiles
    }));
Assert((await new SnapshotIntegrityService().CheckAsync(walSnapshotDir)).Count == 0,
    "wal snapshot fixture failed its own integrity check");

var readSession = await SnapshotReadSession.OpenAsync(walSnapshotDir);
Assert(File.Exists(readSession.DatabasePath), "read session database copy missing");
Assert(readSession.CopiedFiles.Contains("EnMicroMsg.db"), "read session did not copy the main database");
Assert(readSession.DatabasePath != Path.Combine(walSnapshotDir, "EnMicroMsg.db"),
    "read session must not expose the pristine snapshot database");
await using (var readOnly = new SqliteConnection(
    $"Data Source={readSession.DatabasePath};Mode=ReadOnly;Pooling=False"))
{
    await readOnly.OpenAsync();
    await using var count = readOnly.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM t;";
    Assert(Convert.ToInt32(await count.ExecuteScalarAsync()) == 1,
        "read session did not expose committed WAL contents");
}
Assert((await new SnapshotIntegrityService().CheckAsync(walSnapshotDir)).Count == 0,
    "snapshot files changed while the database was read");
await readSession.DisposeAsync();
Assert(!Directory.Exists(SnapshotReadSession.WorkingDirectoryFor(walSnapshotDir)),
    "read session working copy was not cleaned up");
Assert((await new SnapshotIntegrityService().CheckAsync(walSnapshotDir)).Count == 0,
    "snapshot integrity failed after read session disposal");

// Conversation export: text is escaped, local media is copied, device-only and
// missing attachments are counted, read-only records stay labelled.
var exportRoot = Path.Combine(root, "exports");
var exportMedia = Path.Combine(root, "export-image.png");
await File.WriteAllBytesAsync(exportMedia, new byte[] { 9, 8, 7, 6 });
var exportMessages = new[]
{
    new WeChatMessage
    {
        LocalId = 1, ConversationId = "alice", Kind = MessageKind.Text,
        CreateTime = 1_726_600_000, Content = "<script>alert('x')</script>"
    },
    new WeChatMessage
    {
        LocalId = 2, ConversationId = "alice", IsOutgoing = true, Kind = MessageKind.Image,
        CreateTime = 1_726_600_060, ImgPath = exportMedia
    },
    new WeChatMessage
    {
        LocalId = 3, ConversationId = "alice", Kind = MessageKind.Image,
        CreateTime = 1_726_600_120, ImgPath = "/sdcard/Android/data/com.tencent.mm/x.jpg"
    },
    new WeChatMessage
    {
        LocalId = 4, ConversationId = "alice", Kind = MessageKind.RedPacket,
        CreateTime = 1_726_600_180,
        Content = "<msg><appmsg><type>2001</type><title>packet</title></appmsg></msg>"
    },
    new WeChatMessage
    {
        LocalId = 5, ConversationId = "alice", Kind = MessageKind.File,
        CreateTime = 1_726_600_240,
        Content = "<msg><appmsg><type>6</type><title>report.pdf</title><fileext>pdf</fileext>" +
                  "<totallen>1024</totallen></appmsg></msg>"
    }
};
var export = await new ChatExportService().ExportAsync(exportRoot, "Alice", exportMessages, root);
Assert(File.Exists(export.HtmlPath) && File.Exists(export.JsonPath), "export files missing");
Assert(export.MessageCount == 5, "export message count mismatch");
Assert(export.LocalAssetCount == 1, "export local asset count mismatch");
Assert(export.DeviceOnlyAttachmentCount == 1, "export device-only attachment count mismatch");
Assert(export.MissingAttachmentCount == 1, "export missing attachment count mismatch");
var exportHtml = await File.ReadAllTextAsync(export.HtmlPath);
Assert(exportHtml.Contains("&lt;script&gt;"), "export html must escape message content");
Assert(!exportHtml.Contains("<script>alert", StringComparison.Ordinal), "export html must not embed raw script");
Assert(exportHtml.Contains("assets/"), "export html must reference the copied asset");
Assert(exportHtml.Contains("report.pdf"), "export html must describe file attachments");
Assert(!exportHtml.Contains("<appmsg>", StringComparison.Ordinal), "export html must not dump raw xml");
Assert(exportHtml.Contains("交易类"), "export html must label transaction-class records");
Assert(Directory.GetFiles(Path.Combine(export.DirectoryPath, "assets")).Length == 1,
    "export asset directory mismatch");
using (var exportJson = System.Text.Json.JsonDocument.Parse(
    await File.ReadAllTextAsync(export.JsonPath)))
{
    var rootElement = exportJson.RootElement;
    Assert(rootElement.GetProperty("messageCount").GetInt32() == 5, "export json message count mismatch");
    Assert(rootElement.GetProperty("attachments").GetProperty("localFiles").GetInt32() == 1,
        "export json attachment summary mismatch");
    Assert(rootElement.GetProperty("messages")[3].GetProperty("transactionClass").GetBoolean(),
        "export json must mark transaction-class records");
}

// Workspace to database writer, then SC1 re-encryption round trip: the restore
// channel must produce a database that decrypts back to what we wrote.
// Transfer / red packet payloads are built from amount and note fields.
// Editing a message's time must parse friendly input and re-sort the chat.
Assert(ChatBubble.TryParseTime("2026-09-20 14:30", out var parsedTime) &&
       DateTimeOffset.FromUnixTimeSeconds(parsedTime).LocalDateTime.ToString("yyyy-MM-dd HH:mm") ==
       "2026-09-20 14:30", "yyyy-MM-dd HH:mm parsing mismatch");
Assert(ChatBubble.TryParseTime("2026/9/20 14:30:05", out var parsedSlash) &&
       DateTimeOffset.FromUnixTimeSeconds(parsedSlash).LocalDateTime.Minute == 30,
    "yyyy/M/d parsing mismatch");
Assert(ChatBubble.TryParseTime("09-20 08:05", out var parsedShort) &&
       DateTimeOffset.FromUnixTimeSeconds(parsedShort).LocalDateTime.Year == DateTime.Today.Year,
    "month-day parsing must assume the current year");
Assert(!ChatBubble.TryParseTime("昨天下午", out _), "unparseable time must be rejected");

var sortWorkspace = new WorkspaceService().Create(root, conversations[0], new[]
{
    new WeChatMessage { LocalId = 11, ConversationId = "alice", Kind = MessageKind.Text, CreateTime = 1_726_600_000, Content = "first" },
    new WeChatMessage { LocalId = 12, ConversationId = "alice", Kind = MessageKind.Text, CreateTime = 1_726_600_100, Content = "second" },
    new WeChatMessage { LocalId = 13, ConversationId = "alice", Kind = MessageKind.Text, CreateTime = 1_726_600_200, Content = "third" }
});
new WorkspaceService().EditTime(sortWorkspace, 11, 1_726_600_300);
var orderedByTime = sortWorkspace.Messages
    .OrderBy(x => x.CreateTime).ThenBy(x => x.LocalId)
    .Select(x => x.LocalId).ToArray();
Assert(orderedByTime.SequenceEqual(new long[] { 12, 13, 11 }),
    "edited time must move the message to its new place in the order");

var moneyTransferXml = TransactionMessageTemplate.Build(MessageKind.Transfer, "¥88.88", "还款", "已收钱");
var transferBack = TransactionMessageTemplate.Read(MessageKind.Transfer, moneyTransferXml);
Assert(transferBack.Amount == "¥88.88" && transferBack.Note == "还款" && transferBack.Status == "已收钱",
    "transfer template round trip mismatch");
Assert(MessageTypeClassifier.Classify(49, moneyTransferXml) == MessageKind.Transfer,
    "transfer template must classify as a transfer");
var packetXml = TransactionMessageTemplate.Build(MessageKind.RedPacket, "¥200.00", "生日快乐", "");
Assert(MessageTypeClassifier.Classify(49, packetXml) == MessageKind.RedPacket,
    "red packet template must classify as a red packet");
Assert(TransactionMessageTemplate.Read(MessageKind.RedPacket, packetXml).Amount == "¥200.00",
    "red packet amount round trip mismatch");

// Editing a real transfer must only touch the fields the user changed: the
// transaction ids have to survive or WeChat cannot open the transfer detail.
const string realTransferXml =
    "<msg><appmsg><type>2000</type><title>转账</title><des>已收款</des>" +
    "<wcpayinfo><pay_memo>房租</pay_memo><feedesc>¥1,000.00</feedesc>" +
    "<transcationid>1000012345</transcationid><transferid>1000023456</transferid>" +
    "<paymsgid>pay_abc</paymsgid><payer_username>wxid_payer</payer_username>" +
    "<receiver_username>wxid_me</receiver_username><paysubtype>2</paysubtype>" +
    "</wcpayinfo></appmsg></msg>";
var editedTransfer = TransactionMessageTemplate.Update(
    realTransferXml, MessageKind.Transfer, "¥2,000.00", "房租预付款", "已收款");
Assert(editedTransfer.Contains("<feedesc>¥2,000.00</feedesc>"), "edited amount was not applied");
Assert(editedTransfer.Contains("<pay_memo>房租预付款</pay_memo>"), "edited note was not applied");
Assert(editedTransfer.Contains("<transcationid>1000012345</transcationid>"),
    "in-place edit must keep the transaction id");
Assert(editedTransfer.Contains("<transferid>1000023456</transferid>"),
    "in-place edit must keep the transfer id");
Assert(editedTransfer.Contains("<paymsgid>pay_abc</paymsgid>"),
    "in-place edit must keep the pay message id");
Assert(editedTransfer.Contains("<payer_username>wxid_payer</payer_username>"),
    "in-place edit must keep the payer");
var editedRead = TransactionMessageTemplate.Read(MessageKind.Transfer, editedTransfer);
Assert(editedRead.Amount == "¥2,000.00" && editedRead.Note == "房租预付款",
    "in-place edit did not read back");
Assert(editedRead.Status == "已收款", "bubble status must be read from des");

var writeDbPath = Path.Combine(root, "write-target.db");
await using (var connection = new SqliteConnection(
    $"Data Source={writeDbPath};Pooling=False"))
{
    await connection.OpenAsync();
    foreach (var sql in new[]
    {
        "PRAGMA page_size=1024;",
        "VACUUM;",
        """
        CREATE TABLE message(msgId INTEGER PRIMARY KEY, msgSvrId INTEGER, talker TEXT, isSend INTEGER,
            type INTEGER, status INTEGER, createTime INTEGER, msgSeq INTEGER, content TEXT,
            imgPath TEXT, reserved TEXT);
        """,
        "INSERT INTO message VALUES (1,101,'alice',0,1,3,1726500000,1,'hello',NULL,NULL);",
        "INSERT INTO message VALUES (2,102,'alice',0,1,3,1726500001,2,'world',NULL,NULL);"
    })
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
Assert(new FileInfo(writeDbPath).Length > 0 && new FileInfo(writeDbPath).Length % 1024 == 0,
    "smoke database fixture must use 1024-byte pages");

var writeService = new WorkspaceService();
var writeWorkspace = writeService.Create(root, conversations[0], new[]
{
    new WeChatMessage
    {
        LocalId = 1, ConversationId = "alice", Kind = MessageKind.Text,
        CreateTime = 1_726_500_000, Content = "hello"
    },
    new WeChatMessage
    {
        LocalId = 2, ConversationId = "alice", Kind = MessageKind.Text,
        CreateTime = 1_726_500_001, Content = "world"
    }
});
writeService.EditContent(writeWorkspace, 1, "recovered hello");
writeService.EditTime(writeWorkspace, 2, 1_726_500_100);
Assert(writeService.Delete(writeWorkspace, 2), "delete must remove the message");
var recoveredRow = writeService.AddMessage(
    writeWorkspace, MessageKind.Text, "recovered new row");
Assert(recoveredRow.IsNew, "recovered row must be marked as new");
var recoveredIncoming = writeService.AddMessage(
    writeWorkspace, MessageKind.Text, "recovered incoming row",
    attachment: null, when: null, isOutgoing: false, sender: "wxid_other");
Assert(!recoveredIncoming.IsOutgoing && recoveredIncoming.Sender == "wxid_other",
    "incoming recovered row must keep its direction and sender");

var writeResult = await new WorkspaceDatabaseWriter().ApplyAsync(writeDbPath, writeWorkspace);
Assert(writeResult.Updated == 1, "writer must update the edited row");
Assert(writeResult.Inserted == 2, "writer must insert both recovered rows");
Assert(writeResult.Deleted == 1, "writer must delete the removed row");
Assert((await WorkspaceDatabaseWriter.IntegrityCheckAsync(writeDbPath)) == "ok",
    "written database failed its integrity check");

// The phone stores createTime in milliseconds; a message written in seconds
// shows up as 1970. The writer must follow whatever unit the table already uses.
var millisecondsDb = Path.Combine(root, "write-ms.db");
await using (var msConnection = new SqliteConnection(
    $"Data Source={millisecondsDb};Pooling=False"))
{
    await msConnection.OpenAsync();
    foreach (var sql in new[]
    {
        "PRAGMA page_size=1024;", "VACUUM;",
        """
        CREATE TABLE message(msgId INTEGER PRIMARY KEY, msgSvrId INTEGER, talker TEXT, isSend INTEGER,
            type INTEGER, status INTEGER, createTime INTEGER, msgSeq INTEGER, content TEXT,
            imgPath TEXT, reserved TEXT);
        """,
        // Existing rows use milliseconds, like the real phone does.
        "INSERT INTO message VALUES (1,101,'alice',0,1,3,1726500000000,1,'hello',NULL,NULL);",
        // A row an older build wrote in seconds and WeChat would show as 1970.
        "INSERT INTO message VALUES (2,102,'alice',0,1,3,1726500900,2,'bad unit',NULL,NULL);"
    })
    {
        await using var msCommand = msConnection.CreateCommand();
        msCommand.CommandText = sql;
        await msCommand.ExecuteNonQueryAsync();
    }
}
var msService = new WorkspaceService();
var msWorkspace = msService.Create(root, conversations[0], new[]
{
    new WeChatMessage
    {
        LocalId = 1, ConversationId = "alice", Kind = MessageKind.Text,
        CreateTime = 1_726_500_000, Content = "hello"
    }
});
msService.EditTime(msWorkspace, 1, 1_726_500_500);
msService.AddMessage(msWorkspace, MessageKind.Text, "recovered in ms");
var msResult = await new WorkspaceDatabaseWriter().ApplyAsync(millisecondsDb, msWorkspace);
Assert(msResult.Updated == 1 && msResult.Inserted == 1, "millisecond fixture write mismatch");
await using (var verifyMs = new SqliteConnection(
    $"Data Source={millisecondsDb};Mode=ReadOnly;Pooling=False"))
{
    await verifyMs.OpenAsync();
    await using var verifyCommand = verifyMs.CreateCommand();
    verifyCommand.CommandText = "SELECT min(createTime), max(createTime), count(*) FROM message";
    await using var reader2 = await verifyCommand.ExecuteReaderAsync();
    Assert(await reader2.ReadAsync(), "millisecond verification failed");
    Assert(reader2.GetInt64(0) == 1_726_500_500_000L,
        "edited time must be written in milliseconds, not seconds");
    Assert(reader2.GetInt64(1) >= 1_726_500_500_000L,
        "inserted time must be written in milliseconds, not seconds");
    Assert(reader2.GetInt64(2) == 3, "millisecond fixture row count mismatch");
}
Assert(WorkspaceDatabaseWriter.MapRawType(MessageKind.Image) == 3, "image raw type mismatch");
Assert(WorkspaceDatabaseWriter.MapRawType(MessageKind.RedPacket) == 49, "red packet raw type mismatch");

var writtenRows = await new WeChatDatabaseReader().LoadMessagesAsync(
    writeDbPath, "alice", new DatabaseOpenOptions { ReadOnly = true });
Assert(writtenRows.Any(x => x.Content == "recovered hello"), "content edit was not written");
using (var checkConnection = new SqliteConnection(
    $"Data Source={writeDbPath};Mode=ReadOnly;Pooling=False"))
{
    await checkConnection.OpenAsync();
    await using var check = checkConnection.CreateCommand();
    check.CommandText = "SELECT msgSvrId FROM message WHERE msgId=1";
    Assert(Convert.ToInt64(await check.ExecuteScalarAsync()) == 0,
        "edited rows must have their server id cleared so WeChat keeps the edit");
}
Assert(writtenRows.Any(x => x.Content == "recovered new row" && x.IsOutgoing),
    "recovered row was not written as an outgoing message");
Assert(writtenRows.Any(x => x.Content == "recovered incoming row"), "incoming recovered row missing");
Assert(writtenRows.All(x => x.Content != "world"), "deleted row must be gone from the database");

// Edits must survive leaving and re-entering a conversation.
var savedRoot = Path.Combine(root, "workspace-store");
await writeService.SaveAsync(writeWorkspace, savedRoot);
var reloadedWorkspace = await writeService.FindSavedAsync(
    savedRoot, root, writeWorkspace.ConversationId);
Assert(reloadedWorkspace is not null, "saved edits for a conversation were not found");
Assert(reloadedWorkspace!.Messages.Any(x => x.Content == "recovered hello"),
    "content edit did not survive a reload");
Assert(reloadedWorkspace.Messages.Any(x => x.IsDeleted),
    "deleted flag did not survive a reload");
Assert(reloadedWorkspace.Messages.Count(x => x.IsNew) == 2,
    "recovered rows did not survive a reload");

var sc1Service = new LegacySc1DatabaseService();
var saltSource = Path.Combine(root, "salt-source.bin");
var saltBytes = new byte[1024];
for (var i = 0; i < 16; i++) saltBytes[i] = (byte)(i + 1);
await File.WriteAllBytesAsync(saltSource, saltBytes);
var encryptedPath = Path.Combine(root, "write-target.enc.db");
var decryptedPath = Path.Combine(root, "write-target.roundtrip.db");
await sc1Service.EncryptWithPasswordAsync(writeDbPath, saltSource, "smoke-passphrase", encryptedPath);
Assert(new FileInfo(encryptedPath).Length == new FileInfo(writeDbPath).Length,
    "encrypted image size mismatch");
await sc1Service.DecryptWithPasswordAsync(encryptedPath, "smoke-passphrase", decryptedPath, foldWal: false);
Assert(await LegacySc1DatabaseService.PayloadsMatchAsync(writeDbPath, decryptedPath),
    "re-encrypted database did not decrypt back to the edited image");

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
