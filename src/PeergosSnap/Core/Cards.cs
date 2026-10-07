namespace PeergosSnap.Core;

/// <summary>What a notification card offers, by what is true at that moment (unit tested). A card never offers to open
/// or show a file that is not on this PC: then it offers what fits a file in Peergos.</summary>
public static class CardLogic
{
    public const string View = "View", Open = "Open", ShowFile = "Show file", Download = "Download", GetLink = "Get a link", GetLinks = "Get links";

    /// <summary>The buttons of the card for what a friend sent: kept on this PC – look at it, open it, show it in its
    /// folder; only in Peergos – look at it (the direct window), download a copy, or get a link.</summary>
    public static string[] Arrival(bool kept, int count) => (kept, count) switch
    {
        (true, 1) => [View, Open, ShowFile],
        (true, _) => [View, ShowFile],
        (false, 1) => [View, Download, GetLink],
        _ => [View, Download, GetLinks],
    };

    /// <summary>A capture's card shows "Show file" only when the copy on this PC stays (Settings → Files & history).</summary>
    public static bool OffersShowFile(bool fileStays, bool busy) => fileStays && !busy;
}
