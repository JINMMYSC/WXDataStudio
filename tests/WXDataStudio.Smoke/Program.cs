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
workspaceService.EditContent(workspace, 1, "edited hello");
workspaceService.EditTime(workspace, 1, messages[0].CreateTime + 60);
Assert(workspace.Audit.Count == 2, "workspace audit mismatch");

var diffService = new WorkspaceDiffService();
var diffs = diffService.GetDiffs(workspace);
Assert(diffs.Count >= 2, "workspace diff mismatch");

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
