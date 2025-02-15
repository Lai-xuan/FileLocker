using System;
using System.IO;
using System.Data;
using System.Data.SQLite;
using FileLockerApp.Properties; // �� �T�O�ɤJ�o�өR�W�Ŷ�

public class DatabaseManager
{
    private string _connectionString;
    private string _databasePath;
    private string _lockedDataPath;

    public DatabaseManager()
    {
        // Ū���]�w��
        _lockedDataPath = Settings.Default.LockedDataPath;  // �� �ץ� `Properties.Settings`

        // �p�G�ϥΪ��٨S�]�w�A�w�]�s��b���ε{����Ƨ�
        if (string.IsNullOrEmpty(_lockedDataPath))
        {
            _lockedDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "locked_data");
        }

        // �T�O locked_data ��Ƨ��s�b
        if (!Directory.Exists(_lockedDataPath))
        {
            Directory.CreateDirectory(_lockedDataPath);
        }

        // �]�w��Ʈw�s����|
        _databasePath = Path.Combine(_lockedDataPath, "FileLocker.db");
        _connectionString = $"Data Source={_databasePath};Version=3;";

        // ��l�Ƹ�Ʈw
        InitializeDatabase(_databasePath); // �� �ǤJ `_databasePath`
    }

    public static void InitializeDatabase(string databasePath)
    {
        try
        {
            // �T�O��Ʈw�ɮץi�Q�s��
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
            throw new Exception($"��Ʈw��l�ƥ���: {ex.Message}");
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

        // �����ª���Ʈw�ɮ�
        if (File.Exists(_databasePath))
        {
            File.Move(_databasePath, newDatabasePath);
        }

        _lockedDataPath = newPath;
        _databasePath = newDatabasePath;
        _connectionString = $"Data Source={_databasePath};Version=3;";

        // �x�s�]�w
        Settings.Default.LockedDataPath = newPath; // �� �ץ� `Properties.Settings`
        Settings.Default.Save();
    }
}
