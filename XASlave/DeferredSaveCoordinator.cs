namespace XASlave;

/// <summary>
/// Owns coalesced configuration-save generations without coupling the policy to Dalamud.
/// </summary>
internal sealed class DeferredSaveCoordinator
{
    private readonly object sync = new();
    private int generation;
    private bool pending;

    public int Queue()
    {
        lock (sync)
        {
            pending = true;
            return ++generation;
        }
    }

    public void Invalidate()
    {
        lock (sync)
        {
            pending = false;
            generation++;
        }
    }

    public bool TryClaim(int expectedGeneration)
    {
        lock (sync)
        {
            if (!pending || generation != expectedGeneration)
                return false;

            pending = false;
            return true;
        }
    }

    public bool TryFlush()
    {
        lock (sync)
        {
            if (!pending)
                return false;

            pending = false;
            generation++;
            return true;
        }
    }
}
