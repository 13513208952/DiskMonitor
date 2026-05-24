namespace DiskMonitor.Core.Etw;

// 高频读写的 FileObject → 路径前缀映射，使用分段锁降低竞争
internal sealed class FileObjectCache
{
    private const int Segments = 64;
    private readonly Dictionary<ulong, string>[] _segments;
    private readonly Lock[] _locks;

    public FileObjectCache()
    {
        _segments = new Dictionary<ulong, string>[Segments];
        _locks    = new Lock[Segments];
        for (int i = 0; i < Segments; i++)
        {
            _segments[i] = new Dictionary<ulong, string>();
            _locks[i]    = new Lock();
        }
    }

    private int Index(ulong key) => (int)(key % (ulong)Segments);

    public void Set(ulong fileObject, string prefix)
    {
        int i = Index(fileObject);
        lock (_locks[i]) _segments[i][fileObject] = prefix;
    }

    public string? Get(ulong fileObject)
    {
        int i = Index(fileObject);
        lock (_locks[i])
            return _segments[i].TryGetValue(fileObject, out var v) ? v : null;
    }

    public void Remove(ulong fileObject)
    {
        int i = Index(fileObject);
        lock (_locks[i]) _segments[i].Remove(fileObject);
    }
}
