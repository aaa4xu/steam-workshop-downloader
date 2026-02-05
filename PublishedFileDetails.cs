/// <summary>
/// Minimal metadata returned by the Steam Web API for a workshop item.
/// Used to validate the item and log basic details.
/// </summary>
internal sealed class PublishedFileDetails
{
    public ulong PublishedFileId { get; set; }
    public int Result { get; set; }
    public string Title { get; set; } = string.Empty;
    public ulong HContentFile { get; set; }
    public uint ConsumerAppId { get; set; }
}
