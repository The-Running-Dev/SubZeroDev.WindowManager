using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Sdk;

namespace Example.Hello;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<HelloPlugin>(args);
}

public sealed class HelloPlugin : Plugin
{
    protected override ContributionInstance CreateInstance(InstanceRequest request) => new HelloInstance(request);
}

/// <summary>Shows "Hello, &lt;name&gt;" and follows the <c>name</c> setting.</summary>
public sealed class HelloInstance(InstanceRequest request) : ContributionInstance(request)
{
    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        Surface.SetView(View.Text("greeting", Prop<string>.State("greeting")));
        Greet();
        return Task.CompletedTask;
    }

    protected override Task OnSettingsChangedAsync(JsonObject settings, CancellationToken ct)
    {
        Greet();
        return Task.CompletedTask;
    }

    private void Greet()
    {
        var name = Settings["name"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : "world";
        Surface.SetState("greeting", $"Hello, {name}");
    }
}
