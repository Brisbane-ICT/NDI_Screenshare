namespace NdiScreenshare.Capture;

public sealed class DisplayInfo
{
    public string DeviceName { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public bool IsPrimary;
    public int Width;
    public int Height;
    public int Left;
    public int Top;
}
