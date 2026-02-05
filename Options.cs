using System.Collections.Generic;

/// <summary>
/// Parsed command-line and environment configuration used across the app.
/// </summary>
internal sealed class Options
{
    public ulong PublishedFileId { get; set; }
    public string OutputDir { get; set; } = string.Empty;
    public uint AppId { get; set; } = 268500; // XCOM 2 default
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? GuardCode { get; set; }
    public string? EmailCode { get; set; }
    public string? LogPath { get; set; }
    public string? AuthCachePath { get; set; }
    public List<string> Filters { get; set; } = new();
    public bool UseAnonymous { get; set; }

    public bool IsValid => PublishedFileId != 0 && !string.IsNullOrWhiteSpace(OutputDir);

    public Options Clone()
    {
        var clone = (Options)MemberwiseClone();
        clone.Filters = new List<string>(Filters);
        return clone;
    }
}
