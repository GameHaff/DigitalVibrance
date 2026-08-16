using System.Collections.Generic;

namespace DigitalVibrance.Core;

/// <summary>Everything persisted to %AppData%\DigitalVibrance\config.json.</summary>
public sealed class AppConfig
{
    /// <summary>Global kill switch. Off means the screen is never touched.</summary>
    public bool MasterEnabled { get; set; } = true;

    /// <summary>Apply <see cref="Desktop"/> when no game profile is active.</summary>
    public bool ApplyToDesktop { get; set; }

    public ColorProfile Desktop { get; set; } = new();

    public List<GameProfile> Games { get; set; } = new();

    public bool MinimizeToTray { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>ISO code of the chosen language. Null means "follow the OS display language".</summary>
    public string? Language { get; set; }

    /// <summary>"Dark" or "Light". Null means "follow the Windows app theme".</summary>
    public string? Theme { get; set; }
}
