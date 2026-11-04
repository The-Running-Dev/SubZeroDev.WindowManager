using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// The <c>host.windows</c> and <c>host.desktops</c> feeds. Both are built from one snapshot in one call, so they
/// carry the same revision (I42); the windows feed publishes facts only, with no view's eligibility in it (I40).
/// </summary>
public static class WindowFeeds
{
    public const string WindowsFeedId = "host.windows";
    public const string DesktopsFeedId = "host.desktops";

    public static JsonObject Windows(WindowSnapshot snapshot, long epoch) => new()
    {
        ["revision"] = snapshot.Revision,
        ["windows"] = new JsonArray([.. snapshot.Windows.Select(w => (JsonNode?)Item(w, epoch))]),
    };

    /// <param name="snapshot">The snapshot the windows feed was built from.</param>
    /// <param name="current">The desktop the user is on.</param>
    /// <param name="busy">Whether a desktop switch is planned or running.</param>
    public static JsonObject Desktops(WindowSnapshot snapshot, Guid current, bool busy)
    {
        var ids = new List<Guid> { current };
        ids.AddRange(snapshot.Windows.Select(w => w.DesktopId).Where(id => id != current).Distinct());
        return new JsonObject
        {
            ["revision"] = snapshot.Revision,
            ["current"] = current.ToString("D"),
            ["busy"] = busy,
            ["desktops"] = new JsonArray([.. ids.Select(id => (JsonNode?)new JsonObject
            {
                ["id"] = id.ToString("D"),
                ["windowCount"] = snapshot.Windows.Count(w => w.DesktopId == id && !w.PinnedToAll),
                ["hasAttention"] = snapshot.Windows.Any(w => w.DesktopId == id && w.Attention),
            })]),
        };
    }

    public static string Reference(WindowKey key, long epoch) => new WindowReference(epoch, key.Generation).ToString();

    private static JsonObject Item(ManagedWindow w, long epoch)
    {
        var item = new JsonObject
        {
            ["windowKey"] = Reference(w.Key, epoch),
            ["hwnd"] = (long)w.Key.Handle,
            ["processId"] = w.Fingerprint.ProcessId,
            ["title"] = w.Title,
        };
        if (w.ExePath is not null)
        {
            item["exe"] = w.ExePath;
        }

        if (w.Aumid is not null)
        {
            item["aumid"] = w.Aumid;
        }

        item["groupKey"] = w.GroupKey;
        if (w.Owner is { } owner)
        {
            item["ownerKey"] = Reference(owner, epoch);
        }

        item["monitorId"] = w.MonitorId;
        item["desktopId"] = w.DesktopId.ToString("D");
        item["pinnedToAll"] = w.PinnedToAll;
        item["visible"] = w.Visible;
        item["cloak"] = w.Cloak switch
        {
            CloakState.App => "app",
            CloakState.Shell => "shell",
            CloakState.Inherited => "inherited",
            _ => "none",
        };
        item["shellHidden"] = w.ShellHidden;
        item["toolWindow"] = w.ToolWindow;
        item["noActivate"] = w.NoActivate;
        item["appWindow"] = w.AppWindow;
        item["titled"] = w.Titled;
        item["firstSeen"] = w.FirstSeen;
        item["activatedAt"] = w.ActivatedAt;
        item["active"] = w.Active;
        item["minimized"] = w.Minimised;
        item["attention"] = w.Attention;
        item["progress"] = ProgressJson(w.Progress);
        if (w.Overlay is { } overlay && IconJson(overlay.Icon) is { } icon)
        {
            var o = new JsonObject { ["icon"] = icon };
            if (overlay.Description is not null)
            {
                o["description"] = overlay.Description;
            }

            item["overlay"] = o;
        }

        return item;
    }

    private static JsonObject ProgressJson(WindowProgress progress)
    {
        var json = new JsonObject
        {
            ["state"] = progress.State switch
            {
                ProgressState.Indeterminate => "indeterminate",
                ProgressState.Normal => "normal",
                ProgressState.Error => "error",
                ProgressState.Paused => "paused",
                _ => "none",
            },
        };
        if (progress.Value is { } value)
        {
            json["value"] = value;
        }

        return json;
    }

    private static JsonNode? IconJson(IconRef icon) => icon switch
    {
        IconRef.Named named => new JsonObject { ["named"] = named.Id },
        IconRef.Window window => new JsonObject { ["window"] = window.Hwnd },
        IconRef.File file => new JsonObject { ["file"] = file.PackagePath },
        IconRef.Tray tray => new JsonObject { ["tray"] = tray.Key, ["v"] = tray.Version },
        IconRef.App app => new JsonObject
        {
            ["app"] = new JsonObject { ["exe"] = app.Exe, ["aumid"] = app.Aumid },
        },
        _ => null,
    };
}
