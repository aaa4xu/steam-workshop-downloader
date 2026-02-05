using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Thin wrapper over Steam Web API endpoints needed by the CLI.
/// </summary>
internal static class SteamWebApi
{
    private const string PublishedFileDetailsUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    public const int MaxPublishedFileDetailsBatchSize = 100;

    public static async Task<PublishedFileDetails> FetchPublishedFileDetailsAsync(ulong publishedFileId, CancellationToken cancellationToken)
    {
        var batch = await FetchPublishedFileDetailsBatchAsync(new[] { publishedFileId }, cancellationToken);
        if (batch.Count == 0)
        {
            return new PublishedFileDetails { PublishedFileId = publishedFileId, Result = 0 };
        }

        return batch[0];
    }

    public static async Task<IReadOnlyList<PublishedFileDetails>> FetchPublishedFileDetailsBatchAsync(IReadOnlyList<ulong> publishedFileIds, CancellationToken cancellationToken)
    {
        if (publishedFileIds.Count == 0)
        {
            return Array.Empty<PublishedFileDetails>();
        }

        using var http = new HttpClient();
        var form = new List<KeyValuePair<string, string>>(publishedFileIds.Count + 1)
        {
            new KeyValuePair<string, string>("itemcount", publishedFileIds.Count.ToString(CultureInfo.InvariantCulture))
        };
        for (var i = 0; i < publishedFileIds.Count; i++)
        {
            form.Add(new KeyValuePair<string, string>($"publishedfileids[{i}]", publishedFileIds[i].ToString(CultureInfo.InvariantCulture)));
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(PublishedFileDetailsUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var detailsArray = doc.RootElement.GetProperty("response").GetProperty("publishedfiledetails");

        var results = new List<PublishedFileDetails>(detailsArray.GetArrayLength());
        var index = 0;
        foreach (var details in detailsArray.EnumerateArray())
        {
            var publishedFileId = ReadUInt64(details, "publishedfileid");
            if (publishedFileId == 0 && index < publishedFileIds.Count)
            {
                publishedFileId = publishedFileIds[index];
            }

            results.Add(new PublishedFileDetails
            {
                PublishedFileId = publishedFileId,
                Result = details.GetProperty("result").GetInt32(),
                Title = details.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? string.Empty : string.Empty,
                HContentFile = ReadUInt64(details, "hcontent_file"),
                ConsumerAppId = (uint)ReadUInt64(details, "consumer_app_id"),
            });
            index++;
        }

        return results;
    }

    private static ulong ReadUInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return 0;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.GetUInt64(),
            JsonValueKind.String => ulong.TryParse(prop.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var val) ? val : 0,
            _ => 0
        };
    }
}
