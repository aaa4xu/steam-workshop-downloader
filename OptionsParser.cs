using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// Parses CLI arguments and environment variables into <see cref="Options"/>.
/// </summary>
internal static class OptionsParser
{
    public static Options Parse(string[] args)
    {
        var options = new Options();
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var key = arg;
                string? value = null;
                var eqIndex = arg.IndexOf('=');
                if (eqIndex > 0)
                {
                    key = arg.Substring(0, eqIndex);
                    value = arg.Substring(eqIndex + 1);
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }

                switch (key.ToLowerInvariant())
                {
                    case "--appid":
                    case "--app-id":
                        options.AppId = ParseUInt(value, options.AppId);
                        break;
                    case "--user":
                    case "--username":
                        options.Username = value;
                        break;
                    case "--pass":
                    case "--password":
                        options.Password = value;
                        break;
                    case "--filter":
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            options.Filters.Add(value);
                        }
                        break;
                    case "--id-list":
                    case "--ids":
                    case "--batch":
                        options.IdListPath = value;
                        break;
                    case "--log":
                        options.LogPath = value;
                        break;
                    case "--auth-cache":
                    case "--cache":
                        options.AuthCachePath = value;
                        break;
                    case "--guard":
                    case "--steam-guard":
                        options.GuardCode = value;
                        break;
                    case "--email":
                    case "--email-guard":
                        options.EmailCode = value;
                        break;
                    case "--anonymous":
                    case "--anon":
                        options.UseAnonymous = true;
                        break;
                }
            }
            else
            {
                positional.Add(arg);
            }
        }

        if (positional.Count >= 2)
        {
            if (ulong.TryParse(positional[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                options.PublishedFileId = id;
                options.OutputDir = positional[1];
            }
            else if (File.Exists(positional[0]))
            {
                options.IdListPath = positional[0];
                options.OutputDir = positional[1];
            }
            else if (positional.Count >= 4)
            {
                if (ulong.TryParse(positional[3], NumberStyles.None, CultureInfo.InvariantCulture, out var altId))
                {
                    options.Username = positional[0];
                    options.Password = positional[1];
                    options.OutputDir = positional[2];
                    options.PublishedFileId = altId;
                }
                else if (File.Exists(positional[3]))
                {
                    options.Username = positional[0];
                    options.Password = positional[1];
                    options.OutputDir = positional[2];
                    options.IdListPath = positional[3];
                }
            }
        }

        options.Username ??= Environment.GetEnvironmentVariable("STEAM_USER");
        options.Password ??= Environment.GetEnvironmentVariable("STEAM_PASS");
        options.GuardCode ??= Environment.GetEnvironmentVariable("STEAM_GUARD");
        options.EmailCode ??= Environment.GetEnvironmentVariable("STEAM_EMAIL_GUARD");
        options.AuthCachePath ??= Environment.GetEnvironmentVariable("STEAM_AUTH_CACHE");
        options.LogPath ??= Environment.GetEnvironmentVariable("STEAM_LOG") ?? Environment.GetEnvironmentVariable("STEAM_WORKSHOP_DOWNLOADER_LOG");

        if (string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrWhiteSpace(options.Password))
        {
            options.UseAnonymous = true;
        }

        return options;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  steam-workshop-downloader <publishedFileId> <outputDir> [--appid <id>] [--anonymous] [--user <u>] [--pass <p>] [--guard <code>] [--email <code>]");
        Console.WriteLine("  steam-workshop-downloader <user> <pass> <outputDir> <publishedFileId> [--appid <id>] [--filter <glob>] [--log <path>] [--auth-cache <path>]");
        Console.WriteLine("  steam-workshop-downloader <idListFile.txt> <outputDir> [--appid <id>] [--filter <glob>] [--log <path>] [--auth-cache <path>]");
        Console.WriteLine("  steam-workshop-downloader <user> <pass> <outputDir> <idListFile.txt> [--appid <id>] [--filter <glob>] [--log <path>] [--auth-cache <path>]");
        Console.WriteLine();
        Console.WriteLine("Batch mode writes each workshop item into a subfolder named after its id under outputDir.");
        Console.WriteLine();
        Console.WriteLine("Environment variables:");
        Console.WriteLine("  STEAM_USER, STEAM_PASS, STEAM_GUARD, STEAM_EMAIL_GUARD");
        Console.WriteLine("  STEAM_AUTH_CACHE, STEAM_LOG, STEAM_WORKSHOP_DOWNLOADER_LOG");
    }

    private static uint ParseUInt(string? value, uint fallback)
    {
        if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return fallback;
    }
}
