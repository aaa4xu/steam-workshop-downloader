using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Threading.Channels;
using SteamKit2;
using SteamKit2.CDN;
using SteamKit2.Internal;

/// <summary>
/// Downloads workshop items via SteamPipe depots, including optional file filtering,
/// hashing for reuse, and an atomic swap into the target directory.
/// </summary>
internal sealed class WorkshopDepotDownloader
{
    private readonly Options _options;
    private const int MaxDownloadAttempts = 3;
    private static readonly TimeSpan ManifestDownloadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ManifestTimeoutCooldownBase = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ManifestTimeoutCooldownMax = TimeSpan.FromSeconds(120);

    public WorkshopDepotDownloader(Options options)
    {
        _options = options;
    }

    public async Task<bool> DownloadAsync(ulong publishedFileId, string outputDir)
    {
        var session = new SteamSession(_options);
        await session.ConnectAsync();
        await session.LogOnAsync();

        try
        {
            var details = await SteamWebApi.FetchPublishedFileDetailsAsync(publishedFileId, default);
            if (details.Result != 1)
            {
                Console.Error.WriteLine($"Failed to resolve workshop details for {publishedFileId}. Result={details.Result}");
                return false;
            }

            if (details.HContentFile == 0)
            {
                Console.Error.WriteLine($"Workshop item {publishedFileId} has no hcontent_file (not SteamPipe workshop depot content?).");
                return false;
            }

            return await DownloadWithSessionAsync(session, publishedFileId, details.HContentFile, outputDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return false;
        }
        finally
        {
            await session.LogOffAsync();
        }
    }

    /// <summary>
    /// Consumes a queue of workshop IDs and downloads them sequentially using one Steam session.
    /// </summary>
    public async Task<BatchDownloadResult> DownloadQueuedAsync(ChannelReader<WorkshopItemRequest> reader, string parentDir)
    {
        var session = new SteamSession(_options);
        await session.ConnectAsync();
        await session.LogOnAsync();

        var manifestChannel = Channel.CreateUnbounded<ManifestDownloadResult>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        try
        {
            var manifestTask = EnqueueManifestsAsync(reader, manifestChannel.Writer, session, parentDir);
            var downloadTask = ConsumeManifestsAsync(manifestChannel.Reader, session);

            var manifestResult = await manifestTask;
            var downloadResult = await downloadTask;

            var failed = new List<ulong>(manifestResult.FailedIds);
            failed.AddRange(downloadResult.FailedIds);

            return new BatchDownloadResult(manifestResult.Processed, failed);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
        }
        finally
        {
            await session.LogOffAsync();
        }
        return new BatchDownloadResult(0, new List<ulong>());
    }

    private async Task<bool> DownloadWithSessionAsync(SteamSession session, ulong publishedFileId, ulong manifestId, string outputDir)
    {
        var manifest = await FetchManifestAsync(session, publishedFileId, outputDir, manifestId);
        if (manifest == null)
        {
            return false;
        }

        return await DownloadFromManifestAsync(session, manifest);
    }

    private async Task<ManifestStageResult> EnqueueManifestsAsync(
        ChannelReader<WorkshopItemRequest> reader,
        ChannelWriter<ManifestDownloadResult> writer,
        SteamSession session,
        string parentDir)
    {
        var failed = new List<ulong>();
        var processed = 0;
        var cooldownState = new ManifestCooldownState();

        try
        {
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var item))
                {
                    processed++;
                    var id = item.PublishedFileId;
                    var manifestId = item.ManifestId;
                    var itemDir = Path.Combine(parentDir, id.ToString(CultureInfo.InvariantCulture));

                    if (manifestId == 0)
                    {
                        Console.Error.WriteLine($"Workshop item {id} missing manifest id.");
                        failed.Add(id);
                        continue;
                    }

                    var state = TryLoadState(itemDir);
                    if (state != null && state.ManifestId == manifestId)
                    {
                        if (IsStateUpToDate(state, itemDir, _options.Filters))
                        {
                            Console.WriteLine($"State up-to-date for {id}, skipping download.");
                            continue;
                        }

                        Console.WriteLine($"State exists for {id}, but local files differ. Re-downloading.");
                    }

                    var ok = false;
                    for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
                    {
                        if (attempt > 1)
                        {
                            Console.WriteLine($"Retry {attempt}/{MaxDownloadAttempts} for {id} (manifest)...");
                        }

                        try
                        {
                            await WaitForCooldownAsync(cooldownState.CooldownUntil);

                            var manifest = await FetchManifestAsync(session, id, itemDir, manifestId);
                            if (manifest != null)
                            {
                                await writer.WriteAsync(manifest);
                                cooldownState.ConsecutiveTimeouts = 0;
                                ok = true;
                                break;
                            }
                        }
                        catch (SteamKitWebRequestException ex)
                        {
                            LogWebRequestException(ex, $"Manifest download failed for {id}");
                            var delay = TryGetRateLimitDelay(ex);
                            if (delay.HasValue)
                            {
                                cooldownState.CooldownUntil = DateTimeOffset.UtcNow + delay.Value;
                                Console.WriteLine($"Rate limit detected. Cooling down for {delay.Value.TotalSeconds:0} sec...");
                            }
                        }
                        catch (TimeoutException ex)
                        {
                            cooldownState.ConsecutiveTimeouts++;
                            Console.WriteLine($"Manifest download timed out for {id}: {ex.Message}");
                            var delay = GetTimeoutCooldown(cooldownState.ConsecutiveTimeouts);
                            if (delay.HasValue)
                            {
                                cooldownState.CooldownUntil = DateTimeOffset.UtcNow + delay.Value;
                                Console.WriteLine($"Cooling down for {delay.Value.TotalSeconds:0} sec after {cooldownState.ConsecutiveTimeouts} timeouts...");
                            }
                        }
                        catch (AsyncJobFailedException ex)
                        {
                            Console.WriteLine($"Manifest request failed for {id}: {DescribeAsyncJobFailure(ex)}");
                            cooldownState.CooldownUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Manifest fetch failed for {id} on attempt {attempt}: {ex.Message}");
                        }

                        if (attempt < MaxDownloadAttempts)
                        {
                            var delay = TimeSpan.FromSeconds(Math.Min(30, attempt * 10));
                            Console.WriteLine($"Retrying manifest in {delay.TotalSeconds:0} sec...");
                            await Task.Delay(delay);
                        }
                    }

                    if (!ok)
                    {
                        failed.Add(id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
        }
        finally
        {
            writer.Complete();
        }

        return new ManifestStageResult(processed, failed);
    }

    private async Task<DownloadStageResult> ConsumeManifestsAsync(ChannelReader<ManifestDownloadResult> reader, SteamSession session)
    {
        var failed = new List<ulong>();

        try
        {
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var manifest))
                {
                    var ok = false;
                    for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
                    {
                        if (attempt > 1)
                        {
                            Console.WriteLine($"Retry {attempt}/{MaxDownloadAttempts} for {manifest.PublishedFileId} (download)...");
                        }

                        try
                        {
                            ok = await DownloadFromManifestAsync(session, manifest);
                        }
                        catch (AsyncJobFailedException ex)
                        {
                            Console.Error.WriteLine($"Download failed for {manifest.PublishedFileId} on attempt {attempt}: {DescribeAsyncJobFailure(ex)}");
                            ok = false;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Download failed for {manifest.PublishedFileId} on attempt {attempt}: {ex.Message}");
                            ok = false;
                        }

                        if (ok)
                        {
                            break;
                        }

                        if (attempt < MaxDownloadAttempts)
                        {
                            var delay = TimeSpan.FromSeconds(Math.Min(10, attempt * 2));
                            Console.WriteLine($"Retrying download in {delay.TotalSeconds:0} sec...");
                            await Task.Delay(delay);
                        }
                    }

                    if (!ok)
                    {
                        failed.Add(manifest.PublishedFileId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
        }

        return new DownloadStageResult(failed);
    }

    private async Task<ManifestDownloadResult?> FetchManifestAsync(SteamSession session, ulong publishedFileId, string outputDir, ulong manifestId)
    {
        var depotId = await GetWorkshopDepotIdAsync(session);
        if (depotId == 0)
        {
            Console.WriteLine("Workshop depot id not found.");
            return null;
        }

        var depotKey = await GetDepotKeyAsync(session, depotId);
        if (depotKey == null || depotKey.Length == 0)
        {
            Console.WriteLine("Depot key not available.");
            return null;
        }

        var servers = await session.Content.GetServersForSteamPipe();
        var server = PickServer(servers);
        if (server == null || string.IsNullOrWhiteSpace(server.Host))
        {
            Console.WriteLine("No CDN servers available.");
            return null;
        }

        string? cdnAuthToken = null;
        try
        {
            var token = await session.Content.GetCDNAuthToken(_options.AppId, depotId, server.Host!);
            if (token.Result == EResult.OK)
            {
                cdnAuthToken = token.Token;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CDN auth token request failed: {ex.Message}");
        }

        ulong manifestRequestCode = 0;
        try
        {
            manifestRequestCode = await session.Content.GetManifestRequestCode(depotId, _options.AppId, manifestId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Manifest request code failed: {ex.Message}");
        }

        using var cdn = new Client(session.Client);
        var manifestTask = cdn.DownloadManifestAsync(depotId, manifestId, manifestRequestCode, server, depotKey, null, cdnAuthToken);
        var manifest = await manifestTask.WaitAsync(ManifestDownloadTimeout);

        if (manifest.FilenamesEncrypted)
        {
            manifest.DecryptFilenames(depotKey);
        }

        LogManifestFileList(manifest);

        return new ManifestDownloadResult(publishedFileId, outputDir, manifestId, depotId, depotKey, server, cdnAuthToken, manifest);
    }

    private async Task<bool> DownloadFromManifestAsync(SteamSession session, ManifestDownloadResult manifestResult)
    {
        var manifest = manifestResult.Manifest;
        var selectedFiles = SelectManifestFiles(manifest, _options.Filters);
        if (_options.Filters.Count > 0)
        {
            LogFilteredFileList(selectedFiles, _options.Filters);
        }

        var tempDir = PrepareTempDirectory(manifestResult.OutputDir);
        var plan = BuildDownloadPlan(selectedFiles, manifestResult.OutputDir);

        Console.WriteLine($"Files selected: {selectedFiles.Count}");
        Console.WriteLine($"Files to copy: {plan.CopyFiles.Count}");
        Console.WriteLine($"Files to download: {plan.DownloadFiles.Count}");

        CopyUnchangedFiles(plan.CopyFiles, tempDir);

        using var cdn = new Client(session.Client);
        await DownloadManifestFilesAsync(cdn, manifestResult.DepotId, manifestResult.DepotKey, manifestResult.Server, manifestResult.CdnAuthToken, tempDir, plan.DownloadFiles);

        SaveState(manifestResult, tempDir);

        SwapDirectories(manifestResult.OutputDir, tempDir);
        return true;
    }

    private async Task<uint> GetWorkshopDepotIdAsync(SteamSession session)
    {
        var accessToken = 0UL;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var job = session.Apps.PICSGetProductInfo(new SteamApps.PICSRequest(_options.AppId, accessToken), null);
            job.Timeout = TimeSpan.FromSeconds(60);
            var resultSet = await job.ToTask().WaitAsync(TimeSpan.FromSeconds(65));
            if (resultSet.Results == null)
            {
                continue;
            }

            foreach (var callback in resultSet.Results)
            {
                if (!callback.Apps.TryGetValue(_options.AppId, out var appInfo))
                {
                    continue;
                }

                if (appInfo.MissingToken && accessToken == 0)
                {
                    accessToken = await GetPicsAccessTokenAsync(session);
                    break;
                }

                var depotId = FindWorkshopDepotId(appInfo.KeyValues);
                if (depotId.HasValue)
                {
                    Console.WriteLine($"Workshop depot id: {depotId.Value}");
                    return depotId.Value;
                }
            }

            if (accessToken == 0)
            {
                break;
            }
        }

        return 0;
    }

    private async Task<ulong> GetPicsAccessTokenAsync(SteamSession session)
    {
        var job = session.Apps.PICSGetAccessTokens(_options.AppId, null);
        job.Timeout = TimeSpan.FromSeconds(30);
        try
        {
            var response = await job.ToTask().WaitAsync(TimeSpan.FromSeconds(35));
            if (response.AppTokens.TryGetValue(_options.AppId, out var token))
            {
                return token;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PICS access token request failed: {ex.Message}");
        }

        return 0;
    }

    private async Task<byte[]?> GetDepotKeyAsync(SteamSession session, uint depotId)
    {
        var job = session.Apps.GetDepotDecryptionKey(depotId, _options.AppId);
        job.Timeout = TimeSpan.FromSeconds(30);
        try
        {
            var response = await job.ToTask().WaitAsync(TimeSpan.FromSeconds(35));
            Console.WriteLine($"Depot key result: {response.Result}");
            if (response.Result != EResult.OK)
            {
                return null;
            }

            return response.DepotKey;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Depot key request failed: {ex.Message}");
            return null;
        }
    }

    private async Task DownloadManifestFilesAsync(
        Client cdn,
        uint depotId,
        byte[] depotKey,
        Server server,
        string? cdnAuthToken,
        string outputDir,
        IReadOnlyList<DepotManifest.FileData> filesToDownload)
    {
        if (filesToDownload.Count == 0)
        {
            return;
        }

        var destinationRoot = Path.GetFullPath(outputDir);
        foreach (var file in filesToDownload)
        {
            if ((file.Flags & EDepotFileFlag.Directory) != 0)
            {
                var relPath = NormalizeManifestPath(file.FileName);
                if (string.IsNullOrWhiteSpace(relPath))
                {
                    continue;
                }
                var dirPath = GetSafePath(destinationRoot, relPath);
                Directory.CreateDirectory(dirPath);
                continue;
            }

            var targetRel = NormalizeManifestPath(file.FileName);
            if (string.IsNullOrWhiteSpace(targetRel))
            {
                continue;
            }
            var targetPath = GetSafePath(destinationRoot, targetRel);

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
            foreach (var chunk in file.Chunks)
            {
                if (chunk.ChunkID == null)
                {
                    continue;
                }

                if (chunk.UncompressedLength > int.MaxValue)
                {
                    throw new InvalidOperationException($"Chunk too large: {chunk.UncompressedLength} bytes");
                }

                // SteamPipe encrypts/compresses per chunk; we must download, decrypt, and write each chunk whole.
                var buffer = new byte[(int)chunk.UncompressedLength];
                var written = await cdn.DownloadDepotChunkAsync(depotId, chunk, server, buffer, depotKey, null, cdnAuthToken);

                fs.Position = (long)chunk.Offset;
                await fs.WriteAsync(buffer.AsMemory(0, written));
            }
        }
    }

    private static void LogManifestFileList(DepotManifest manifest)
    {
        if (manifest.Files == null)
        {
            Console.WriteLine("Depot manifest files: 0");
            return;
        }

        Console.WriteLine($"Depot manifest files: {manifest.Files.Count}");
        Console.WriteLine($"Depot manifest total size (uncompressed): {manifest.TotalUncompressedSize:N0} bytes");

        foreach (var file in manifest.Files)
        {
            var isDir = (file.Flags & EDepotFileFlag.Directory) != 0;
            var name = file.FileName ?? string.Empty;
            if (isDir && !name.EndsWith("/", StringComparison.Ordinal))
            {
                name += "/";
            }

            Console.WriteLine($"[manifest] {(isDir ? "DIR " : "FILE")} {file.TotalSize,12:N0} {name}");
        }
    }

    private static void LogFilteredFileList(List<DepotManifest.FileData> files, List<string> filters)
    {
        Console.WriteLine($"Filters: {string.Join(", ", filters)}");
        Console.WriteLine($"Filtered files: {files.Count}");
        foreach (var file in files)
        {
            var name = file.FileName ?? string.Empty;
            Console.WriteLine($"[filtered] {file.TotalSize,12:N0} {name}");
        }
    }

    private static List<DepotManifest.FileData> SelectManifestFiles(DepotManifest manifest, List<string> filters)
    {
        var result = new List<DepotManifest.FileData>();
        if (manifest.Files == null)
        {
            return result;
        }

        if (filters.Count == 0)
        {
            foreach (var file in manifest.Files)
            {
                if ((file.Flags & EDepotFileFlag.Directory) == 0)
                {
                    result.Add(file);
                }
            }
            return result;
        }

        // Filters are case-insensitive and normalized to forward slashes before matching.
        var regexes = BuildFilterRegexes(filters);
        foreach (var file in manifest.Files)
        {
            if ((file.Flags & EDepotFileFlag.Directory) != 0)
            {
                continue;
            }

            var normalized = NormalizeManifestPath(file.FileName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (MatchesAnyFilter(normalized, regexes))
            {
                result.Add(file);
            }
        }

        return result;
    }

    /// <summary>
    /// Split between files that can be reused from disk and files that must be downloaded.
    /// </summary>
    private sealed class DownloadPlan
    {
        public List<DepotManifest.FileData> DownloadFiles { get; } = new();
        public List<CopyPlanItem> CopyFiles { get; } = new();
    }

    /// <summary>
    /// Represents a verified local file that should be copied into the temp directory.
    /// </summary>
    private sealed class CopyPlanItem
    {
        public string SourcePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
    }

    private static DownloadPlan BuildDownloadPlan(List<DepotManifest.FileData> files, string outputDir)
    {
        var plan = new DownloadPlan();
        foreach (var file in files)
        {
            var relPath = NormalizeManifestPath(file.FileName);
            if (string.IsNullOrWhiteSpace(relPath))
            {
                continue;
            }

            var sourcePath = GetSafePath(outputDir, relPath);
            if (!File.Exists(sourcePath))
            {
                plan.DownloadFiles.Add(file);
                continue;
            }

            var fileInfo = new FileInfo(sourcePath);
            var expectedSize = ClampToLong(file.TotalSize);
            if (fileInfo.Length != expectedSize)
            {
                plan.DownloadFiles.Add(file);
                continue;
            }

            if (file.FileHash == null || file.FileHash.Length == 0)
            {
                plan.DownloadFiles.Add(file);
                continue;
            }

            // SHA-1 is provided by the manifest; compare to avoid re-downloading unchanged files.
            var localHash = ComputeSha1(sourcePath);
            if (!HashEquals(localHash, file.FileHash))
            {
                plan.DownloadFiles.Add(file);
                continue;
            }

            plan.CopyFiles.Add(new CopyPlanItem
            {
                SourcePath = sourcePath,
                RelativePath = relPath
            });
        }

        return plan;
    }

    private bool IsStateUpToDate(WorkshopState state, string outputDir, List<string> filters)
    {
        if (!Directory.Exists(outputDir))
        {
            return false;
        }

        var expectedFiles = SelectStateFiles(state.Files, filters);
        var expectedMap = new Dictionary<string, WorkshopStateFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in expectedFiles)
        {
            var normalized = NormalizeManifestPath(file.Path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (!expectedMap.ContainsKey(normalized))
            {
                expectedMap.Add(normalized, file);
            }
        }

        if (expectedMap.Count == 0)
        {
            foreach (var filePath in Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories))
            {
                var relPath = NormalizeManifestPath(Path.GetRelativePath(outputDir, filePath));
                if (string.Equals(relPath, ".state.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        foreach (var kvp in expectedMap)
        {
            var stateFile = kvp.Value;
            if (string.IsNullOrWhiteSpace(stateFile.Sha1))
            {
                return false;
            }

            var fullPath = GetSafePath(outputDir, kvp.Key);
            if (!File.Exists(fullPath))
            {
                return false;
            }

            var info = new FileInfo(fullPath);
            if ((ulong)info.Length != stateFile.Size)
            {
                return false;
            }

            byte[] expectedHash;
            try
            {
                expectedHash = Convert.FromHexString(stateFile.Sha1);
            }
            catch
            {
                return false;
            }

            var localHash = ComputeSha1(fullPath);
            if (!HashEquals(localHash, expectedHash))
            {
                return false;
            }
        }

        return true;
    }

    private static List<WorkshopStateFile> SelectStateFiles(List<WorkshopStateFile> files, List<string> filters)
    {
        if (filters.Count == 0)
        {
            return files;
        }

        var regexes = BuildFilterRegexes(filters);
        var result = new List<WorkshopStateFile>();
        foreach (var file in files)
        {
            var normalized = NormalizeManifestPath(file.Path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (MatchesAnyFilter(normalized, regexes))
            {
                result.Add(file);
            }
        }

        return result;
    }

    private static WorkshopState? TryLoadState(string outputDir)
    {
        var path = GetStatePath(outputDir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<WorkshopState>(json);
            if (state == null || state.ManifestId == 0 || state.Files == null)
            {
                return null;
            }

            return state;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveState(ManifestDownloadResult manifestResult, string outputDir)
    {
        var state = new WorkshopState
        {
            ManifestId = manifestResult.ManifestId
        };

        if (manifestResult.Manifest.Files != null)
        {
            foreach (var file in manifestResult.Manifest.Files)
            {
                if ((file.Flags & EDepotFileFlag.Directory) != 0)
                {
                    continue;
                }

                var path = NormalizeManifestPath(file.FileName);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var hash = file.FileHash == null || file.FileHash.Length == 0
                    ? null
                    : Convert.ToHexString(file.FileHash);

                state.Files.Add(new WorkshopStateFile
                {
                    Path = path,
                    Size = file.TotalSize,
                    Sha1 = hash
                });
            }
        }

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var pathOut = GetStatePath(outputDir);
        File.WriteAllText(pathOut, json);
    }

    private static string GetStatePath(string outputDir)
    {
        return Path.Combine(outputDir, ".state.json");
    }

    private static void CopyUnchangedFiles(List<CopyPlanItem> filesToCopy, string tempDir)
    {
        foreach (var item in filesToCopy)
        {
            var destPath = GetSafePath(tempDir, item.RelativePath);
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(item.SourcePath, destPath, overwrite: true);
        }
    }

    private static string PrepareTempDirectory(string outputDir)
    {
        // We always start from a clean temp folder to avoid mixing old and new content.
        var trimmed = outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tempDir = $"{trimmed}.tmp";
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static void SwapDirectories(string targetDir, string tempDir)
    {
        // Swap is done via rename to keep the target directory in a consistent state.
        var targetFull = Path.GetFullPath(targetDir);
        var tempFull = Path.GetFullPath(tempDir);
        var backupDir = $"{targetFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}.old";

        if (Directory.Exists(backupDir))
        {
            Directory.Delete(backupDir, recursive: true);
        }

        var hadTarget = Directory.Exists(targetFull);
        if (hadTarget)
        {
            Directory.Move(targetFull, backupDir);
        }

        try
        {
            Directory.Move(tempFull, targetFull);
        }
        catch
        {
            if (Directory.Exists(targetFull))
            {
                Directory.Delete(targetFull, recursive: true);
            }

            if (hadTarget && Directory.Exists(backupDir))
            {
                Directory.Move(backupDir, targetFull);
            }
            throw;
        }

        if (Directory.Exists(backupDir))
        {
            Directory.Delete(backupDir, recursive: true);
        }
    }

    private static string NormalizeManifestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }
        normalized = normalized.TrimStart('/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }
        return normalized;
    }

    private static string GetSafePath(string rootDir, string relativePath)
    {
        // Prevent path traversal from malicious manifest entries.
        var rootFull = Path.GetFullPath(rootDir);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
        {
            rootFull += Path.DirectorySeparatorChar;
        }

        var safeRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, safeRelative));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Blocked path outside output dir: {relativePath}");
        }

        return full;
    }

    private static byte[] ComputeSha1(string path)
    {
        using var sha1 = SHA1.Create();
        using var stream = File.OpenRead(path);
        return sha1.ComputeHash(stream);
    }

    private static bool HashEquals(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static List<Regex> BuildFilterRegexes(List<string> filters)
    {
        var list = new List<Regex>();
        foreach (var filter in filters)
        {
            var normalized = NormalizeFilterPattern(filter);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            list.Add(GlobToRegex(normalized));
        }

        return list;
    }

    private static string NormalizeFilterPattern(string pattern)
    {
        var normalized = NormalizeManifestPath(pattern);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (!normalized.Contains("/", StringComparison.Ordinal) && !normalized.StartsWith("**/", StringComparison.Ordinal))
        {
            normalized = $"**/{normalized}";
        }

        return normalized;
    }

    private static Regex GlobToRegex(string pattern)
    {
        // '*' matches within a path segment, '**' spans path separators.
        var sb = new StringBuilder();
        sb.Append('^');
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '*')
            {
                var isDouble = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (isDouble)
                {
                    var hasSlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                    if (hasSlash)
                    {
                        // "**/" should match zero or more path segments.
                        sb.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i++;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
                continue;
            }

            if (ch == '?')
            {
                sb.Append("[^/]");
                continue;
            }

            if (ch == '/')
            {
                sb.Append('/');
                continue;
            }

            sb.Append(Regex.Escape(ch.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool MatchesAnyFilter(string path, List<Regex> filters)
    {
        foreach (var regex in filters)
        {
            if (regex.IsMatch(path))
            {
                return true;
            }
        }

        return false;
    }

    private static Server? PickServer(IReadOnlyCollection<Server> servers)
    {
        foreach (var server in servers)
        {
            if (!string.IsNullOrWhiteSpace(server.Host))
            {
                return server;
            }
        }

        return null;
    }

    private static uint? FindWorkshopDepotId(KeyValue root)
    {
        var node = FindKeyValue(root, "workshopdepot") ?? FindKeyValue(root, "workshop_depot");
        if (node != null && uint.TryParse(node.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var depotId))
        {
            return depotId;
        }

        return null;
    }

    private static KeyValue? FindKeyValue(KeyValue root, string name)
    {
        if (string.Equals(root.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var match = FindKeyValue(child, name);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static long ClampToLong(ulong value)
    {
        return value > long.MaxValue ? long.MaxValue : (long)value;
    }

    private static async Task WaitForCooldownAsync(DateTimeOffset cooldownUntil)
    {
        if (cooldownUntil == DateTimeOffset.MinValue)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (cooldownUntil <= now)
        {
            return;
        }

        var delay = cooldownUntil - now;
        Console.WriteLine($"Cooling down for {delay.TotalSeconds:0} sec...");
        await Task.Delay(delay);
    }

    private static TimeSpan? GetTimeoutCooldown(int consecutiveTimeouts)
    {
        if (consecutiveTimeouts < 3)
        {
            return null;
        }

        var seconds = ManifestTimeoutCooldownBase.TotalSeconds * (consecutiveTimeouts - 2);
        var delay = TimeSpan.FromSeconds(Math.Min(seconds, ManifestTimeoutCooldownMax.TotalSeconds));
        return delay;
    }

    private static void LogWebRequestException(SteamKitWebRequestException ex, string context)
    {
        var status = ex.StatusCode;
        var statusText = (int)status == 0 ? "unknown" : $"{(int)status} {status}";
        var retryAfter = TryGetRetryAfter(ex);
        if (retryAfter.HasValue)
        {
            Console.WriteLine($"{context}: HTTP {statusText}. Retry-After: {retryAfter.Value.TotalSeconds:0} sec.");
        }
        else
        {
            Console.WriteLine($"{context}: HTTP {statusText}.");
        }
    }

    private static TimeSpan? TryGetRateLimitDelay(SteamKitWebRequestException ex)
    {
        var retryAfter = TryGetRetryAfter(ex);
        if (retryAfter.HasValue)
        {
            return retryAfter;
        }

        var status = ex.StatusCode;
        if (IsRateLimitStatus(status))
        {
            return TimeSpan.FromSeconds(60);
        }

        return null;
    }

    private static bool IsRateLimitStatus(HttpStatusCode status)
    {
        return status == (HttpStatusCode)429
            || status == HttpStatusCode.ServiceUnavailable
            || status == HttpStatusCode.BadGateway
            || status == HttpStatusCode.GatewayTimeout;
    }

    private static TimeSpan? TryGetRetryAfter(SteamKitWebRequestException ex)
    {
        object? headers = ex.Headers;
        if (headers == null)
        {
            return null;
        }

        if (headers is HttpResponseHeaders httpHeaders)
        {
            if (httpHeaders.TryGetValues("Retry-After", out var values))
            {
                foreach (var value in values)
                {
                    if (TryParseRetryAfterValue(value, out var delay))
                    {
                        return delay;
                    }
                }
            }

            return null;
        }

        if (headers is IEnumerable<KeyValuePair<string, IEnumerable<string>>> pairs)
        {
            foreach (var pair in pairs)
            {
                if (!string.Equals(pair.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var value in pair.Value)
                {
                    if (TryParseRetryAfterValue(value, out var delay))
                    {
                        return delay;
                    }
                }
            }
        }

        return null;
    }

    private static bool TryParseRetryAfterValue(string value, out TimeSpan delay)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            delay = TimeSpan.FromSeconds(seconds);
            return delay > TimeSpan.Zero;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var diff = date - DateTimeOffset.UtcNow;
            if (diff > TimeSpan.Zero)
            {
                delay = diff;
                return true;
            }
        }

        delay = TimeSpan.Zero;
        return false;
    }

    private static string DescribeAsyncJobFailure(AsyncJobFailedException ex)
    {
        // SteamKit2 throws this when a job callback returns EResult != OK.
        // Some versions expose `Result` property; use reflection to keep compatibility.
        try
        {
            var prop = ex.GetType().GetProperty("Result");
            var value = prop?.GetValue(ex);
            if (value != null)
            {
                return $"Result={value}";
            }
        }
        catch
        {
            // ignored
        }

        return ex.Message;
    }
}

/// <summary>
/// Summary of a batch workshop download run.
/// </summary>
internal sealed class BatchDownloadResult
{
    public BatchDownloadResult(int totalCount, List<ulong> failedIds)
    {
        TotalCount = totalCount;
        FailedIds = failedIds;
    }

    public int TotalCount { get; }
    public IReadOnlyList<ulong> FailedIds { get; }
}

/// <summary>
/// Holds a downloaded and decrypted depot manifest plus the metadata required to fetch its chunks.
/// </summary>
internal sealed class ManifestDownloadResult
{
    public ManifestDownloadResult(
        ulong publishedFileId,
        string outputDir,
        ulong manifestId,
        uint depotId,
        byte[] depotKey,
        Server server,
        string? cdnAuthToken,
        DepotManifest manifest)
    {
        PublishedFileId = publishedFileId;
        OutputDir = outputDir;
        ManifestId = manifestId;
        DepotId = depotId;
        DepotKey = depotKey;
        Server = server;
        CdnAuthToken = cdnAuthToken;
        Manifest = manifest;
    }

    public ulong PublishedFileId { get; }
    public string OutputDir { get; }
    public ulong ManifestId { get; }
    public uint DepotId { get; }
    public byte[] DepotKey { get; }
    public Server Server { get; }
    public string? CdnAuthToken { get; }
    public DepotManifest Manifest { get; }
}

internal sealed class ManifestStageResult
{
    public ManifestStageResult(int processed, List<ulong> failedIds)
    {
        Processed = processed;
        FailedIds = failedIds;
    }

    public int Processed { get; }
    public List<ulong> FailedIds { get; }
}

internal sealed class DownloadStageResult
{
    public DownloadStageResult(List<ulong> failedIds)
    {
        FailedIds = failedIds;
    }

    public List<ulong> FailedIds { get; }
}

/// <summary>
/// Cached manifest metadata saved to disk to skip redundant manifest downloads.
/// </summary>
internal sealed class WorkshopState
{
    public ulong ManifestId { get; set; }
    public List<WorkshopStateFile> Files { get; set; } = new();
}

/// <summary>
/// Cached file entry for a workshop item.
/// </summary>
internal sealed class WorkshopStateFile
{
    public string Path { get; set; } = string.Empty;
    public ulong Size { get; set; }
    public string? Sha1 { get; set; }
}

internal sealed class ManifestCooldownState
{
    public int ConsecutiveTimeouts { get; set; }
    public DateTimeOffset CooldownUntil { get; set; } = DateTimeOffset.MinValue;
}
