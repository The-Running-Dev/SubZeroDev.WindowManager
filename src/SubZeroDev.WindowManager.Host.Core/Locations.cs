namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>The data root (user documents and packages) and the content root (built-in, read-only).</summary>
public sealed record Locations(string DataRoot, string ContentRoot)
{
    public const string DataRootVariable = "SUBZERODEV_WM_DATA_ROOT";
    public const string ContentRootVariable = "SUBZERODEV_WM_CONTENT_ROOT";

    public string LogsDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>
    /// Resolves the roots for this process. The variables exist so tests can run against a scratch
    /// data root; normal launches use <c>%LOCALAPPDATA%\SubZeroDev\WindowManager</c> and the
    /// <c>content</c> folder beside the host or one level above it (the install layout).
    /// </summary>
    public static Locations Resolve(string baseDirectory)
    {
        var dataRoot = Environment.GetEnvironmentVariable(DataRootVariable);
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SubZeroDev", "WindowManager");
        }

        var contentRoot = Environment.GetEnvironmentVariable(ContentRootVariable);
        if (string.IsNullOrWhiteSpace(contentRoot))
        {
            var beside = Path.Combine(baseDirectory, "content");
            contentRoot = Directory.Exists(beside)
                ? beside
                : Path.GetFullPath(Path.Combine(baseDirectory, "..", "content"));
        }

        return new Locations(Path.GetFullPath(dataRoot), Path.GetFullPath(contentRoot));
    }
}
