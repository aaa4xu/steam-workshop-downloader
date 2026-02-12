using System.Collections.Generic;

/// <summary>
/// Parsed command-line and environment configuration used across the app.
/// </summary>
internal sealed class Options
{
    public bool ShowHelp { get; set; }
    public CommandKind Command { get; set; } = CommandKind.None;
    public ulong PublishedFileId { get; set; }
    public string? IdListPath { get; set; }
    public uint SyncAppId { get; set; }
    public string OutputDir { get; set; } = string.Empty;
    public uint AppId { get; set; } = 268500; // XCOM 2 default
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? GuardCode { get; set; }
    public string? EmailCode { get; set; }
    public string? WebApiKey { get; set; }
    public string? LogPath { get; set; }
    public string? AuthCachePath { get; set; }
    public List<string> Filters { get; set; } = new();
    public bool UseAnonymous { get; set; }

    public bool IsValid => Command switch
    {
        CommandKind.Mod => PublishedFileId != 0 && !string.IsNullOrWhiteSpace(OutputDir),
        CommandKind.ModsFile => !string.IsNullOrWhiteSpace(IdListPath) && !string.IsNullOrWhiteSpace(OutputDir),
        CommandKind.Sync => SyncAppId != 0 && !string.IsNullOrWhiteSpace(OutputDir),
        _ => false
    };

    public bool IsBatch => Command is CommandKind.ModsFile or CommandKind.Sync;
}
