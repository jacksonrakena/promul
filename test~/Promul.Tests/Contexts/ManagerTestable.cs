namespace Promul.Tests.Contexts;

public class ManagerTestable : PromulManager, IDisposable
{
    private readonly ManagerGroup _parent;
    private readonly int _key;
    public readonly CancellationTokenSource Cts;
    public ManagerTestable(int key, ManagerGroup parent, CancellationTokenSource cts)
    {
        _parent = parent;
        _key = key;
        Cts = cts;
    }

    public void Dispose()
    {
        _parent.ChildDispose(_key);
    }
}