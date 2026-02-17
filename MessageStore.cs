using Microsoft.Data.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace IGMediaDownloaderV2
{
    internal enum MessageStatus
    {
        New = 0,
        Processed = 1,
        Skipped = 2,
        Failed = 3
    }

    internal sealed class MessageStore
    {
        private readonly string _connectionString;
        // Use ConcurrentDictionary for thread-safe access without manual locks
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _knownThreads = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        // For a thread safe HashSet, we use ConcurrentDictionary with dummy bytes to represent presence
        public MessageStore(string dbPath = "processed_messages.db")
        {
            dbPath = Environment.GetEnvironmentVariable("SQLITE_DB_PATH") ?? "processed_messages.db";
            // Busy Timeout is essential for preventing "Database is locked" errors during concurrent terminal access
            _connectionString = $"Data Source={dbPath};Default Timeout=5;";

            Init();
            LoadThreadCache();
        }

        #region Safety Wrappers
        private T? ExecuteSafe<T>(Func<T> action, string methodName)
        {
            try
            {
                return action();
            }
            catch (SqliteException ex)
            {
                Logger.Error($"SQL ERROR: {methodName} ({ex.SqliteErrorCode}): {ex.Message}");
                return default;
            }
            catch (Exception ex)
            {
                Logger.Error($"GENERAL ERROR: {methodName}: {ex.Message}");
                return default;
            }
        }

        private void ExecuteSafe(Action action, string methodName)
        {
            try { action(); }
            catch (SqliteException ex) { Logger.Error($"SQL ERROR: {methodName} ({ex.SqliteErrorCode}): {ex.Message}"); }
            catch (Exception ex) { Logger.Error($"GENERAL ERROR: {methodName}: {ex.Message}"); }
        }
        #endregion

        private void LoadThreadCache()
        {
            ExecuteSafe(() =>
            {
                using var con = new SqliteConnection(_connectionString);
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT ThreadId FROM ThreadState;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    _knownThreads.TryAdd(reader.GetString(0), 0);
                }
            }, nameof(LoadThreadCache));
        }

        private void Init()
        {
            ExecuteSafe(() =>
            {
                using var con = new SqliteConnection(_connectionString);
                con.Open();

                // Optional but recommended for concurrency/perf
                using (var pragma = con.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                    pragma.ExecuteNonQuery();
                }

                using var cmd = con.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Messages (
    MessageId TEXT PRIMARY KEY,
    ThreadId  TEXT NOT NULL,
    Timestamp INTEGER NOT NULL,
    Status    INTEGER NOT NULL, -- 0=New, 1=Processed, 2=Skipped, 3=Failed
    UpdatedAtUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_Messages_Thread_Timestamp
ON Messages(ThreadId, Timestamp DESC);

CREATE TABLE IF NOT EXISTS ThreadState (
    ThreadId TEXT PRIMARY KEY,
    ReceiverId TEXT,
    CutoffTimestamp INTEGER NOT NULL
);
";
                cmd.ExecuteNonQuery();
            }, nameof(Init));
        }

        public long GetCutoff(string threadId)
        {
            return ExecuteSafe(() =>
            {
                using var con = new SqliteConnection(_connectionString);
                con.Open();

                using var cmd = con.CreateCommand();
                cmd.CommandText = @"SELECT CutoffTimestamp FROM ThreadState WHERE ThreadId = $t;";
                cmd.Parameters.AddWithValue("$t", threadId);

                object? obj = cmd.ExecuteScalar();
                return obj == null ? 0 : Convert.ToInt64(obj);
            }, nameof(GetCutoff));
        }

        public async Task SetCutoffAsync(string threadId, long cutoff)
        {
            await _writeLock.WaitAsync();
            try
            {
                ExecuteSafe(() =>
                {
                    using var con = new SqliteConnection(_connectionString);
                    con.Open();

                    using var cmd = con.CreateCommand();
                    cmd.CommandText = @"
INSERT INTO ThreadState (ThreadId, CutoffTimestamp)
VALUES ($t,$c)
ON CONFLICT(ThreadId) DO UPDATE SET CutoffTimestamp = excluded.CutoffTimestamp;
";
                    cmd.Parameters.AddWithValue("$t", threadId);
                    cmd.Parameters.AddWithValue("$c", cutoff);
                    cmd.ExecuteNonQuery();

                    _knownThreads.TryAdd(threadId, 0);
                }, nameof(SetCutoffAsync));
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public bool Exists(string messageId)
        {
            return ExecuteSafe(() =>
            {
                using var con = new SqliteConnection(_connectionString);
                con.Open();

                using var cmd = con.CreateCommand();
                cmd.CommandText = @"SELECT 1 FROM Messages WHERE MessageId = $id LIMIT 1;";
                cmd.Parameters.AddWithValue("$id", messageId);

                return cmd.ExecuteScalar() != null;
            }, nameof(Exists));
        }

        public MessageStatus? GetStatus(string messageId)
        {
            return ExecuteSafe<MessageStatus?>(() =>
            {
                using var con = new SqliteConnection(_connectionString);
                con.Open();

                using var cmd = con.CreateCommand();
                cmd.CommandText = @"SELECT Status FROM Messages WHERE MessageId = $id;";
                cmd.Parameters.AddWithValue("$id", messageId);

                object? obj = cmd.ExecuteScalar();
                if (obj == null) return null;

                return (MessageStatus)Convert.ToInt32(obj);
            }, nameof(GetStatus));
        }

        public bool IsTerminalProcessed(string messageId)
        {
            // Treat both Processed and Skipped as "done"
            var status = GetStatus(messageId);
            return status == MessageStatus.Processed || status == MessageStatus.Skipped;
        }

        public async Task UpsertMessageAsync(string messageId, string threadId, long timestamp, MessageStatus status)
        {
            await _writeLock.WaitAsync();
            try
            {
                ExecuteSafe(() =>
                {
                    using var con = new SqliteConnection(_connectionString);
                    con.Open();

                    using var cmd = con.CreateCommand();
                    cmd.CommandText = @"
INSERT INTO Messages (MessageId, ThreadId, Timestamp, Status, UpdatedAtUtc)
VALUES ($id, $t, $ts, $s, $u)
ON CONFLICT(MessageId) DO UPDATE SET
    ThreadId = excluded.ThreadId,
    Timestamp = excluded.Timestamp,
    Status = excluded.Status,
    UpdatedAtUtc = excluded.UpdatedAtUtc;
";

                    cmd.Parameters.AddWithValue("$id", messageId);
                    cmd.Parameters.AddWithValue("$t", threadId);
                    cmd.Parameters.AddWithValue("$ts", timestamp);
                    cmd.Parameters.AddWithValue("$s", (int)status);
                    cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));

                    cmd.ExecuteNonQuery();
                }, nameof(UpsertMessageAsync));
            }
            finally
            {
                _writeLock.Release();
            }
        }


        public int DeleteUntilCutoff()
        {
            return ExecuteSafe(() =>
            {
                using var con = new SqliteConnection(_connectionString);
                con.Open();

                using var cmd = con.CreateCommand();
                cmd.CommandText = @"
DELETE FROM Messages
WHERE MessageId IN (
    SELECT m.MessageId
    FROM Messages m
    JOIN ThreadState t
        ON m.ThreadId = t.ThreadId
    WHERE m.Timestamp < t.CutoffTimestamp
);
";
                return cmd.ExecuteNonQuery(); // returns number of deleted rows
            }, nameof(DeleteUntilCutoff));
        }

        public async Task EnsureThreadExistsAndAddAsync(string threadId, string receiverId)
        {
            if (_knownThreads.ContainsKey(threadId))
                return;

            await _writeLock.WaitAsync();
            try
            {
                ExecuteSafe(() =>
                {
                    using var con = new SqliteConnection(_connectionString);
                    con.Open();

                    using var cmd = con.CreateCommand();
                    cmd.CommandText =
                        "INSERT OR IGNORE INTO ThreadState (ThreadId, ReceiverId, CutoffTimestamp) VALUES ($t,$r,0);";

                    cmd.Parameters.AddWithValue("$t", threadId);
                    cmd.Parameters.AddWithValue("$r", receiverId ?? (object)DBNull.Value);
                    cmd.ExecuteNonQuery();

                    _knownThreads.TryAdd(threadId, 0);
                    Logger.Info($"Added {receiverId} to thread {threadId}");
                }, nameof(EnsureThreadExistsAndAddAsync));
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public string? GetReceiverId(string threadId)
        {
            return ExecuteSafe(() =>
            {
                using var con = new SqliteConnection(_connectionString);
                con.Open();

                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT ReceiverId FROM ThreadState WHERE ThreadId = $t LIMIT 1;";
                cmd.Parameters.AddWithValue("$t", threadId);

                object? obj = cmd.ExecuteScalar();
                return (obj == null || obj == DBNull.Value) ? null : obj.ToString();
            }, nameof(GetReceiverId));
        }

        public string? GetThreadId(string userId)
        {
            return ExecuteSafe(() =>
            {
                using var con = new SqliteConnection(_connectionString);
                con.Open();

                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT ThreadId FROM ThreadState WHERE ReceiverId = $t LIMIT 1;";
                cmd.Parameters.AddWithValue("$t", userId);

                object? obj = cmd.ExecuteScalar();
                return (obj == null || obj == DBNull.Value) ? null : obj.ToString();
            }, nameof(GetThreadId));
        }

        public void RegisterThread(string threadId, string receiverId)
        {
            if (string.IsNullOrEmpty(threadId)) return;

            ExecuteSafe(() =>
            {
                using var con = new SqliteConnection(_connectionString);
                con.Open();

                using var cmd = con.CreateCommand();
                cmd.CommandText = @"
        INSERT INTO ThreadState (ThreadId, ReceiverId, CutoffTimestamp)
        VALUES ($t, $r, 0)
        ON CONFLICT(ThreadId) DO UPDATE SET
            ReceiverId = excluded.ReceiverId;
    ";
                cmd.Parameters.AddWithValue("$t", threadId);
                cmd.Parameters.AddWithValue("$r", receiverId ?? (object)DBNull.Value);

                cmd.ExecuteNonQuery();
                _knownThreads.TryAdd(threadId, 0);
            }, nameof(RegisterThread));
        }

        public int DeleteOlderThanOneHour()
        {
            return ExecuteSafe(() =>
            {
                using var con = new SqliteConnection(_connectionString);
                con.Open();

                using var cmd = con.CreateCommand();
                cmd.CommandText = @"
DELETE FROM Messages
WHERE datetime(UpdatedAtUtc) <= datetime('now', '-1 hour');
";

                return cmd.ExecuteNonQuery();
            }, nameof(DeleteOlderThanOneHour));
        }
    }
}