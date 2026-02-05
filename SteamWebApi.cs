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

    public static async Task<PublishedFileDetails> FetchPublishedFileDetailsAsync(ulong publishedFileId, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("itemcount", "1"),
            new KeyValuePair<string, string>("publishedfileids[0]", publishedFileId.ToString(CultureInfo.InvariantCulture))
        });

        using var response = await http.PostAsync(PublishedFileDetailsUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var details = doc.RootElement.GetProperty("response").GetProperty("publishedfiledetails")[0];
        return new PublishedFileDetails
        {
            PublishedFileId = publishedFileId,
            Result = details.GetProperty("result").GetInt32(),
            Title = details.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? string.Empty : string.Empty,
            HContentFile = ReadUInt64(details, "hcontent_file"),
            ConsumerAppId = (uint)ReadUInt64(details, "consumer_app_id"),
        };
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
