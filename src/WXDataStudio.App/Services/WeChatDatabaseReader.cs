using Microsoft.Data.Sqlite;
using SQLitePCL;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

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
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(reader.GetString(0));
        return list;
    }

    public async Task<IReadOnlyList<ConversationItem>> LoadConversationsAsync(
        string path, DatabaseOpenOptions? options = null, int limit = 500)
    {
        await using var connection = await OpenAsync(path, options);
        if (!await TableExistsAsync(connection, "rconversation"))
            throw new InvalidOperationException("rconversation table was not found.");
        var columns = await GetColumnsAsync(connection, "rconversation");
        var contactExists = await TableExistsAsync(connection, "rcontact");
        return await QueryConversationsAsync(connection, columns, contactExists, limit);
    }

    public async Task<IReadOnlyList<WeChatMessage>> LoadMessagesAsync(
        string path, string talker, DatabaseOpenOptions? options = null, int limit = 1000)
    {
        await using var connection = await OpenAsync(path, options);
        if (!await TableExistsAsync(connection, "message"))
            throw new InvalidOperationException("message table was not found.");
        var columns = await GetColumnsAsync(connection, "message");
        if (!columns.Contains("talker"))
            throw new InvalidOperationException("message.talker column was not found.");

        var fields = new[]
        {
            Pick(columns, "msgId", "0"), Pick(columns, "msgSvrId", "NULL"),
            Pick(columns, "talker", "''"), Pick(columns, "isSend", "0"),
            Pick(columns, "type", "0"), Pick(columns, "status", "0"),
            Pick(columns, "createTime", "0"), Pick(columns, "msgSeq", "0"),
            Pick(columns, "content", "''"), Pick(columns, "imgPath", "NULL"),
            Pick(columns, "reserved", "NULL")
        };
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(',', fields)} FROM message " +
                          "WHERE talker=$talker ORDER BY createTime DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$talker", talker);
        cmd.Parameters.AddWithValue("$limit", limit);
        var rows = new List<WeChatMessage>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(ReadMessage(r));
        rows.Reverse();
        return rows;
    }

    private static async Task<IReadOnlyList<ConversationItem>> QueryConversationsAsync(
        SqliteConnection c, HashSet<string> cols, bool contactExists, int limit)
    {
        var time = cols.Contains("conversationTime") ? "cv.[conversationTime]" : "0";
        var content = cols.Contains("content") ? "cv.[content]" : "''";
        var user = cols.Contains("username") ? "cv.[username]" : "''";
        var join = contactExists ? "LEFT JOIN rcontact rc ON rc.username=cv.username" : "";
        var remark = contactExists ? "COALESCE(rc.conRemark,'')" : "''";
        var nick = contactExists ? "COALESCE(rc.nickname,'')" : "''";
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {user}, {remark}, {nick}, {content}, {time} " +
                          $"FROM rconversation cv {join} ORDER BY {time} DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var items = new List<ConversationItem>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var username = Text(r, 0);
            if (string.IsNullOrWhiteSpace(username)) continue;
            items.Add(new ConversationItem
            {
                Username = username,
                Remark = Text(r, 1),
                NickName = Text(r, 2),
                LastContent = Text(r, 3),
                LastTime = Int64(r, 4)
            });
        }
        return items;
    }

    private static WeChatMessage ReadMessage(SqliteDataReader r)
    {
        var rawType = Int32(r, 4);
        var content = Text(r, 8);
        return new WeChatMessage
        {
            LocalId = Int64(r, 0),
            ServerId = r.IsDBNull(1) ? null : Int64(r, 1),
            ConversationId = Text(r, 2),
            IsOutgoing = Int32(r, 3) != 0,
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
            Mode = options.ReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite
        };
        if (!string.IsNullOrWhiteSpace(options.Password))
            builder.Password = options.Password;

        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync();
        if (!string.IsNullOrWhiteSpace(options.Password))
        {
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA cipher_compatibility={options.CipherCompatibility};";
            await pragma.ExecuteNonQueryAsync();
        }
        return connection;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection c, string name)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", name);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task<HashSet<string>> GetColumnsAsync(SqliteConnection c, string table)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info([{table.Replace("]", "]]", StringComparison.Ordinal)}])";
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(1));
        return set;
    }

    private static string Pick(HashSet<string> columns, string name, string fallback) =>
        columns.Contains(name) ? $"[{name}]" : fallback;

    private static string Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)) ?? "";
    private static string? NullText(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
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
}
