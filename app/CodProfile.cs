using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace JesusKpekpes;

sealed record CodSetting(string Key, string Index, string Value, string Why);

sealed record CodChange(string Key, string Index, string Old, string New, string Why);

sealed record CodPlan(string NewText, List<CodChange> Changes, int Unchanged, List<CodSetting> Missing);

/// <summary>
/// Applies a short, hand-picked list of portable settings to the Call of Duty options file.
/// Only the value after "=" changes. The key, the numbers after the @, and the comment stay as the game wrote them.
/// Resolution, the Windows refresh rate, devices, and DLSS are not touched.
/// The in-game cap is set to one under the primary monitor when that rate can be read.
/// </summary>
static class CodProfile
{
    public static readonly CodSetting[] Core =
    [
        new("AudioWantedChannelsNumber", "0", "2", "Two channels, so the stereo footstep tune is what you hear"),
        new("WindowsSonicEnable", "0", "false", "Windows Sonic would process the sound a second time"),
        new("MusicVolume", "0", "0.000000", "No music over the footsteps"),
        new("VoiceVolume", "0", "0.400000", "Voices lower so they do not cover steps"),
        new("EffectsVolume", "0", "1.000000", "Effects at full"),
        new("HitMarkersVolume", "0", "1.000000", "Hit sound at full"),
        new("Volume", "0", "1.000000", "Game volume at full, set the headphones volume instead"),
        new("NvidiaReflex", "0", "Enabled + boost", "On + boost. Keeps the GPU clocks up when a scene is easy"),
        new("VSync", "0", "disabled", "No sync delay"),
        new("VSyncInMenu", "0", "disabled", "No sync delay in menus"),
        new("MotionBlur", "0", "Off", "Clearer picture while moving"),
        new("WeaponMotionBlur", "0", "false", "Clearer weapon"),
        new("DofWeaponDisable", "0", "true", "Clearer weapon"),
        new("DxrMode", "0", "Off", "Ray tracing is heavy"),
        new("DxrMode", "1", "Off", "Ray tracing is heavy"),
        new("ShadowQuality", "0", "Very_Low", "More frames"),
        new("ShadowFilteringQuality", "0", "Low", "More frames"),
        new("ParticleQuality", "0", "very low", "More frames, less clutter"),
        new("ShadingQuality", "0", "Low", "More frames"),
        new("ModelQuality", "0", "Low Quality", "More frames"),
        new("ShaderQuality", "0", "Low", "More frames"),
        new("TerrainQuality", "0", "Very Low", "More frames"),
        new("Tessellation", "0", "0_Off", "More frames"),
        new("TextureFilter", "0", "aniso 2x", "Light on the GPU"),
        new("ScreenSpaceShadowQuality", "0", "Off", "More frames"),
        new("SSRQuality", "0", "Off", "More frames"),
        new("SSRQuality", "1", "Off", "More frames"),
        new("AmbientLightingQuality", "0", "Off", "More frames"),
        new("WaterCausticsMode", "0", "Off", "More frames"),
        new("WeatherGridVolumesQuality", "0", "Off", "More frames"),
        new("VolumetricQuality", "0", "QUALITY_LOW", "More frames"),
        new("VolumetricQuality", "1", "QUALITY_LOW", "More frames"),
        new("DeferredPhysics", "0", "Low Quality", "More frames"),
        new("WorldStreamingQuality", "0", "Low", "Smoother streaming"),
        new("GPUUploadHeaps", "0", "true", "Uses Resizable BAR when the PC has it"),
        new("SkipIntro", "1", "true", "Skip the intro movie"),
    ];

    /// <summary>Optional. Makes the picture much softer, so it is asked about separately.</summary>
    public static readonly CodSetting[] Lowest =
    [
        new("ResolutionMultiplier", "0", "50", "Renders at half size. Big gain in frames, much softer picture"),
        new("BulletImpacts", "0", "false", "No impact marks. Fewer frames lost, less feedback"),
    ];

    public static string LeftAlone(int refreshHz)
    {
        var cap = CapUnderRefresh(refreshHz);
        var tail = cap > 0
            ? " In-game cap: " + cap.ToString(CultureInfo.InvariantCulture) + " on a " + refreshHz.ToString(CultureInfo.InvariantCulture) + " Hz panel."
            : " The frame cap was left as it is, because the monitor rate could not be read.";
        return "Not touched: audio devices, microphone, monitor choice, GPU name, driver versions, resolution, refresh rate, DLSS, AMD and Intel options, window position." + tail;
    }

    /// <summary>One under the panel. 200 Hz becomes 199, so a frame is not held on the ceiling. 0 means do not write a cap.</summary>
    public static int CapUnderRefresh(int hz) => hz >= 60 && hz <= 1000 ? hz - 1 : 0;

    static readonly string[] GameNames = ["cod", "BlackOps7", "BlackOps6", "BlackOpsColdWar", "ModernWarfare"];

    public static CodSetting[] Chosen(bool lowest, int refreshHz = 0)
    {
        var list = new List<CodSetting>(Core.Length + (lowest ? Lowest.Length : 0) + 2);
        list.AddRange(Core);
        if (lowest) list.AddRange(Lowest);
        var cap = CapUnderRefresh(refreshHz);
        if (cap > 0)
        {
            list.Add(new("CapFps", "0", "true", "The frame cap does nothing while this is off"));
            list.Add(new("MaxFpsInGame", "0", cap.ToString(CultureInfo.InvariantCulture),
                cap.ToString(CultureInfo.InvariantCulture) + " on a " + refreshHz.ToString(CultureInfo.InvariantCulture) + " Hz panel. One under the ceiling."));
        }
        return list.ToArray();
    }

    public static CodPlan Plan(string text, IEnumerable<CodSetting> settings)
    {
        var wanted = settings.ToDictionary(s => s.Key + "@" + s.Index, s => s, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<CodChange>();
        var unchanged = 0;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd('\r');
            var tail = raw[line.Length..];
            if (line.StartsWith("//") || line.Length == 0) continue;
            var eq = line.IndexOf(" = ", StringComparison.Ordinal);
            var at = line.IndexOf('@');
            var semi = line.IndexOf(';');
            if (eq < 0 || at < 1 || semi < at || semi > eq) continue;
            var key = line[..at] + "@" + line[(at + 1)..semi];
            if (!wanted.TryGetValue(key, out var setting) || seen.Contains(key)) continue;
            seen.Add(key);
            var right = line[(eq + 3)..];
            var comment = right.IndexOf(" // ", StringComparison.Ordinal);
            var old = comment >= 0 ? right[..comment] : right;
            var rest = comment >= 0 ? right[comment..] : "";
            if (old.Trim() == setting.Value)
            {
                unchanged++;
                continue;
            }
            changes.Add(new CodChange(setting.Key, setting.Index, old.Trim(), setting.Value, setting.Why));
            lines[i] = line[..(eq + 3)] + setting.Value + rest + tail;
        }
        var missing = wanted.Values.Where(s => !seen.Contains(s.Key + "@" + s.Index)).ToList();
        return new CodPlan(string.Join("\n", lines), changes, unchanged, missing);
    }

    public static string Describe(string file, CodPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine(file);
        if (plan.Changes.Count == 0) sb.AppendLine("  Already matches. Nothing to change.");
        foreach (var c in plan.Changes)
            sb.AppendLine($"  {c.Key}: {c.Old} -> {c.New}   ({c.Why})");
        sb.AppendLine($"  Already right: {plan.Unchanged}. Not in this file: {plan.Missing.Count}.");
        return sb.ToString();
    }

    public static string? Folder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Call of Duty", "players");
        return Directory.Exists(dir) ? dir : null;
    }

    public static string[] Files()
    {
        var dir = Folder();
        return dir is null ? [] : Directory.GetFiles(dir, "options*.cst");
    }

    public static bool GameRunning()
    {
        var all = Process.GetProcesses();
        try { return all.Any(p => GameNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)); }
        finally { foreach (var p in all) p.Dispose(); }
    }

    public static string BackupOf(string file) => file + ".kpekpes-backup";

    /// <summary>Writes the new text. Keeps the original once as .kpekpes-backup and keeps the file's own encoding.</summary>
    public static void Write(string file, string newText)
    {
        var bytes = File.ReadAllBytes(file);
        var hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (!File.Exists(BackupOf(file))) File.Copy(file, BackupOf(file));
        var temp = file + ".kpekpes-tmp";
        File.WriteAllText(temp, newText, new UTF8Encoding(hadBom));
        File.Move(temp, file, true);
    }

    public static string Read(string file) => File.ReadAllText(file);

    public static bool Restore(string file)
    {
        var backup = BackupOf(file);
        if (!File.Exists(backup)) return false;
        File.Copy(backup, file, true);
        return true;
    }

    /// <summary>Current refresh of the primary display. 0 if Windows does not say.</summary>
    public static int PrimaryRefreshHz()
    {
        try
        {
            var mode = new DevMode();
            mode.dmDeviceName = new string('\0', 32);
            mode.dmFormName = new string('\0', 32);
            mode.dmSize = (short)Marshal.SizeOf(typeof(DevMode));
            if (!EnumDisplaySettings(null, -1, ref mode)) return 0;
            return CapUnderRefresh(mode.dmDisplayFrequency) > 0 ? mode.dmDisplayFrequency : 0;
        }
        catch
        {
            return 0;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }
}
