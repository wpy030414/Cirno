using System.Globalization;
using Cirno.Agents.Abstractions;
using Cirno.Core.Models;
using Microsoft.Data.Sqlite;

namespace Cirno.Core.Persistence;

/// <summary>
/// SQLite 归一化存储：会话元数据 + 条目副本。
/// 后端历史仍是恢复真相源，这里只是渲染与检索用的本地索引（见 ADR-104）。
/// 单连接 + 锁：写量小（条目级），换取实现简单与顺序确定。
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    /// <summary>各会话的下一个顺序号。避免每条写入都查 MAX(seq)（那是 O(n²)）。</summary>
    private readonly Dictionary<string, long> _nextSeq = new(StringComparer.Ordinal);

    private bool _disposed;

    public SessionStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        Initialize();
    }

    private void Initialize()
    {
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA busy_timeout=3000;");

        Execute("""
            CREATE TABLE IF NOT EXISTS sessions (
                id                  TEXT PRIMARY KEY,
                provider_id         TEXT NOT NULL,
                model               TEXT NULL,
                working_directory   TEXT NOT NULL,
                title               TEXT NULL,
                backend_session_id  TEXT NULL,
                created_at          TEXT NOT NULL,
                updated_at          TEXT NOT NULL
            );
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS items (
                session_id  TEXT NOT NULL,
                item_id     TEXT NOT NULL,
                seq         INTEGER NOT NULL,
                kind        INTEGER NOT NULL,
                text        TEXT NULL,
                detail      TEXT NULL,
                tool_name   TEXT NULL,
                tool_use_id TEXT NULL,
                is_error    INTEGER NOT NULL DEFAULT 0,
                timestamp   TEXT NOT NULL,
                PRIMARY KEY (session_id, item_id)
            );
            """);

        Execute("CREATE INDEX IF NOT EXISTS ix_items_session_seq ON items(session_id, seq);");
    }

    public IReadOnlyList<SessionRecord> ListSessions()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, provider_id, model, working_directory, title, backend_session_id, created_at, updated_at
                FROM sessions
                ORDER BY updated_at DESC;
                """;

            var list = new List<SessionRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadSession(reader));
            }

            return list;
        }
    }

    public SessionRecord? GetSession(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, provider_id, model, working_directory, title, backend_session_id, created_at, updated_at
                FROM sessions WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSession(reader) : null;
        }
    }

    public void UpsertSession(SessionRecord record)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sessions (id, provider_id, model, working_directory, title, backend_session_id, created_at, updated_at)
                VALUES ($id, $provider, $model, $cwd, $title, $backend, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    model = excluded.model,
                    title = excluded.title,
                    backend_session_id = excluded.backend_session_id,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$provider", record.ProviderId);
            command.Parameters.AddWithValue("$model", (object?)record.Model ?? DBNull.Value);
            command.Parameters.AddWithValue("$cwd", record.WorkingDirectory);
            command.Parameters.AddWithValue("$title", (object?)record.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("$backend", (object?)record.BackendSessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", Format(record.CreatedAt));
            command.Parameters.AddWithValue("$updated", Format(record.UpdatedAt));
            command.ExecuteNonQuery();
        }
    }

    public void DeleteSession(string id)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var sql in new[] { "DELETE FROM items WHERE session_id = $id;", "DELETE FROM sessions WHERE id = $id;" })
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // 序号缓存作废：会话已删，下次写入要从头取号
        lock (_gate)
        {
            _nextSeq.Remove(id);
        }
    }

    /// <summary>追加或覆盖一个条目（同 id 幂等），返回其顺序号。</summary>
    public long AppendItem(string sessionId, AgentItem item)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();

            // 单条语句完成「取号 + 写入」：seq 在内存里递增，避免每条多跑一次
            // MAX(seq) 查询——流式输出时这是热路径。
            command.CommandText = """
                INSERT INTO items (session_id, item_id, seq, kind, text, detail, tool_name, tool_use_id, is_error, timestamp)
                VALUES ($session, $item, $seq, $kind, $text, $detail, $tool, $toolUse, $error, $ts)
                ON CONFLICT(session_id, item_id) DO UPDATE SET
                    text = excluded.text,
                    detail = excluded.detail,
                    is_error = excluded.is_error
                RETURNING seq;
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$item", item.Id);
            command.Parameters.AddWithValue("$seq", NextSeq(sessionId));
            command.Parameters.AddWithValue("$kind", (int)item.Kind);
            command.Parameters.AddWithValue("$text", (object?)item.Text ?? DBNull.Value);
            command.Parameters.AddWithValue("$detail", (object?)item.Detail ?? DBNull.Value);
            command.Parameters.AddWithValue("$tool", (object?)item.ToolName ?? DBNull.Value);
            command.Parameters.AddWithValue("$toolUse", (object?)item.ToolUseId ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", item.IsError ? 1 : 0);
            command.Parameters.AddWithValue("$ts", Format(item.Timestamp));

            var result = command.ExecuteScalar();
            return result is long seq ? seq : 0;
        }
    }

    /// <summary>
    /// 批量追加条目（单事务提交）。
    /// <para>
    /// 流式输出时逐条 <see cref="AppendItem"/> 就够——写量小、延迟低。
    /// 但导入/迁移/压测这类一次性写入，逐条提交会被事务开销拖垮，
    /// 这里用一个事务包住整批。
    /// </para>
    /// </summary>
    public IReadOnlyList<long> AppendItems(string sessionId, IEnumerable<AgentItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var sequences = new List<long>();
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var item in items)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO items (session_id, item_id, seq, kind, text, detail, tool_name, tool_use_id, is_error, timestamp)
                        VALUES ($session, $item, $seq, $kind, $text, $detail, $tool, $toolUse, $error, $ts)
                        ON CONFLICT(session_id, item_id) DO UPDATE SET
                            text = excluded.text,
                            detail = excluded.detail,
                            is_error = excluded.is_error
                        RETURNING seq;
                        """;
                    command.Parameters.AddWithValue("$session", sessionId);
                    command.Parameters.AddWithValue("$item", item.Id);
                    command.Parameters.AddWithValue("$seq", NextSeq(sessionId));
                command.Parameters.AddWithValue("$kind", (int)item.Kind);
                command.Parameters.AddWithValue("$text", (object?)item.Text ?? DBNull.Value);
                command.Parameters.AddWithValue("$detail", (object?)item.Detail ?? DBNull.Value);
                command.Parameters.AddWithValue("$tool", (object?)item.ToolName ?? DBNull.Value);
                command.Parameters.AddWithValue("$toolUse", (object?)item.ToolUseId ?? DBNull.Value);
                command.Parameters.AddWithValue("$error", item.IsError ? 1 : 0);
                command.Parameters.AddWithValue("$ts", Format(item.Timestamp));

                var result = command.ExecuteScalar();
                sequences.Add(result is long seq ? seq : 0);
            }

            transaction.Commit();
        }

        return sequences;
    }

    public IReadOnlyList<AgentItem> LoadItems(string sessionId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT item_id, kind, text, detail, tool_name, tool_use_id, is_error, timestamp
                FROM items WHERE session_id = $session ORDER BY seq;
                """;
            command.Parameters.AddWithValue("$session", sessionId);

            var list = new List<AgentItem>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new AgentItem
                {
                    Id = reader.GetString(0),
                    Kind = (AgentItemKind)reader.GetInt32(1),
                    Text = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Detail = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ToolName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ToolUseId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    IsError = reader.GetInt32(6) != 0,
                    Timestamp = Parse(reader.GetString(7)),
                });
            }

            return list;
        }
    }

    /// <summary>读取某会话的条目顺序号（升序）。用于验证时间线顺序的连续性。</summary>
    public IReadOnlyList<long> LoadItemSequences(string sessionId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT seq FROM items WHERE session_id = $session ORDER BY seq;";
            command.Parameters.AddWithValue("$session", sessionId);

            var list = new List<long>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetInt64(0));
            }

            return list;
        }
    }

    /// <summary>
    /// 把 WAL 日志落回主库。应用退出时调用，避免下次打开要先回放大量 WAL。
    /// </summary>
    public void Checkpoint()
    {
        lock (_gate)
        {
            Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        }
    }

    public void TouchSession(string sessionId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET updated_at = $updated WHERE id = $id;";
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$updated", Format(DateTimeOffset.Now));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 取会话的下一个顺序号。
    /// 首次访问时查一次库定基准，之后在内存里递增——每条写入都查 MAX(seq) 是 O(n²)。
    /// </summary>
    private long NextSeq(string sessionId)
    {
        if (_nextSeq.TryGetValue(sessionId, out var next))
        {
            _nextSeq[sessionId] = next + 1;
            return next;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) + 1 FROM items WHERE session_id = $session;";
        command.Parameters.AddWithValue("$session", sessionId);
        var seq = Convert.ToInt64(command.ExecuteScalar() ?? 1L, CultureInfo.InvariantCulture);
        _nextSeq[sessionId] = seq + 1;
        return seq;
    }

    private static SessionRecord ReadSession(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ProviderId = reader.GetString(1),
        Model = reader.IsDBNull(2) ? null : reader.GetString(2),
        WorkingDirectory = reader.GetString(3),
        Title = reader.IsDBNull(4) ? null : reader.GetString(4),
        BackendSessionId = reader.IsDBNull(5) ? null : reader.GetString(5),
        CreatedAt = Parse(reader.GetString(6)),
        UpdatedAt = Parse(reader.GetString(7)),
    };

    private static string Format(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.Now;

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }
}
