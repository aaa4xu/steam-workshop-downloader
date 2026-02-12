using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// Parses CLI arguments and environment variables into <see cref="Options"/>.
/// </summary>
internal static class OptionsParser
{
    public static CliParseResult Parse(string[] args)
    {
        var result = new CliParseResult();
        var options = result.Options;
        var positional = new List<string>();
        var appIdSpecified = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsHelpToken(arg))
            {
                options.ShowHelp = true;
                return result;
            }

            if (string.Equals(arg, "--", StringComparison.Ordinal))
            {
                // End of options marker; everything after is positional.
                for (var j = i + 1; j < args.Length; j++)
                {
                    positional.Add(args[j]);
                }
                break;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                ParseLongOption(arg, args, ref i, options, result, ref appIdSpecified);
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                result.Errors.Add($"Unknown option: '{arg}'.");
                continue;
            }

            positional.Add(arg);
        }

        ParseCommand(positional, options, result);

        options.Username ??= Environment.GetEnvironmentVariable("STEAM_USER");
        options.Password ??= Environment.GetEnvironmentVariable("STEAM_PASS");
        options.GuardCode ??= Environment.GetEnvironmentVariable("STEAM_GUARD");
        options.EmailCode ??= Environment.GetEnvironmentVariable("STEAM_EMAIL_GUARD");
        options.WebApiKey ??= Environment.GetEnvironmentVariable("STEAM_WEBAPI_KEY");
        options.AuthCachePath ??= Environment.GetEnvironmentVariable("STEAM_AUTH_CACHE");
        options.LogPath ??= Environment.GetEnvironmentVariable("STEAM_LOG") ?? Environment.GetEnvironmentVariable("STEAM_WORKSHOP_DOWNLOADER_LOG");

        if (string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrWhiteSpace(options.Password))
        {
            options.UseAnonymous = true;
        }

        if (options.Command == CommandKind.Sync && options.SyncAppId != 0)
        {
            if (!appIdSpecified)
            {
                options.AppId = options.SyncAppId;
            }
            else if (options.AppId != options.SyncAppId)
            {
                result.Warnings.Add($"Warning: sync <appId> is {options.SyncAppId}, but --appid was set to {options.AppId} (used for depot lookup).");
            }

            if (string.IsNullOrWhiteSpace(options.WebApiKey))
            {
                result.Errors.Add("Sync mode requires a Steam Web API key (--webapi-key or STEAM_WEBAPI_KEY).");
            }
        }

        return result;
    }

    public static void PrintUsage()
    {
        PrintUsage(Console.Out);
    }

    public static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Usage:");
        output.WriteLine("  steam-workshop-downloader mod <publishedFileId> <outputDir> [options]");
        output.WriteLine("  steam-workshop-downloader mods-file <idListFile.txt> <outputDir> [options]");
        output.WriteLine("  steam-workshop-downloader sync <appId> <outputDir> --webapi-key <key> [options]");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --appid <id>        Consumer app id for workshop depot lookup (default: 268500).");
        output.WriteLine("  --anonymous         Try anonymous login first (falls back to credentials if available).");
        output.WriteLine("  --user <u>          Steam username (or STEAM_USER).");
        output.WriteLine("  --pass <p>          Steam password (or STEAM_PASS).");
        output.WriteLine("  --guard <code>      Steam Guard code (or STEAM_GUARD).");
        output.WriteLine("  --email <code>      Email Steam Guard code (or STEAM_EMAIL_GUARD).");
        output.WriteLine("  --webapi-key <key>  Steam Web API key (or STEAM_WEBAPI_KEY). Required for sync.");
        output.WriteLine("  --filter <glob>     Repeatable; case-insensitive glob filter.");
        output.WriteLine("  --log <path>        Log file path (or STEAM_LOG / STEAM_WORKSHOP_DOWNLOADER_LOG).");
        output.WriteLine("  --auth-cache <path> Auth cache file/dir (or STEAM_AUTH_CACHE).");
        output.WriteLine("  --help, -h, /?      Show this help.");
        output.WriteLine();
        output.WriteLine("Batch mode writes each workshop item into a subfolder named after its id under outputDir.");
        output.WriteLine();
        output.WriteLine("Environment variables:");
        output.WriteLine("  STEAM_USER, STEAM_PASS, STEAM_GUARD, STEAM_EMAIL_GUARD");
        output.WriteLine("  STEAM_AUTH_CACHE, STEAM_LOG, STEAM_WORKSHOP_DOWNLOADER_LOG, STEAM_WEBAPI_KEY");
    }

    private static bool IsHelpToken(string arg)
    {
        return string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase);
    }

    private static void ParseLongOption(string arg, string[] args, ref int i, Options options, CliParseResult result, ref bool appIdSpecified)
    {
        var key = arg;
        string? value = null;
        var eqIndex = arg.IndexOf('=');
        if (eqIndex > 0)
        {
            key = arg.Substring(0, eqIndex);
            value = arg.Substring(eqIndex + 1);
        }

        var normalizedKey = key.ToLowerInvariant();

        // Known boolean flags. Important: never consume the next token as a value.
        if (normalizedKey is "--anonymous" or "--anon")
        {
            if (eqIndex > 0)
            {
                result.Errors.Add($"Flag '{key}' does not take a value.");
            }
            options.UseAnonymous = true;
            return;
        }

        var expectsValue = normalizedKey is "--appid" or "--app-id"
            or "--user" or "--username"
            or "--pass" or "--password"
            or "--filter"
            or "--webapi-key" or "--api-key"
            or "--log"
            or "--auth-cache" or "--cache"
            or "--guard" or "--steam-guard"
            or "--email" or "--email-guard";

        if (!expectsValue)
        {
            result.Errors.Add($"Unknown option: '{key}'.");
            return;
        }

        if (value is null)
        {
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result.Errors.Add($"Option '{key}' requires a value.");
                return;
            }

            value = args[++i];
        }

        switch (normalizedKey)
        {
            case "--appid":
            case "--app-id":
                if (!TryParseUInt(value, out var appId) || appId == 0)
                {
                    result.Errors.Add($"Invalid app id: '{value}'.");
                    return;
                }
                options.AppId = appId;
                appIdSpecified = true;
                return;
            case "--user":
            case "--username":
                if (string.IsNullOrWhiteSpace(value))
                {
                    result.Errors.Add($"Option '{key}' requires a non-empty value.");
                    return;
                }
                options.Username = value;
                return;
            case "--pass":
            case "--password":
                if (string.IsNullOrWhiteSpace(value))
                {
                    result.Errors.Add($"Option '{key}' requires a non-empty value.");
                    return;
                }
                options.Password = value;
                return;
            case "--filter":
                if (string.IsNullOrWhiteSpace(value))
                {
                    result.Errors.Add($"Option '{key}' requires a non-empty value.");
                    return;
                }
                options.Filters.Add(value);
                return;
            case "--webapi-key":
            case "--api-key":
                if (string.IsNullOrWhiteSpace(value))
                {
                    result.Errors.Add($"Option '{key}' requires a non-empty value.");
                    return;
                }
                options.WebApiKey = value;
                return;
            case "--log":
                if (string.IsNullOrWhiteSpace(value))
                {
                    result.Errors.Add($"Option '{key}' requires a non-empty value.");
                    return;
                }
                options.LogPath = value;
                return;
            case "--auth-cache":
            case "--cache":
                if (string.IsNullOrWhiteSpace(value))
                {
                    result.Errors.Add($"Option '{key}' requires a non-empty value.");
                    return;
                }
                options.AuthCachePath = value;
                return;
            case "--guard":
            case "--steam-guard":
                if (string.IsNullOrWhiteSpace(value))
                {
                    result.Errors.Add($"Option '{key}' requires a non-empty value.");
                    return;
                }
                options.GuardCode = value;
                return;
            case "--email":
            case "--email-guard":
                if (string.IsNullOrWhiteSpace(value))
                {
                    result.Errors.Add($"Option '{key}' requires a non-empty value.");
                    return;
                }
                options.EmailCode = value;
                return;
        }
    }

    private static void ParseCommand(IReadOnlyList<string> positional, Options options, CliParseResult result)
    {
        if (positional.Count == 0)
        {
            result.Errors.Add("Missing command. Expected 'mod', 'mods-file', or 'sync'.");
            return;
        }

        var rawCmd = positional[0];
        var cmd = rawCmd.Trim().ToLowerInvariant();
        switch (cmd)
        {
            case "mod":
                options.Command = CommandKind.Mod;
                if (positional.Count != 3)
                {
                    result.Errors.Add("'mod' requires exactly 2 arguments: <publishedFileId> <outputDir>.");
                    return;
                }
                if (!TryParseUlong(positional[1], out var publishedFileId) || publishedFileId == 0)
                {
                    result.Errors.Add($"Invalid published file id: '{positional[1]}'.");
                    return;
                }
                options.PublishedFileId = publishedFileId;
                options.OutputDir = positional[2];
                return;

            case "mods-file":
            case "mods":
            case "batch":
                if (cmd != "mods-file")
                {
                    result.Warnings.Add($"Warning: command '{rawCmd}' is an alias; prefer 'mods-file'.");
                }
                options.Command = CommandKind.ModsFile;
                if (positional.Count != 3)
                {
                    result.Errors.Add("'mods-file' requires exactly 2 arguments: <idListFile.txt> <outputDir>.");
                    return;
                }
                options.IdListPath = positional[1];
                options.OutputDir = positional[2];
                return;

            case "sync":
                options.Command = CommandKind.Sync;
                if (positional.Count != 3)
                {
                    result.Errors.Add("'sync' requires exactly 2 arguments: <appId> <outputDir>.");
                    return;
                }
                if (!TryParseUInt(positional[1], out var syncAppId) || syncAppId == 0)
                {
                    result.Errors.Add($"Invalid app id: '{positional[1]}'.");
                    return;
                }
                options.SyncAppId = syncAppId;
                options.OutputDir = positional[2];
                return;

            default:
                result.Errors.Add($"Unknown command: '{rawCmd}'. Expected 'mod', 'mods-file', or 'sync'.");
                AddLegacySyntaxHint(positional, result);
                return;
        }
    }

    private static void AddLegacySyntaxHint(IReadOnlyList<string> positional, CliParseResult result)
    {
        if (positional.Count == 2 && TryParseUlong(positional[0], out var legacyId) && legacyId != 0)
        {
            result.Errors.Add("Hint: CLI now requires a subcommand. Try: steam-workshop-downloader mod <publishedFileId> <outputDir>.");
            return;
        }

        if (positional.Count == 2)
        {
            result.Errors.Add("Hint: CLI now requires a subcommand. Try: steam-workshop-downloader mods-file <idListFile.txt> <outputDir>.");
            return;
        }

        if (positional.Count == 4 && TryParseUlong(positional[3], out var legacyModId) && legacyModId != 0)
        {
            result.Errors.Add("Hint: credentials are now flags. Try: steam-workshop-downloader mod <publishedFileId> <outputDir> --user <u> --pass <p>.");
            return;
        }

        if (positional.Count == 4)
        {
            result.Errors.Add("Hint: credentials are now flags. Try: steam-workshop-downloader mods-file <idListFile.txt> <outputDir> --user <u> --pass <p>.");
        }
    }

    private static bool TryParseUInt(string? value, out uint parsed)
    {
        return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool TryParseUlong(string? value, out ulong parsed)
    {
        return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed);
    }
}
