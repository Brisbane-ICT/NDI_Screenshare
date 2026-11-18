namespace NdiScreenshare.Capture;

public sealed class CapturedFrame
{
    public int Width;
    public int Height;
    public int Stride;
    public byte[] Bgra = Array.Empty<byte>();
}
