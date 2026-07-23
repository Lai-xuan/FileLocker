namespace FileLocker.Core.Settings;

/// <summary>
/// 對應 GUI 設定頁：Vault 位置、語言、主題。存在固定位置（不像 Vault 本身可以搬），
/// 因為這是「這個 App 安裝在這台裝置上」的設定，不是要跟著 Vault 內容搬走的東西。
/// </summary>
public class AppSettings
{
    /// <summary>null 代表還沒設定過，交由呼叫端決定預設值（見 AppSettingsManager）。</summary>
    public string? VaultPath { get; set; }

    /// <summary>目前只有繁體中文一個選項，先把欄位定出來，未來加語言時前端直接多一個選項即可，不用動這裡的格式。</summary>
    public string Language { get; set; } = "zh-TW";

    /// <summary>light 或 dark。目前只存偏好、按鈕看得到，實際套用畫面主題要等 GUI 美化階段才會真的生效。</summary>
    public string Theme { get; set; } = "light";
}