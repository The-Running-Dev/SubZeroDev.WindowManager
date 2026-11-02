using SubZeroDev.WindowManager.Host.Core.Plugins;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S15: workers in a release run on the bundled runtime only when nothing else would serve.</summary>
public sealed class BundledRuntimeTests
{
    private const string Host = @"C:\App\host";
    private const string Bundled = @"C:\App\runtime";
    private const string ProgramFiles = @"C:\Program Files";

    [Fact]
    public void S15_3_workers_use_the_bundled_runtime_when_no_dotnet_is_installed()
    {
        Assert.Equal(Bundled, Resolve(variables: [], directories: [Bundled + @"\host\fxr"]));
    }

    [Fact]
    public void S15_3_a_machine_wide_dotnet_is_left_to_serve()
    {
        Assert.Null(Resolve(variables: [], directories: [Bundled + @"\host\fxr", ProgramFiles + @"\dotnet\host\fxr"]));
    }

    [Theory]
    [InlineData(BundledRuntime.RootVariable)]
    [InlineData(BundledRuntime.RootVariableX64)]
    public void S15_3_a_DOTNET_ROOT_the_user_set_is_kept(string variable)
    {
        Assert.Null(Resolve(variables: [variable], directories: [Bundled + @"\host\fxr"]));
    }

    [Fact]
    public void S15_3_a_build_without_a_bundled_runtime_changes_nothing()
    {
        Assert.Null(Resolve(variables: [], directories: []));
    }

    private static string? Resolve(string[] variables, string[] directories) =>
        BundledRuntime.Resolve(Host,
            name => variables.Contains(name) ? @"D:\dotnet" : null,
            ProgramFiles,
            path => directories.Contains(path, StringComparer.OrdinalIgnoreCase));
}
