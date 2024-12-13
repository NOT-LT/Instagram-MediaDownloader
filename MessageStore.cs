using Microsoft.Data.Sqlite;
using System;

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

        public MessageStore(string dbPath = "processed_messages.db")
        {
            dbPath = Environment.GetEnvironmentVariable("SQLITE_DB_PATH")
             ?? "processed_messages.db";

            _connectionString = $"Data Source={dbPath}";

            Init();
        }

        private void Init()
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
    CutoffTimestamp INTEGER NOT NULL
);
";
            cmd.ExecuteNonQuery();
        }

        public long GetCutoff(string threadId)
        {
            using var con = new SqliteConnection(_connectionString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT CutoffTimestamp FROM ThreadState WHERE ThreadId = $t;";
            cmd.Parameters.AddWithValue("$t", threadId);

            object? obj = cmd.ExecuteScalar();
            return obj == null ? 0 : Convert.ToInt64(obj);
        }

        public void SetCutoff(string threadId, long cutoff)
        {
            using var con = new SqliteConnection(_connectionString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
INSERT INTO ThreadState (ThreadId, CutoffTimestamp)
VALUES ($t, $c)
ON CONFLICT(ThreadId) DO UPDATE SET CutoffTimestamp = excluded.CutoffTimestamp;
";
            cmd.Parameters.AddWithValue("$t", threadId);
            cmd.Parameters.AddWithValue("$c", cutoff);
            cmd.ExecuteNonQuery();
        }

        public bool Exists(string messageId)
        {
            using var con = new SqliteConnection(_connectionString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT 1 FROM Messages WHERE MessageId = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", messageId);

            return cmd.ExecuteScalar() != null;
        }

        public MessageStatus? GetStatus(string messageId)
        {
            using var con = new SqliteConnection(_connectionString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT Status FROM Messages WHERE MessageId = $id;";
            cmd.Parameters.AddWithValue("$id", messageId);

            object? obj = cmd.ExecuteScalar();
            if (obj == null) return null;

            return (MessageStatus)Convert.ToInt32(obj);
        }

        public bool IsTerminalProcessed(string messageId)
        {
            // Treat both Processed and Skipped as "done"
            var status = GetStatus(messageId);
            return status == MessageStatus.Processed || status == MessageStatus.Skipped;
        }

        public void UpsertMessage(string messageId, string threadId, long timestamp, MessageStatus status)
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
        }
    }
}
