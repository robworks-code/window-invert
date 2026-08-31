namespace WindowInvert.Core.InvertState;

public sealed class InvertedWindowSet
{
    private readonly HashSet<nint> _inverted = new();

    public IReadOnlyCollection<nint> InvertedHandles => _inverted;

    public bool IsInverted(nint hwnd) => _inverted.Contains(hwnd);

    public bool Toggle(nint hwnd)
    {
        if (_inverted.Remove(hwnd))
        {
            return false;
        }

        _inverted.Add(hwnd);
        return true;
    }

    public void Remove(nint hwnd) => _inverted.Remove(hwnd);
}
