using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FolderToFileName
{
    public class AppConfig
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "f2fn_config.json");

        public bool UsePrefix { get; set; } = false;
        public string Prefix { get; set; } = string.Empty;

        public bool UseSuffix { get; set; } = false;
        public string Suffix { get; set; } = string.Empty;

        public bool UseSeparator { get; set; } = false;
        public string Separator { get; set; } = string.Empty;

        public bool IncludeSubdirectories { get; set; } = false;
        public bool IncludeAllParentFolders { get; set; } = false;
        public bool LimitExtensionsByConfig { get; set; } = false;
        public bool MoveToRootDirectory { get; set; } = false;

        public List<string> AllowedExtensions { get; set; } = ["mp4", "ts", "mkv", "mov"];

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null)
                    {
                        return config;
                    }
                }
            }
            catch
            {
                // 读取失败时采用默认配置
            }

            var defaultConfig = new AppConfig();
            defaultConfig.Save();
            return defaultConfig;
        }

        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch
            {
                // 忽略配置文件保存异常
            }
        }
    }
}