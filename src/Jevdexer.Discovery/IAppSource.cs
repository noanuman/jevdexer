namespace Jevdexer.Discovery;

/// <summary>A place that records installed apps. Each source reports what it sees and never merges.</summary>
public interface IAppSource
{
    string Name { get; }

    /// <summary>Returns every app the source records. Must not throw for a missing launcher or key: return empty.</summary>
    IEnumerable<RawApp> Read();
}
