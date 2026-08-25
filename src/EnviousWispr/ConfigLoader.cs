using System.IO;
using System.Text.Json;

namespace EnviousWispr;

public sealed class AppConfig
{
    public string BaseDir { get; set; } = "";
    public string Hotkey { get; set; } = "F9";
    public AsrSection Asr { get; set; } = new();
    public Eg1Section Eg1 { get; set; } = new();

    public sealed class AsrSection
    {
        public string ModelDir { get; set; } = "";
        public string Pack { get; set; } = "int8";
        public string Provider { get; set; } = "cpu";
        public string CudaRuntimeDir { get; set; } = "";
        public int IntraOpThreads { get; set; } = 8;
        public int InterOpThreads { get; set; } = 1;
        public int MaxTokensPerStep { get; set; } = 10;
    }

    public sealed class Eg1Section
    {
        public bool Enabled { get; set; } = true;
        public string ModelDir { get; set; } = "";
        public string EntrypointShard { get; set; } = "";
        public string ServerExe { get; set; } = "";
        // "0" = CPU (default). "all" or an int offloads GPU layers — requires a
        // CUDA build of llama-server as ServerExe (e.g. C:\AI\llama-cuda-b10615\bin).
        public string GpuLayers { get; set; } = "0";
        public int ContextTokens { get; set; } = 16384;
        public int StartTimeoutSeconds { get; set; } = 240;
        public int RequestTimeoutSeconds { get; set; } = 20;
    }

    public string Resolve(string? relative) =>
        string.IsNullOrEmpty(relative)
            ? BaseDir
            : Path.GetFullPath(Path.Combine(BaseDir, Environment.ExpandEnvironmentVariables(relative)));
}

public static class ConfigLoader
{
    public static AppConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        // appsettings.json is camelCase; the default JsonNamingPolicy is case-SENSITIVE.
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), opts)
                  ?? new AppConfig();

        // Resolve BaseDir: explicit setting wins; otherwise walk up from the
        // exe looking for the repo root (a dir containing models/).
        if (string.IsNullOrWhiteSpace(cfg.BaseDir))
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "models")))
                dir = dir.Parent;
            cfg.BaseDir = dir?.FullName ?? AppContext.BaseDirectory;
        }
        return cfg;
    }
}
