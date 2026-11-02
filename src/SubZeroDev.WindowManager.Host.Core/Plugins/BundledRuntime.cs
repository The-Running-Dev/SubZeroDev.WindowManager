namespace SubZeroDev.WindowManager.Host.Core.Plugins;

/// <summary>
/// The release carries its own .NET runtime in <c>runtime\</c>, beside <c>host\</c>. The guardian and the host find it
/// through their app hosts; plugin workers are ordinary framework-dependent programs, so they find it only through
/// <c>DOTNET_ROOT</c>. The host sets that for itself (and so for its children) only when nothing else would serve:
/// the bundled runtime is there, neither variable is set, and no machine-wide .NET is installed.
/// </summary>
public static class BundledRuntime
{
    public const string RootVariable = "DOTNET_ROOT";
    public const string RootVariableX64 = "DOTNET_ROOT_X64";

    /// <summary>The runtime folder workers should use, or <see langword="null"/> to leave the environment alone.</summary>
    public static string? Resolve(string hostDirectory, Func<string, string?> variable, string? programFiles, Func<string, bool> directoryExists)
    {
        if (!string.IsNullOrEmpty(variable(RootVariable)) || !string.IsNullOrEmpty(variable(RootVariableX64)))
        {
            return null;
        }

        var bundled = Path.GetFullPath(Path.Combine(hostDirectory, "..", "runtime"));
        if (!directoryExists(Path.Combine(bundled, "host", "fxr")))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(programFiles) && directoryExists(Path.Combine(programFiles, "dotnet", "host", "fxr")))
        {
            return null;
        }

        return bundled;
    }

    /// <summary>Points this process, and so every worker it starts, at the bundled runtime when <see cref="Resolve"/> says so.</summary>
    public static string? Apply()
    {
        var root = Resolve(AppContext.BaseDirectory, Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Directory.Exists);
        if (root is not null)
        {
            Environment.SetEnvironmentVariable(RootVariable, root);
            Environment.SetEnvironmentVariable(RootVariableX64, root);
        }

        return root;
    }
}
