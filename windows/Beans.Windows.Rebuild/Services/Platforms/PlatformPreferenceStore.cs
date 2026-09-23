using System.Text.Json;

namespace Beans.Windows.Rebuild.Services.Platforms;

public interface IPlatformPreferenceStore
{
    string? GetString(string key);
    void SetString(string key, string value);
}

public sealed class LocalSettingsPlatformPreferenceStore : IPlatformPreferenceStore
{
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private Dictionary<string, string>? _values;

    public LocalSettingsPlatformPreferenceStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeansMusic",
        "platform-settings.json"))
    {
    }

    internal LocalSettingsPlatformPreferenceStore(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
            throw new ArgumentException("设置文件路径不能为空", nameof(settingsPath));
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public string? GetString(string key)
    {
        lock (_gate) return Values.GetValueOrDefault(key);
    }

    public void SetString(string key, string value)
    {
        lock (_gate)
        {
            Values[key] = value;
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Values));
            File.Move(temporaryPath, _settingsPath, true);
        }
    }

    private Dictionary<string, string> Values => _values ??= Load();

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_settingsPath)) ?? [];
        }
        catch (IOException) { return []; }
        catch (JsonException) { return []; }
    }
}
