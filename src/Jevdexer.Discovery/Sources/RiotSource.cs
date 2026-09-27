namespace Jevdexer.Discovery.Sources;

/// <summary>
/// Riot games (League of Legends, VALORANT, ...): one `*.product_settings.yaml` per product under
/// `%ProgramData%\Riot Games\Metadata\&lt;product&gt;.&lt;patchline&gt;\`, holding
/// `product_install_full_path`. Read line by line; no YAML parser needed for one key.
/// Riot games usually also register Uninstall keys; this source catches them when they don't.
/// </summary>
public sealed class RiotSource : IAppSource
{
    private const string InstallKey = "product_install_full_path:";

    public string Name => "riot";

    public IEnumerable<RawApp> Read()
    {
        var metadata = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "Metadata");
        if (!Directory.Exists(metadata)) yield break;

        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true };
        foreach (var yaml in Directory.EnumerateFiles(metadata, "*.product_settings.yaml", options))
        {
            var dir = ReadInstallPath(yaml);
            if (dir is null) continue;

            // Folder is "<product>.<patchline>", e.g. "league_of_legends.live".
            var product = Path.GetFileName(Path.GetDirectoryName(yaml)!).Split('.')[0];
            yield return new RawApp
            {
                Source = Name,
                Name = Path.GetFileName(dir.TrimEnd('\\', '/')),
                Publisher = "Riot Games",
                InstallDir = dir,
                SourceId = product,
            };
        }
    }

    private static string? ReadInstallPath(string yaml)
    {
        try
        {
            foreach (var line in File.ReadLines(yaml))
            {
                var t = line.Trim();
                if (t.StartsWith(InstallKey, StringComparison.Ordinal))
                    return t[InstallKey.Length..].Trim().Trim('"', '\'').Replace('/', '\\');
            }
        }
        catch (IOException) { }
        return null;
    }
}
