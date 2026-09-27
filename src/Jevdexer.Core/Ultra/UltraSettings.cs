namespace Jevdexer.Core.Ultra;

/// <summary>A folder the user gave to Ultra Search.</summary>
public sealed class UltraFolder
{
    public string Path { get; set; } = "";

    /// <summary>
    /// Content understanding for this folder never touches the cloud, not even scrubbed summaries.
    /// Default for folders that look sensitive (name, or a file with ID numbers, medical words, keys).
    /// </summary>
    public bool LocalOnly { get; set; }

    /// <summary>The user confirmed a sensitive-looking folder may use Jev (cleared again when new signals appear).</summary>
    public bool SensitiveConfirmed { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>Ultra Search settings (stored protected inside settings.json).</summary>
public sealed class UltraSettings
{
    public List<UltraFolder> Folders { get; set; } = [];

    /// <summary>"light" (no models: text, keywords, OCR) or "full" (plus local meaning and photo models).</summary>
    public string Mode { get; set; } = "light";

    /// <summary>Scanning waits while the PC is on battery.</summary>
    public bool PauseOnBattery { get; set; } = true;

    /// <summary>The user paused scanning.</summary>
    public bool Paused { get; set; }

    /// <summary>Smart content searches (Jev calls) allowed per calendar month. 0: none, local only.</summary>
    public int MonthlyCap { get; set; } = 300;

    public int UsedThisMonth { get; set; }
    public string UsageMonth { get; set; } = "";

    /// <summary>Last benchmark, one core: ms per passage and per photo.</summary>
    public double? PassageMs { get; set; }
    public double? PhotoMs { get; set; }

    public bool Full => Mode == "full";

    /// <summary>Counts one smart content search; false when the cap is reached (nothing is sent).</summary>
    public bool TryUse()
    {
        var month = DateTime.Now.ToString("yyyy-MM");
        if (UsageMonth != month)
        {
            UsageMonth = month;
            UsedThisMonth = 0;
        }
        if (UsedThisMonth >= MonthlyCap) return false;
        UsedThisMonth++;
        return true;
    }

    public int UsedNow => UsageMonth == DateTime.Now.ToString("yyyy-MM") ? UsedThisMonth : 0;
}
