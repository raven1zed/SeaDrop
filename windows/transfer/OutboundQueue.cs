using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SeaDropWindows.SeaDrop.transfer
{
    public class OutboundQueue : IDisposable
    {
        private const string DbName = "outbound_queue.db";
        private const string TableName = "transfer_queue";
        private readonly string _dbPath;
        private readonly SqliteConnection _connection;
        private bool _disposed;

        public OutboundQueue()
        {
            _dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeaDrop",
                DbName);

            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            _connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
            _connection.Open();
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @$"
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS {TableName} (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FilePath TEXT NOT NULL,
                    EnqueuedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );
                CREATE INDEX IF NOT EXISTS idx_enqueued_at ON {TableName}(EnqueuedAt);
            ";
            cmd.ExecuteNonQuery();
        }

        public void Enqueue(string filePath)
        {
            if (!File.Exists(filePath)) return;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO {TableName} (FilePath) VALUES (@filePath)";
            cmd.Parameters.AddWithValue("@filePath", filePath);
            cmd.ExecuteNonQuery();
        }

        public string? Dequeue()
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @$"
                SELECT Id, FilePath FROM {TableName}
                ORDER BY EnqueuedAt ASC
                LIMIT 1;
            ";

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            int id = reader.GetInt32(0);
            string filePath = reader.GetString(1);
            reader.Close();

            using var deleteCmd = _connection.CreateCommand();
            deleteCmd.CommandText = $"DELETE FROM {TableName} WHERE Id = @id";
            deleteCmd.Parameters.AddWithValue("@id", id);
            deleteCmd.ExecuteNonQuery();

            return filePath;
        }

        public int GetCount()
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {TableName}";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void Clear()
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {TableName}";
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _connection?.Dispose();
                _disposed = true;
            }
        }
    }
}