using Microsoft.Data.Sqlite;
using SQLitePCL;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed record WeChatSchemaInfo(
    string? MessageTable,
    string? ConversationTable,
    string? ContactTable,
    bool UsesConversationFallback);

public sealed class WeChatDatabaseReader
{
    private static int _initialized;

    public WeChatDatabaseReader()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
            Batteries_V2.Init();
    }

    public async Task<IReadOnlyList<string>> ListTablesAsync(
        string path, DatabaseOpenOptions? options = null)
    {
        await using var connection = await OpenAsync(path, options);
        return (await GetTableInfosAsync(connection)).Select(x => x.Name).OrderBy(x => x).ToArray();
    }

    public async Task<WeChatSchemaInfo> DetectSchemaAsync(
        string path, DatabaseOpenOptions? options = null)
    {
        await using var connection = await OpenAsync(path, options);
        var schema = await DetectSchemaOnConnectionAsync(connection);
        return new WeChatSchemaInfo(
            schema.Message?.Name,
            schema.Conversation?.Name,
            schema.Contact?.Name,
            schema.Conversation is null && schema.Message is not null);
    }

    public async Task<IReadOnlyList<ConversationItem>> LoadConversationsAsync(
        string path, DatabaseOpenOptions? options = null, int limit = 500)
    {
        await using var connection = await OpenAsync(path, options);
        var schema = await DetectSchemaOnConnectionAsync(connection);

        if (schema.Conversation is not null)
            return await QueryConversationsAsync(
                connection, schema.Conversation, schema.Contact, limit);

        if (schema.Message is not null)
            return await QueryConversationsFromMessagesAsync(
                connection, schema.Message, schema.Contact, limit);

        throw new InvalidOperationException(
            "No compatible conversation or message table could be detected.");
    }

    public async Task<IReadOnlyList<WeChatMessage>> LoadMessagesAsync(
        string path, string talker, DatabaseOpenOptions? options = null, int limit = 1000)
    {
        await using var connection = await OpenAsync(path, options);
        var schema = await DetectSchemaOnConnectionAsync(connection);
        var message = schema.Message
            ?? throw new InvalidOperationException("No compatible message table could be detected.");

        var talkerColumn = FindColumn(message.Columns, "talker", "username", "conversationId")
            ?? throw new InvalidOperationException(
                $"Message table [{message.Name}] has no recognized talker column.");

        var fields = new[]
        {
            Pick(message.Columns, "0", "msgId", "localId", "msgLocalId"),
            Pick(message.Columns, "NULL", "msgSvrId", "serverId", "msgServerId"),
            Q(talkerColumn),
            Pick(message.Columns, "0", "isSend", "isOutgoing"),
            Pick(message.Columns, "0", "type", "msgType"),
            Pick(message.Columns, "0", "status", "msgStatus"),
            Pick(message.Columns, "0", "createTime", "time", "timestamp"),
            Pick(message.Columns, "0", "msgSeq", "sequence", "seq"),
            Pick(message.Columns, "''", "content", "body", "text"),
            Pick(message.Columns, "NULL", "imgPath", "imagePath", "mediaPath"),
            Pick(message.Columns, "NULL", "reserved", "lvbuffer", "extra")
        };

        var timeColumn = FindColumn(message.Columns, "createTime", "time", "timestamp");
        var order = timeColumn is null ? "1" : Q(timeColumn);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(',', fields)} FROM {Q(message.Name)} " +
                          $"WHERE {Q(talkerColumn)}=$talker ORDER BY {order} DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$talker", talker);
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<WeChatMessage>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(ReadMessage(r));
        rows.Reverse();
        return rows;
    }

    private static async Task<IReadOnlyList<ConversationItem>> QueryConversationsAsync(
        SqliteConnection c, TableInfo conversation, TableInfo? contact, int limit)
    {
        var userColumn = FindColumn(conversation.Columns, "username", "talker", "conversationId");
        if (userColumn is null)
            throw new InvalidOperationException(
                $"Conversation table [{conversation.Name}] has no recognized username column.");

        var timeColumn = FindColumn(conversation.Columns,
            "conversationTime", "lastTime", "createTime", "timestamp");
        var contentColumn = FindColumn(conversation.Columns,
            "content", "digest", "lastContent");

        var contactUser = contact is null
            ? null
            : FindColumn(contact.Columns, "username", "talker", "userName");
        var remarkColumn = contact is null
            ? null
            : FindColumn(contact.Columns, "conRemark", "remark", "displayName");
        var nickColumn = contact is null
            ? null
            : FindColumn(contact.Columns, "nickname", "nickName", "name");

        var join = contact is not null && contactUser is not null
            ? $"LEFT JOIN {Q(contact.Name)} rc ON rc.{Q(contactUser)}=cv.{Q(userColumn)}"
            : "";
        var remark = remarkColumn is not null && join.Length > 0
            ? $"COALESCE(rc.{Q(remarkColumn)},'')"
            : "''";
        var nick = nickColumn is not null && join.Length > 0
            ? $"COALESCE(rc.{Q(nickColumn)},'')"
            : "''";
        var content = contentColumn is null ? "''" : $"cv.{Q(contentColumn)}";
        var time = timeColumn is null ? "0" : $"cv.{Q(timeColumn)}";

        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            $"SELECT cv.{Q(userColumn)}, {remark}, {nick}, {content}, {time} " +
            $"FROM {Q(conversation.Name)} cv {join} ORDER BY {time} DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var items = new List<ConversationItem>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            AddConversation(items, r);
        return items;
    }

    private static async Task<IReadOnlyList<ConversationItem>> QueryConversationsFromMessagesAsync(
        SqliteConnection c, TableInfo message, TableInfo? contact, int limit)
    {
        var talkerColumn = FindColumn(message.Columns, "talker", "username", "conversationId")
            ?? throw new InvalidOperationException(
                $"Message table [{message.Name}] has no recognized talker column.");
        var timeColumn = FindColumn(message.Columns, "createTime", "time", "timestamp");

        var contactUser = contact is null
            ? null
            : FindColumn(contact.Columns, "username", "talker", "userName");
        var remarkColumn = contact is null
            ? null
            : FindColumn(contact.Columns, "conRemark", "remark", "displayName");
        var nickColumn = contact is null
            ? null
            : FindColumn(contact.Columns, "nickname", "nickName", "name");

        var join = contact is not null && contactUser is not null
            ? $"LEFT JOIN {Q(contact.Name)} rc ON rc.{Q(contactUser)}=m.{Q(talkerColumn)}"
            : "";
        var remark = remarkColumn is not null && join.Length > 0
            ? $"COALESCE(MAX(rc.{Q(remarkColumn)}),'')"
            : "''";
        var nick = nickColumn is not null && join.Length > 0
            ? $"COALESCE(MAX(rc.{Q(nickColumn)}),'')"
            : "''";
        var time = timeColumn is null ? "0" : $"MAX(m.{Q(timeColumn)})";

        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            $"SELECT m.{Q(talkerColumn)}, {remark}, {nick}, '', {time} " +
            $"FROM {Q(message.Name)} m {join} " +
            $"WHERE m.{Q(talkerColumn)} IS NOT NULL AND m.{Q(talkerColumn)}<>'' " +
            $"GROUP BY m.{Q(talkerColumn)} ORDER BY {time} DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var items = new List<ConversationItem>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            AddConversation(items, r);
        return items;
    }

    private static void AddConversation(List<ConversationItem> items, SqliteDataReader r)
    {
        var username = Text(r, 0);
        if (string.IsNullOrWhiteSpace(username)) return;
        items.Add(new ConversationItem
        {
            Username = username,
            Remark = Text(r, 1),
            NickName = Text(r, 2),
            LastContent = Text(r, 3),
            LastTime = Int64(r, 4)
        });
    }

    private static WeChatMessage ReadMessage(SqliteDataReader r)
    {
        var rawType = Int32(r, 4);
        var content = Text(r, 8);
        var conversationId = Text(r, 2);
        var isOutgoing = Int32(r, 3) != 0;
        var group = !isOutgoing && conversationId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase)
            ? GroupMessageParser.Parse(content)
            : new GroupMessageEnvelope("", content);

        return new WeChatMessage
        {
            LocalId = Int64(r, 0),
            ServerId = r.IsDBNull(1) ? null : Int64(r, 1),
            ConversationId = conversationId,
            Sender = group.Sender,
            IsOutgoing = isOutgoing,
            RawType = rawType,
            RawStatus = Int32(r, 5),
            CreateTime = NormalizeUnixTime(Int64(r, 6)),
            Sequence = Int64(r, 7),
            Content = content,
            ImgPath = NullText(r, 9),
            Reserved = NullText(r, 10),
            Kind = MessageTypeClassifier.Classify(rawType, content)
        };
    }

    private static long NormalizeUnixTime(long value)
    {
        if (value > 10_000_000_000L) return value / 1000;
        return Math.Max(0, value);
    }

    private static async Task<SqliteConnection> OpenAsync(
        string path, DatabaseOpenOptions? options)
    {
        options ??= new DatabaseOpenOptions();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = options.ReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync();
        if (!string.IsNullOrWhiteSpace(options.Password) || !string.IsNullOrWhiteSpace(options.RawKeyHex))
        {
            var keySql = !string.IsNullOrWhiteSpace(options.RawKeyHex)
                ? $"PRAGMA key=\"x'{NormalizeHex(options.RawKeyHex)}'\";"
                : $"PRAGMA key='{options.Password!.Replace("'", "''", StringComparison.Ordinal)}';";
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = options.UseLegacyWeChatCipher
                ? keySql + "PRAGMA cipher_use_hmac=OFF;PRAGMA cipher_page_size=1024;PRAGMA kdf_iter=4000;"
                : $"PRAGMA cipher_compatibility={options.CipherCompatibility};" + keySql;
            await pragma.ExecuteNonQueryAsync();
        }
        return connection;
    }

    private static string NormalizeHex(string value)
    {
        var hex = new string(value.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (hex.Length == 0 || hex.Length % 2 != 0)
            throw new ArgumentException(
                "Raw key must contain an even number of hexadecimal characters.");
        return hex;
    }

    private static async Task<SchemaProfile> DetectSchemaOnConnectionAsync(SqliteConnection connection)
    {
        var tables = await GetTableInfosAsync(connection);

        var message = tables.FirstOrDefault(x =>
                          x.Name.Equals("message", StringComparison.OrdinalIgnoreCase))
                      ?? tables.FirstOrDefault(x =>
                          HasAny(x.Columns, "talker", "username", "conversationId") &&
                          HasAny(x.Columns, "content", "body", "text") &&
                          HasAny(x.Columns, "type", "msgType") &&
                          HasAny(x.Columns, "createTime", "time", "timestamp"));

        var conversation = tables.FirstOrDefault(x =>
                               x.Name.Equals("rconversation", StringComparison.OrdinalIgnoreCase))
                           ?? tables.FirstOrDefault(x =>
                               HasAny(x.Columns, "username", "talker", "conversationId") &&
                               HasAny(x.Columns, "content", "digest", "lastContent") &&
                               HasAny(x.Columns,
                                   "conversationTime", "lastTime", "createTime", "timestamp"));

        var contact = tables.FirstOrDefault(x =>
                          x.Name.Equals("rcontact", StringComparison.OrdinalIgnoreCase))
                      ?? tables.FirstOrDefault(x =>
                          HasAny(x.Columns, "username", "talker", "userName") &&
                          HasAny(x.Columns, "nickname", "nickName", "conRemark", "remark"));

        return new SchemaProfile(message, conversation, contact);
    }

    private static async Task<IReadOnlyList<TableInfo>> GetTableInfosAsync(SqliteConnection c)
    {
        var names = new List<string>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) names.Add(r.GetString(0));
        }

        var tables = new List<TableInfo>();
        foreach (var name in names)
            tables.Add(new TableInfo(name, await GetColumnsAsync(c, name)));
        return tables;
    }

    private static async Task<HashSet<string>> GetColumnsAsync(SqliteConnection c, string table)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Q(table)})";
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(1));
        return set;
    }

    private static bool HasAny(HashSet<string> columns, params string[] names) =>
        names.Any(columns.Contains);

    private static string? FindColumn(HashSet<string> columns, params string[] names) =>
        names.FirstOrDefault(columns.Contains);

    private static string Pick(HashSet<string> columns, string fallback, params string[] names)
    {
        var column = FindColumn(columns, names);
        return column is null ? fallback : Q(column);
    }

    private static string Q(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string Text(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)) ?? "";

    private static string? NullText(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));

    private static long Int64(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return 0;
        var value = r.GetValue(i);
        return value switch
        {
            long x => x,
            int x => x,
            short x => x,
            byte x => x,
            _ => long.TryParse(Convert.ToString(value), out var parsed) ? parsed : 0
        };
    }

    private static int Int32(SqliteDataReader r, int i) => unchecked((int)Int64(r, i));

    private sealed record TableInfo(string Name, HashSet<string> Columns);
    private sealed record SchemaProfile(
        TableInfo? Message,
        TableInfo? Conversation,
        TableInfo? Contact);
}
