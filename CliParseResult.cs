using System.Collections.Generic;

/// <summary>
/// Outcome of parsing CLI args and environment variables into <see cref="Options"/>.
/// </summary>
internal sealed class CliParseResult
{
    public Options Options { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool HasErrors => Errors.Count > 0;
}

