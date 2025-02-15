using System;
using System.IO;
using System.Data;
using System.Data.SQLite;
using FileLockerApp.Properties; // ← 確保導入這個命名空間

public class DatabaseManager
{
    private string _connectionString;
    private string _databasePath;
    private string _lockedDataPath;

    public DatabaseManager()
    {
        // 讀取設定值
        _lockedDataPath = Settings.Default.LockedDataPath;  // ← 修正 `Properties.Settings`

        // 如果使用者還沒設定，預設存放在應用程式資料夾
        if (string.IsNullOrEmpty(_lockedDataPath))
        {
            _lockedDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "locked_data");
        }

        // 確保 locked_data 資料夾存在
        if (!Directory.Exists(_lockedDataPath))
        {
            Directory.CreateDirectory(_lockedDataPath);
        }

        // 設定資料庫存放路徑
        _databasePath = Path.Combine(_lockedDataPath, "FileLocker.db");
        _connectionString = $"Data Source={_databasePath};Version=3;";

        // 初始化資料庫
        InitializeDatabase(_databasePath); // ← 傳入 `_databasePath`
    }

    public static void InitializeDatabase(string databasePath)
    {
        try
        {
            // 確保資料庫檔案可被存取
            if (!File.Exists(databasePath))
            {
                SQLiteConnection.CreateFile(databasePath);
            }

            using (var connection = new SQLiteConnection($"Data Source={databasePath}; Version=3; Journal Mode=WAL;"))
            {
                connection.Open();

                string createTableQuery = @"
                    CREATE TABLE IF NOT EXISTS EncryptedFiles (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        OriginalName TEXT NOT NULL,
                        EncryptedPath TEXT NOT NULL UNIQUE,
                        PasswordHash TEXT NOT NULL,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    );";

                using (var command = new SQLiteCommand(createTableQuery, connection))
                {
                    command.ExecuteNonQuery();
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"資料庫初始化失敗: {ex.Message}");
        }
    }

    public string GetLockedDataPath()
    {
        return _lockedDataPath;
    }

    public void SetLockedDataPath(string newPath)
    {
        if (!Directory.Exists(newPath))
        {
            Directory.CreateDirectory(newPath);
        }

        string newDatabasePath = Path.Combine(newPath, "FileLocker.db");

        // 移動舊的資料庫檔案
        if (File.Exists(_databasePath))
        {
            File.Move(_databasePath, newDatabasePath);
        }

        _lockedDataPath = newPath;
        _databasePath = newDatabasePath;
        _connectionString = $"Data Source={_databasePath};Version=3;";

        // 儲存設定
        Settings.Default.LockedDataPath = newPath; // ← 修正 `Properties.Settings`
        Settings.Default.Save();
    }
}
