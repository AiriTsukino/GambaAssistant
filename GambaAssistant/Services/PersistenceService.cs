using System.Text.Json;
using GambaAssistant.Games.Blackjack;

namespace GambaAssistant.Services;

public sealed class PersistenceService : IDisposable
{
    private readonly Configuration config;
    private readonly string root;
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    public string ConfigRoot => root;

    public PersistenceService(Configuration config)
    {
        this.config = config;
        root = DalamudServices.PluginInterface.ConfigDirectory.FullName;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Profiles"));
        Directory.CreateDirectory(Path.Combine(root, "Templates"));
        Directory.CreateDirectory(Path.Combine(root, "Exports"));
    }

    public void SaveNow() => DalamudServices.PluginInterface.SavePluginConfig(config);

    public void SaveJson<T>(string relativePath, T value)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, jsonOptions));
    }

    public T? LoadJson<T>(string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        if (!File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
    }

    public void Dispose() => SaveNow();
}
