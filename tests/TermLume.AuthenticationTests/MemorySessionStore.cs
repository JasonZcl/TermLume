using TermLume.Shared.Abstractions;

internal sealed class MemorySessionStore : IAegisSessionStore
{
    public SavedAegisSession? Session { get; set; }
    public bool ReadFails { get; set; }
    public SavedAegisSession? Read() => ReadFails ? throw new IOException("Synthetic credential read failure") : Session;
    public void Write(SavedAegisSession session) => Session = session;
    public void Delete() => Session = null;
    public void Dispose() { }
}
