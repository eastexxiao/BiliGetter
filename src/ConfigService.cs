// 配置与路径服务：ini 读写 + 相对路径解析。
// 键名（out / quality / aria2c / ffmpeg_path / aria2c_path）与旧版一致。
using System;
using System.IO;
using System.Text;

namespace BiliGetter
{
    internal sealed class ConfigService : IConfigService
    {
        public string BaseDir { get { return Tools.BaseDir; } }
        public string ConfigFile { get { return Path.Combine(BaseDir, "bili-getter.ini"); } }
        public string CookieFile { get { return Path.Combine(BaseDir, "bili-cookies.txt"); } }
        public string DefaultOutDir { get { return Path.Combine(BaseDir, "downloads"); } }

        public string Resolve(string stored) { return Tools.Resolve(stored); }
        public string Storable(string full) { return Tools.Storable(full); }

        public AppConfig Load()
        {
            AppConfig cfg = new AppConfig();
            cfg.OutDir = DefaultOutDir;
            cfg.QualityIndex = 0;
            cfg.UseAria2c = true;

            try
            {
                if (!File.Exists(ConfigFile)) return cfg;
                foreach (string line in File.ReadAllLines(ConfigFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();

                    if (k == "out") cfg.OutDir = Resolve(v);
                    else if (k == "quality") { int q; if (int.TryParse(v, out q)) cfg.QualityIndex = q; }
                    else if (k == "aria2c") cfg.UseAria2c = v == "1";
                    else if (k == "ffmpeg_path") cfg.FfmpegPath = v.Length > 0 ? Resolve(v) : null;
                    else if (k == "aria2c_path") cfg.Aria2cPath = v.Length > 0 ? Resolve(v) : null;
                }
            }
            catch { }
            return cfg;
        }

        public void Save(AppConfig cfg)
        {
            if (cfg == null) return;
            try
            {
                File.WriteAllText(ConfigFile,
                    "out=" + Storable(cfg.OutDir) + "\r\n"
                    + "quality=" + cfg.QualityIndex + "\r\n"
                    + "aria2c=" + (cfg.UseAria2c ? "1" : "0") + "\r\n"
                    + "ffmpeg_path=" + Storable(cfg.FfmpegPath) + "\r\n"
                    + "aria2c_path=" + Storable(cfg.Aria2cPath) + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
