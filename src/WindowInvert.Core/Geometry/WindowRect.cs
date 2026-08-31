namespace WindowInvert.Core.Geometry;

public readonly record struct WindowRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}
