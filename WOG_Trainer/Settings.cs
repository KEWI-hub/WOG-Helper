using System.Text.Json;

namespace WOG_Trainer;

/// <summary>Automation settings, saved next to the exe. Names match auto.js's config.</summary>
internal sealed class Settings
{
    public EquipSettings    Equip    { get; set; } = new();
    public SortSettings     Sort     { get; set; } = new();
    public TrainingSettings Training { get; set; } = new();
    public RaidSettings     Raid     { get; set; } = new();
    public FusionSettings   Fusion   { get; set; } = new();
    public bool HideCurrency { get; set; } = true;

    public sealed class EquipSettings    { public bool Enabled { get; set; } public int IntervalSec { get; set; } = 5; }
    public sealed class SortSettings     { public bool Enabled { get; set; } public int IntervalSec { get; set; } = 60; }
    public sealed class RaidSettings     { public bool Enabled { get; set; } public int IntervalSec { get; set; } = 10; }
    public sealed class FusionSettings
    {
        public bool Enabled { get; set; }
        public int IntervalSec { get; set; } = 30;
        public int MaxRating { get; set; } = 3;   // Rare
    }
    public sealed class TrainingSettings
    {
        public bool Enabled { get; set; }
        public long ReserveGold { get; set; }
        public int IntervalMs { get; set; } = 1000;
        public bool DamageFirst { get; set; } = true;
    }

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options)); }
        catch { /* read-only folder: settings just are not remembered */ }
    }

    /// <summary>JS object literal for auto.config(...).</summary>
    public string ToAutoConfigJs() => JsonSerializer.Serialize(new
    {
        equip    = new { Equip.Enabled, Equip.IntervalSec },
        sort     = new { Sort.Enabled, Sort.IntervalSec },
        training = new { Training.Enabled, Training.ReserveGold, Training.IntervalMs, Training.DamageFirst },
        raid     = new { Raid.Enabled, Raid.IntervalSec },
        fusion   = new { Fusion.Enabled, Fusion.IntervalSec, Fusion.MaxRating },
    });
}
