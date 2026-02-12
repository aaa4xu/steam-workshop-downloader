/// <summary>
/// Minimal queue payload for the downloader: a workshop item id and its depot manifest id.
/// For SteamPipe workshop items, the manifest id comes from Web API field <c>hcontent_file</c>.
/// </summary>
internal readonly record struct WorkshopItemRequest(ulong PublishedFileId, ulong ManifestId);

