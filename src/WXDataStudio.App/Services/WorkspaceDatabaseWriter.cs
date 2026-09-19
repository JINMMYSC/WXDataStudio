using Microsoft.Data.Sqlite;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed record WorkspaceWriteResult(
    string DatabasePath,
    int Updated,
    int Inserted,
    int Deleted,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Applies a workspace document to a plaintext <c>EnMicroMsg.db</c> image.
/// Only the columns a workspace can change are touched: content, createTime and
/// imgPath for existing rows, and a full row for messages the user added while
/// recovering deleted history.
/// </summary>
public sealed class WorkspaceDatabaseWriter
{
    private static readonly string[] MessageTableNames = { "message", "Message", "msg" };
    private static readonly string[] TalkerColumns = { "talker", "username", "conversationId" };
    private static readonly string[] IdColumns = { "msgId", "localId", "msgLocalId" };
    private static readonly string[] ContentColumns = { "content", "body", "text" };
    private static readonly string[] TimeColumns = { "createTime", "time", "timestamp" };
    private static readonly string[] ImageColumns = { "imgPath", "imagePath", "mediaPath" };
    private static readonly string[] TypeColumns = { "type", "msgType" };
    private static readonly string[] SendColumns = { "isSend", "isOutgoing" };
    private static readonly string[] StatusColumns = { "status", "msgStatus" };
    private static readonly string[] SequenceColumns = { "msgSeq", "sequence", "seq" };
    private static readonly string[] ServerIdColumns = { "msgSvrId", "serverId", "msgServerId" };

    public async Task<WorkspaceWriteResult> ApplyAsync(
        string databasePath,
        WorkspaceDocument workspace,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var table = await ResolveMessageTableAsync(connection, cancellationToken)
            ?? throw new InvalidOperationException(
                "The plaintext database has no recognizable message table.");
        var columns = await GetColumnsAsync(connection, table, cancellationToken);

        var idColumn = Pick(columns, IdColumns)
            ?? throw new InvalidOperationException("Message table has no id column.");
        var contentColumn = Pick(columns, ContentColumns);
        var timeColumn = Pick(columns, TimeColumns);
        var imageColumn = Pick(columns, ImageColumns);
        var talkerColumn = Pick(columns, TalkerColumns);
        var typeColumn = Pick(columns, TypeColumns);
        var sendColumn = Pick(columns, SendColumns);
        var statusColumn = Pick(columns, StatusColumns);
        var sequenceColumn = Pick(columns, SequenceColumns);
        var serverIdColumn = Pick(columns, ServerIdColumns);

        var updated = 0;
        var inserted = 0;
        var deleted = 0;
        var existing = workspace.Messages.Where(x => !x.IsNew).ToArray();
        foreach (var message in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.IsDeleted)
            {
                await using var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText =
                    $"DELETE FROM {Q(table)} WHERE {Q(idColumn)}=$id";
                deleteCommand.Parameters.AddWithValue("$id", message.LocalId);
                deleted += await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }

            var assignments = new List<string>();
            await using var command = connection.CreateCommand();
            if (contentColumn is not null && message.Content != message.OriginalContent)
            {
                assignments.Add($"{Q(contentColumn)}=$content");
                command.Parameters.AddWithValue("$content", message.Content);
            }
            if (timeColumn is not null && message.CreateTime != message.OriginalCreateTime)
            {
                assignments.Add($"{Q(timeColumn)}=$time");
                command.Parameters.AddWithValue("$time", message.CreateTime);
            }
            if (imageColumn is not null &&
                !string.Equals(message.Attachment, message.OriginalAttachment, StringComparison.Ordinal))
            {
                assignments.Add($"{Q(imageColumn)}=$image");
                command.Parameters.AddWithValue("$image", (object?)message.Attachment ?? DBNull.Value);
            }
            if (assignments.Count == 0) continue;

            // WeChat re-syncs messages that carry a server id and would overwrite
            // our edit with the server's copy. Clearing the id on this row makes
            // WeChat treat the message as locally owned, so the edit sticks.
            if (serverIdColumn is not null)
                assignments.Add($"{Q(serverIdColumn)}=0");

            command.CommandText =
                $"UPDATE {Q(table)} SET {string.Join(',', assignments)} WHERE {Q(idColumn)}=$id";
            command.Parameters.AddWithValue("$id", message.LocalId);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
                warnings.Add($"Message {message.LocalId} was not found in the database.");
            else
                updated += affected;
        }

        var newMessages = workspace.Messages.Where(x => x.IsNew).ToArray();
        if (newMessages.Length > 0 && talkerColumn is null)
            throw new InvalidOperationException("Message table has no talker column; cannot insert.");

        var nextId = await NextValueAsync(connection, table, idColumn, cancellationToken);
        var nextSequence = sequenceColumn is null
            ? 0L
            : await NextValueAsync(connection, table, sequenceColumn, cancellationToken);
        var required = columns
            .Where(x => x.NotNull && x.Default is null && x.PrimaryKey == 0)
            .Where(x => !IsManagedColumn(x.Name))
            .ToArray();

        foreach (var message in newMessages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            var names = new List<string> { idColumn };
            var values = new List<string> { "$id" };
            command.Parameters.AddWithValue("$id", nextId);
            nextId++;

            void Add(string? column, string parameter, object? value)
            {
                if (column is null) return;
                names.Add(column);
                values.Add(parameter);
                command.Parameters.AddWithValue(parameter, value ?? DBNull.Value);
            }

            Add(talkerColumn, "$talker", workspace.ConversationId);
            Add(sendColumn, "$send", 1);
            Add(typeColumn, "$type", MapRawType(message.Kind));
            Add(statusColumn, "$status", 3);
            Add(timeColumn, "$time", message.CreateTime);
            Add(sequenceColumn, "$seq", nextSequence++);
            Add(contentColumn, "$content", message.Content);
            Add(imageColumn, "$image", message.Attachment);
            Add(serverIdColumn, "$server", 0);

            // Some WeChat schema versions carry NOT NULL columns without defaults.
            foreach (var column in required)
            {
                names.Add(column.Name);
                values.Add("$" + column.Name);
                command.Parameters.AddWithValue("$" + column.Name, DefaultValueFor(column));
            }

            command.CommandText =
                $"INSERT INTO {Q(table)} ({string.Join(',', names.Select(Q))}) " +
                $"VALUES ({string.Join(',', values)})";
            inserted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new WorkspaceWriteResult(databasePath, updated, inserted, deleted, warnings);
    }

    /// <summary>Runs SQLite's integrity check on the written database.</summary>
    public static async Task<string> IntegrityCheckAsync(
        string databasePath, CancellationToken cancellationToken = default)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "";
    }

    /// <summary>Message class to WeChat raw type used for inserts.</summary>
    public static int MapRawType(MessageKind kind) => kind switch
    {
        MessageKind.Text => 1,
        MessageKind.Image => 3,
        MessageKind.Voice => 34,
        MessageKind.Video => 43,
        MessageKind.Emoji => 47,
        MessageKind.Location => 48,
        MessageKind.ContactCard => 42,
        MessageKind.System => 10000,
        MessageKind.File or MessageKind.Link or MessageKind.MiniProgram or
            MessageKind.Quote or MessageKind.Transfer or MessageKind.RedPacket or
            MessageKind.Payment => 49,
        _ => 1
    };

    private static bool IsManagedColumn(string name) =>
        IdColumns.Concat(ContentColumns).Concat(TimeColumns).Concat(ImageColumns)
            .Concat(TypeColumns).Concat(SendColumns).Concat(StatusColumns)
            .Concat(SequenceColumns).Concat(ServerIdColumns).Concat(TalkerColumns)
            .Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    private static object DefaultValueFor(ColumnInfo column)
    {
        var type = column.Type.ToUpperInvariant();
        if (type.Contains("BLOB")) return Array.Empty<byte>();
        if (type.Contains("INT") || type.Contains("REAL") || type.Contains("NUM")) return 0L;
        return "";
    }

    private static async Task<long> NextValueAsync(
        SqliteConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT ifnull(max({Q(column)}),0)+1 FROM {Q(table)}";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 1L : Convert.ToInt64(value);
    }

    private static async Task<string?> ResolveMessageTableAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        }

        foreach (var candidate in MessageTableNames)
        {
            var match = tables.FirstOrDefault(
                x => string.Equals(x, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        foreach (var table in tables)
        {
            var columns = await GetColumnsAsync(connection, table, cancellationToken);
            if (Pick(columns, TalkerColumns) is not null && Pick(columns, ContentColumns) is not null)
                return table;
        }

        return null;
    }

    private static async Task<List<ColumnInfo>> GetColumnsAsync(
        SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        var columns = new List<ColumnInfo>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Q(table)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new ColumnInfo(
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5)));
        }
        return columns;
    }

    private static string? Pick(IReadOnlyList<ColumnInfo> columns, IEnumerable<string> names)
    {
        var available = columns.Select(x => x.Name).ToArray();
        foreach (var name in names)
        {
            var match = available.FirstOrDefault(
                x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private sealed record ColumnInfo(
        string Name, string Type, bool NotNull, string? Default, int PrimaryKey);
}
