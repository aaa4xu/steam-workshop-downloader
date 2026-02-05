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
        var options = OptionsParser.Parse(args);
        if (!options.IsValid)
        {
            OptionsParser.PrintUsage();
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
        List<ulong> ids;
        if (!string.IsNullOrWhiteSpace(options.IdListPath))
        {
            if (!File.Exists(options.IdListPath))
            {
                Console.Error.WriteLine($"ID list file not found: {options.IdListPath}");
                return 2;
            }

            ids = WorkshopIdListReader.ReadIds(options.IdListPath);
        }
        else if (options.PublishedFileId != 0)
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
        var channel = Channel.CreateUnbounded<ulong>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        Console.WriteLine("Using workshop depot download (batch).");
        var downloader = new WorkshopDepotDownloader(options);
        var downloadTask = downloader.DownloadQueuedAsync(channel.Reader, parentDir);

        var lastRequestAt = DateTimeOffset.MinValue;
        foreach (var id in ids)
        {
            lastRequestAt = await ThrottlePublishedFileDetailsAsync(lastRequestAt, TimeSpan.FromSeconds(2));

            Console.WriteLine($"Workshop item: {id}");
            var details = await SteamWebApi.FetchPublishedFileDetailsAsync(id, CancellationToken.None);
            if (details.Result != 1)
            {
                Console.Error.WriteLine($"Failed to resolve workshop details for {id}. Result={details.Result}");
                invalidIds.Add(id);
                continue;
            }

            Console.WriteLine($"Title: {details.Title}");
            if (details.ConsumerAppId != 0 && details.ConsumerAppId != options.AppId)
            {
                Console.WriteLine($"Warning: workshop item appid {details.ConsumerAppId} differs from requested {options.AppId}.");
            }
            Console.WriteLine($"UGC handle: {details.HContentFile}");

            await channel.Writer.WriteAsync(id);
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
