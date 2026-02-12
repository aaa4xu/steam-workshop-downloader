using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Application entry point that wires CLI parsing, logging, Steam metadata lookup,
/// and depot-based workshop downloading together.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var parsed = OptionsParser.Parse(args);
        var options = parsed.Options;

        if (options.ShowHelp)
        {
            OptionsParser.PrintUsage(Console.Out);
            return 0;
        }

        if (parsed.HasErrors)
        {
            foreach (var error in parsed.Errors)
            {
                Console.Error.WriteLine(error);
            }
            OptionsParser.PrintUsage(Console.Error);
            return 2;
        }

        foreach (var warning in parsed.Warnings)
        {
            Console.Error.WriteLine(warning);
        }

        if (!options.IsValid)
        {
            Console.Error.WriteLine("Invalid arguments.");
            OptionsParser.PrintUsage(Console.Error);
            return 2;
        }

        var outputDir = Path.GetFullPath(options.OutputDir);
        Directory.CreateDirectory(outputDir);

        using var log = LogRouter.Attach(options.LogPath);

        try
        {
            return await RunBatchAsync(options, outputDir);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation canceled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static async Task<int> RunBatchAsync(Options options, string parentDir)
    {
        if (options.Command == CommandKind.Sync)
        {
            return await RunSyncAsync(options, parentDir);
        }

        List<ulong> ids;
        if (options.Command == CommandKind.ModsFile)
        {
            if (!File.Exists(options.IdListPath))
            {
                Console.Error.WriteLine($"ID list file not found: {options.IdListPath}");
                return 2;
            }

            ids = WorkshopIdListReader.ReadIds(options.IdListPath);
        }
        else if (options.Command == CommandKind.Mod)
        {
            ids = new List<ulong> { options.PublishedFileId };
        }
        else
        {
            Console.Error.WriteLine("No workshop id(s) provided.");
            return 2;
        }

        if (ids.Count == 0)
        {
            Console.Error.WriteLine("No valid workshop ids found in list.");
            return 2;
        }

        Console.WriteLine($"Batch list: {ids.Count} items");
        Console.WriteLine($"Output parent: {parentDir}");
        Console.WriteLine($"AppID: {options.AppId}");

        var invalidIds = new List<ulong>();
        var channel = Channel.CreateUnbounded<WorkshopItemRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        Console.WriteLine("Using workshop depot download (batch).");
        var downloader = new WorkshopDepotDownloader(options);
        var downloadTask = downloader.DownloadQueuedAsync(channel.Reader, parentDir);

        var lastRequestAt = DateTimeOffset.MinValue;
        for (var offset = 0; offset < ids.Count; offset += SteamWebApi.MaxPublishedFileDetailsBatchSize)
        {
            var count = Math.Min(SteamWebApi.MaxPublishedFileDetailsBatchSize, ids.Count - offset);
            var batchIds = new List<ulong>(count);
            for (var i = 0; i < count; i++)
            {
                batchIds.Add(ids[offset + i]);
            }

            lastRequestAt = await ThrottlePublishedFileDetailsAsync(lastRequestAt, TimeSpan.FromSeconds(2));

            var batchDetails = await SteamWebApi.FetchPublishedFileDetailsBatchAsync(batchIds, CancellationToken.None);
            var detailsMap = new Dictionary<ulong, PublishedFileDetails>();
            foreach (var details in batchDetails)
            {
                if (details.PublishedFileId != 0 && !detailsMap.ContainsKey(details.PublishedFileId))
                {
                    detailsMap.Add(details.PublishedFileId, details);
                }
            }

            foreach (var id in batchIds)
            {
                if (!detailsMap.TryGetValue(id, out var details))
                {
                    Console.Error.WriteLine($"Failed to resolve workshop details for {id}. Result=missing");
                    invalidIds.Add(id);
                    continue;
                }

                if (details.Result != 1)
                {
                    Console.Error.WriteLine($"Failed to resolve workshop details for {id}. Result={details.Result}");
                    invalidIds.Add(id);
                    continue;
                }
                if (details.HContentFile == 0)
                {
                    Console.Error.WriteLine($"Workshop item {id} has no hcontent_file (not SteamPipe workshop depot content?).");
                    invalidIds.Add(id);
                    continue;
                }

                if (details.ConsumerAppId != 0 && details.ConsumerAppId != options.AppId)
                {
                    Console.WriteLine($"Warning: workshop item appid {details.ConsumerAppId} differs from requested {options.AppId}.");
                }

                await channel.Writer.WriteAsync(new WorkshopItemRequest(id, details.HContentFile));
            }
        }

        channel.Writer.Complete();
        var result = await downloadTask;

        var failed = new List<ulong>(invalidIds);
        failed.AddRange(result.FailedIds);

        if (failed.Count > 0)
        {
            Console.Error.WriteLine($"Batch completed with failures: {string.Join(", ", failed)}");
            return 5;
        }

        Console.WriteLine("Batch done.");
        return 0;
    }

    private static async Task<int> RunSyncAsync(Options options, string parentDir)
    {
        if (string.IsNullOrWhiteSpace(options.WebApiKey))
        {
            Console.Error.WriteLine("Sync mode requires a Steam Web API key (--webapi-key or STEAM_WEBAPI_KEY).");
            return 2;
        }

        Console.WriteLine($"Sync appid: {options.SyncAppId}");
        Console.WriteLine($"Output parent: {parentDir}");

        var invalidIds = new List<ulong>();
        var seen = new HashSet<ulong>();
        var channel = Channel.CreateUnbounded<WorkshopItemRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        Console.WriteLine("Using workshop depot download (sync).");
        var downloader = new WorkshopDepotDownloader(options);
        var downloadTask = downloader.DownloadQueuedAsync(channel.Reader, parentDir);

        await foreach (var details in SteamWebApi.QueryFilesAsync(options.WebApiKey, options.SyncAppId, SteamWebApi.MaxPublishedFileDetailsBatchSize, CancellationToken.None))
        {
            if (details.PublishedFileId == 0)
            {
                continue;
            }

            if (!seen.Add(details.PublishedFileId))
            {
                continue;
            }

            if (details.Result != 1)
            {
                Console.Error.WriteLine($"Failed to resolve workshop details for {details.PublishedFileId}. Result={details.Result}");
                invalidIds.Add(details.PublishedFileId);
                continue;
            }

            if (details.HContentFile == 0)
            {
                Console.Error.WriteLine($"Workshop item {details.PublishedFileId} has no hcontent_file (not SteamPipe workshop depot content?).");
                invalidIds.Add(details.PublishedFileId);
                continue;
            }

            if (details.ConsumerAppId != 0 && details.ConsumerAppId != options.AppId)
            {
                Console.WriteLine($"Warning: workshop item appid {details.ConsumerAppId} differs from requested {options.AppId}.");
            }

            await channel.Writer.WriteAsync(new WorkshopItemRequest(details.PublishedFileId, details.HContentFile));
        }

        channel.Writer.Complete();
        var result = await downloadTask;

        var failed = new List<ulong>(invalidIds);
        failed.AddRange(result.FailedIds);

        if (failed.Count > 0)
        {
            Console.Error.WriteLine($"Sync completed with failures: {string.Join(", ", failed)}");
            return 5;
        }

        Console.WriteLine("Sync done.");
        return 0;
    }

    private static async Task<DateTimeOffset> ThrottlePublishedFileDetailsAsync(DateTimeOffset lastRequestAt, TimeSpan interval)
    {
        if (lastRequestAt != DateTimeOffset.MinValue)
        {
            var nextAllowed = lastRequestAt + interval;
            var now = DateTimeOffset.UtcNow;
            if (nextAllowed > now)
            {
                await Task.Delay(nextAllowed - now);
            }
        }

        return DateTimeOffset.UtcNow;
    }
}
