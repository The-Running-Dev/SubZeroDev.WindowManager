namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>A data root in the temp folder and the built-in content copied beside the test assembly.</summary>
internal sealed class Scratch : IDisposable
{
    public Scratch()
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "SubZeroDev.WindowManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataRoot);
        Locations = new Locations(DataRoot, Path.Combine(AppContext.BaseDirectory, "content"));
    }

    public string DataRoot { get; }

    public Locations Locations { get; }

    /// <summary>Copies the built-in theme into the data root and returns the copy's folder.</summary>
    public string CopyBuiltInTheme(string themeId)
    {
        var source = Path.Combine(Locations.ContentRoot, "themes", themeId);
        var target = Path.Combine(DataRoot, "themes", themeId);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return target;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(DataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
