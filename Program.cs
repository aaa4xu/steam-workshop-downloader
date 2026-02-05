using System;
using System.IO;
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
            Console.WriteLine($"Workshop item: {options.PublishedFileId}");
            Console.WriteLine($"Output: {outputDir}");
            Console.WriteLine($"AppID: {options.AppId}");

            var publishedDetails = await SteamWebApi.FetchPublishedFileDetailsAsync(options.PublishedFileId, CancellationToken.None);
            if (publishedDetails.Result != 1)
            {
                Console.Error.WriteLine($"Failed to resolve workshop details. Result={publishedDetails.Result}");
                return 3;
            }

            Console.WriteLine($"Title: {publishedDetails.Title}");
            if (publishedDetails.ConsumerAppId != 0 && publishedDetails.ConsumerAppId != options.AppId)
            {
                Console.WriteLine($"Warning: workshop item appid {publishedDetails.ConsumerAppId} differs from requested {options.AppId}.");
            }
            Console.WriteLine($"UGC handle: {publishedDetails.HContentFile}");

            Console.WriteLine("Using workshop depot download.");
            var downloader = new WorkshopDepotDownloader(options);
            var depotOk = await downloader.DownloadAsync(publishedDetails.PublishedFileId, outputDir);
            if (!depotOk)
            {
                Console.Error.WriteLine("Workshop depot download failed.");
                return 4;
            }

            Console.WriteLine("Done.");
            return 0;
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
}
