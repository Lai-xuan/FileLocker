using System.Text.Json;
using FileLocker.Core.Io;

namespace FileLocker.Core.Settings;

public class AppSettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    public AppSettingsManager(string filePath)
    {
        _filePath = filePath;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // 設定檔損毀就退回預設值，不要讓整個 App 打不開。
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicFile.WriteAllText(_filePath, json);
    }
}