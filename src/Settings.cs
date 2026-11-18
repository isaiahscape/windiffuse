using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DiffuseWindows
{
    [DataContract]
    public sealed class AppSettings
    {
        [DataMember] public string PostProcessingMode = "blur";
        [DataMember] public float NoiseScale = 0.8f;
        [DataMember] public float SpeedMin = 0.05f;
        [DataMember] public float SpeedMax = 0.50f;
        [DataMember] public float BrightnessOffset = 0.0f;
        [DataMember] public float Saturation = 1.0f;
        [DataMember] public string HdrMode = "auto";
        [DataMember] public float HdrIntensity = 1.6f;
        [DataMember] public bool EnableLiveBeats = false;
        [DataMember] public float BeatStrength = 0.5f;
        [DataMember] public bool PerformanceMode = false;
        [DataMember] public bool PreserveAfterPause = false;
        [DataMember] public string DefaultPaletteId = "monochrome";
        [DataMember] public string CustomPalettePath = "";
        [DataMember] public int WallpaperOpacity = 255;
        [DataMember] public int TargetFps = 60;
        [DataMember] public bool UseVSync = false;
        [DataMember] public bool PauseOnFullscreen = true;
        [DataMember] public bool SpanAllMonitors = true;
        [DataMember] public bool AttachToDesktop = true;
        [DataMember] public bool StartupConfigured = false;

        private static string SettingsDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DiffuseWindows"
                );
            }
        }

        private static string SettingsFilePath
        {
            get { return Path.Combine(SettingsDirectory, "settings.json"); }
        }

        public AppSettings Clone()
        {
            return (AppSettings)MemberwiseClone();
        }

        public void Clamp()
        {
            if (PostProcessingMode != "blur" && PostProcessingMode != "flutedGlass" && PostProcessingMode != "none")
            {
                PostProcessingMode = "blur";
            }
            if (HdrMode != "auto" && HdrMode != "on" && HdrMode != "off")
            {
                HdrMode = "auto";
            }
            NoiseScale = ClampFloat(NoiseScale, 0.05f, 2.5f);
            SpeedMin = ClampFloat(SpeedMin, 0.0f, 2.0f);
            SpeedMax = ClampFloat(SpeedMax, SpeedMin, 3.0f);
            BrightnessOffset = ClampFloat(BrightnessOffset, -1.0f, 1.0f);
            Saturation = ClampFloat(Saturation, 0.0f, 2.0f);
            HdrIntensity = ClampFloat(HdrIntensity, 0.5f, 3.5f);
            BeatStrength = ClampFloat(BeatStrength, 0.0f, 2.0f);
            if (WallpaperOpacity < 25) WallpaperOpacity = 25;
            if (WallpaperOpacity > 255) WallpaperOpacity = 255;
            if (TargetFps < 10) TargetFps = 10;
            if (TargetFps > 240) TargetFps = 240;
            if (string.IsNullOrEmpty(DefaultPaletteId)) DefaultPaletteId = "monochrome";
            if (CustomPalettePath == null) CustomPalettePath = "";
        }

        private static float ClampFloat(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    byte[] bytes = File.ReadAllBytes(SettingsFilePath);
                    using (var ms = new MemoryStream(bytes))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                        var loaded = serializer.ReadObject(ms) as AppSettings;
                        if (loaded != null)
                        {
                            loaded.Clamp();
                            return loaded;
                        }
                    }
                }
            }
            catch
            {
                // Fall back to defaults on corrupt settings file.
            }

            var defaults = new AppSettings();
            defaults.Clamp();
            return defaults;
        }

        public void Save()
        {
            Clamp();
            try
            {
                if (!Directory.Exists(SettingsDirectory))
                {
                    Directory.CreateDirectory(SettingsDirectory);
                }

                var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                using (var ms = new MemoryStream())
                {
                    serializer.WriteObject(ms, this);
                    File.WriteAllBytes(SettingsFilePath, ms.ToArray());
                }
            }
            catch
            {
                // Ignore transient disk write failures.
            }
        }
    }

    public sealed class PaletteEntry
    {
        public string Id;
        public string DisplayName;
        public string FilePath;

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public static class AssetPaths
    {
        public static string AssetsRoot
        {
            get
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string direct = Path.Combine(exeDir, "assets");
                if (Directory.Exists(direct))
                {
                    return direct;
                }

                string parent = Path.GetFullPath(Path.Combine(exeDir, "..", "assets"));
                if (Directory.Exists(parent))
                {
                    return parent;
                }

                return direct;
            }
        }

        public static string ShaderPath(string fileName)
        {
            return Path.Combine(AssetsRoot, "shaders", fileName);
        }

        public static string RandTexturePath()
        {
            return Path.Combine(AssetsRoot, "rand.png");
        }

        public static List<PaletteEntry> GetBuiltInPalettes()
        {
            var result = new List<PaletteEntry>();
            string dir = Path.Combine(AssetsRoot, "palettes");
            if (!Directory.Exists(dir))
            {
                return result;
            }

            string[] files = Directory.GetFiles(dir, "album_*.jpg");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                string baseName = Path.GetFileNameWithoutExtension(file);
                string id = baseName.StartsWith("album_", StringComparison.OrdinalIgnoreCase)
                    ? baseName.Substring(6)
                    : baseName;

                result.Add(new PaletteEntry
                {
                    Id = id,
                    DisplayName = FormatPaletteName(id),
                    FilePath = file
                });
            }

            return result;
        }

        public static string ResolvePaletteFile(AppSettings settings)
        {
            if (!string.IsNullOrEmpty(settings.CustomPalettePath) && File.Exists(settings.CustomPalettePath))
            {
                return settings.CustomPalettePath;
            }

            List<PaletteEntry> palettes = GetBuiltInPalettes();
            for (int i = 0; i < palettes.Count; i++)
            {
                if (string.Equals(palettes[i].Id, settings.DefaultPaletteId, StringComparison.OrdinalIgnoreCase))
                {
                    return palettes[i].FilePath;
                }
            }

            if (palettes.Count > 0)
            {
                return palettes[0].FilePath;
            }

            return null;
        }

        private static string FormatPaletteName(string id)
        {
            string[] parts = id.Split('_');
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpperInvariant(p[0]));
                if (p.Length > 1) sb.Append(p.Substring(1));
            }
            return sb.ToString();
        }
    }
}
