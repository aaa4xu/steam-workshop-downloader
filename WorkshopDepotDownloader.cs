using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
            return await DownloadWithSessionAsync(session, publishedFileId, outputDir);
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

    private async Task<bool> DownloadWithSessionAsync(SteamSession session, ulong publishedFileId, string outputDir)
    {
        var manifestId = await GetWorkshopManifestIdAsync(session, publishedFileId);
        if (manifestId == 0)
        {
            Console.WriteLine("Workshop manifest id not found.");
            return false;
        }

        var depotId = await GetWorkshopDepotIdAsync(session);
        if (depotId == 0)
        {
            Console.WriteLine("Workshop depot id not found.");
            return false;
        }

        var depotKey = await GetDepotKeyAsync(session, depotId);
        if (depotKey == null || depotKey.Length == 0)
        {
            Console.WriteLine("Depot key not available.");
            return false;
        }

        var servers = await session.Content.GetServersForSteamPipe();
        var server = PickServer(servers);
        if (server == null || string.IsNullOrWhiteSpace(server.Host))
        {
            Console.WriteLine("No CDN servers available.");
            return false;
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
        var manifest = await cdn.DownloadManifestAsync(depotId, manifestId, manifestRequestCode, server, depotKey, null, cdnAuthToken);

        if (manifest.FilenamesEncrypted)
        {
            manifest.DecryptFilenames(depotKey);
        }

        LogManifestFileList(manifest);

        var selectedFiles = SelectManifestFiles(manifest, _options.Filters);
        if (_options.Filters.Count > 0)
        {
            LogFilteredFileList(selectedFiles, _options.Filters);
        }

        var tempDir = PrepareTempDirectory(outputDir);
        var plan = BuildDownloadPlan(selectedFiles, outputDir);

        Console.WriteLine($"Files selected: {selectedFiles.Count}");
        Console.WriteLine($"Files to copy: {plan.CopyFiles.Count}");
        Console.WriteLine($"Files to download: {plan.DownloadFiles.Count}");

        CopyUnchangedFiles(plan.CopyFiles, tempDir);
        await DownloadManifestFilesAsync(cdn, depotId, depotKey, server, cdnAuthToken, tempDir, plan.DownloadFiles);

        SwapDirectories(outputDir, tempDir);
        return true;
    }

    private async Task<ulong> GetWorkshopManifestIdAsync(SteamSession session, ulong publishedFileId)
    {
        var unifiedMessages = session.Client.GetHandler<SteamUnifiedMessages>()
            ?? throw new InvalidOperationException("SteamUnifiedMessages handler not available.");
        var publishedService = unifiedMessages.CreateService<PublishedFile>();

        var request = new CPublishedFile_GetItemInfo_Request
        {
            appid = _options.AppId,
        };
        request.workshop_items.Add(new CPublishedFile_GetItemInfo_Request.WorkshopItem
        {
            published_file_id = publishedFileId
        });

        var job = publishedService.GetItemInfo(request);
        job.Timeout = TimeSpan.FromSeconds(60);
        try
        {
            var response = await job.ToTask().WaitAsync(TimeSpan.FromSeconds(65));
            var itemCount = response.Body?.workshop_items?.Count ?? 0;
            Console.WriteLine($"PublishedFile.GetItemInfo result: {response.Result}, items: {itemCount}");
            if (response.Result != EResult.OK || response.Body == null || response.Body.workshop_items.Count == 0)
            {
                return 0;
            }

            var item = response.Body.workshop_items[0];
            Console.WriteLine($"Workshop manifest id: {item.manifest_id}");
            return item.manifest_id;
        }
        catch (TaskCanceledException ex)
        {
            Console.WriteLine($"GetItemInfo timed out: {ex.Message}");
            return 0;
        }
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
                    sb.Append(".*");
                    i++;
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
}
