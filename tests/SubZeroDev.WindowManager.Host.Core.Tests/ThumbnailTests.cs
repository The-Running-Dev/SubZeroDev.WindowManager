using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Surfaces;
using SubZeroDev.WindowManager.Host.Theming;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;
using SdkIconRef = SubZeroDev.WindowManager.Sdk.IconRef;
using SdkView = SubZeroDev.WindowManager.Sdk.View;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>S26: the Thumbnail element, the hoverPopup prop and the window-key icon, without a display.</summary>
public sealed class ThumbnailTests
{
    private const string Key = "wk1:abc:7";

    private static readonly string[] SizeKeys =
    [
        "Wm.Thumbnail.Width.Small", "Wm.Thumbnail.Height.Small",
        "Wm.Thumbnail.Width.Medium", "Wm.Thumbnail.Height.Medium",
        "Wm.Thumbnail.Width.Large", "Wm.Thumbnail.Height.Large",
    ];

    [Theory]
    [InlineData("small", 160, 90)]
    [InlineData("medium", 240, 135)]
    [InlineData("large", 320, 180)]
    [InlineData(null, 240, 135)]
    [InlineData("huge", 240, 135)]
    public void Sizes_fall_back_to_the_host_tokens(string? size, double width, double height)
    {
        Assert.Equal((width, height), ThumbnailPolicy.SizeOf(size, _ => null));
    }

    [Fact]
    public void A_theme_token_wins_unless_it_is_not_a_positive_double()
    {
        var tokens = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Wm.Thumbnail.Width.Large"] = 400.0,
            ["Wm.Thumbnail.Height.Large"] = -1.0,
            ["Wm.Thumbnail.Width.Small"] = "wide",
            ["Wm.Thumbnail.Height.Small"] = double.NaN,
        };

        Assert.Equal((400.0, 180.0), ThumbnailPolicy.SizeOf("large", key => tokens.GetValueOrDefault(key)));
        Assert.Equal((160.0, 90.0), ThumbnailPolicy.SizeOf("small", key => tokens.GetValueOrDefault(key)));
    }

    [Fact]
    public void The_host_carries_all_six_size_tokens()
    {
        Assert.Equal(SizeKeys.Order(StringComparer.Ordinal), ThemePackageReader.HostSizeTokens.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_built_in_theme_defines_the_size_tokens_as_doubles()
    {
        var themes = Directory.GetDirectories(Path.Combine(AppContext.BaseDirectory, "content", "themes"));
        Assert.NotEmpty(themes);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var theme in themes)
        {
            var doubles = Directory.GetFiles(theme, "*.xaml")
                .SelectMany(file => XDocument.Load(file).Descendants(x + "Double"))
                .Where(element => SizeKeys.Contains((string?)element.Attribute(x + "Key"), StringComparer.Ordinal))
                .ToDictionary(element => (string)element.Attribute(x + "Key")!, element => double.Parse(element.Value, System.Globalization.CultureInfo.InvariantCulture),
                    StringComparer.Ordinal);
            foreach (var key in SizeKeys)
            {
                Assert.True(doubles.TryGetValue(key, out var dips), $"{Path.GetFileName(theme)} has no {key}.");
                Assert.Equal(ThemePackageReader.HostSizeTokens[key], dips);
            }
        }
    }

    [Fact]
    public void No_key_shows_an_empty_card_without_a_problem()
    {
        var decision = ThumbnailPolicy.Decide(null, _ => throw new InvalidOperationException(), haveService: true);
        Assert.False(decision.Live);
        Assert.Null(decision.Problem);
    }

    [Fact]
    public void Without_a_window_model_the_thumbnail_is_unavailable()
    {
        Assert.Equal(ThumbnailProblem.ThumbnailUnavailable, ThumbnailPolicy.Decide(Key, null, haveService: true).Problem);
    }

    [Theory]
    [InlineData(CoreErrorCode.StaleWindowReference, "StaleWindowReference")]
    [InlineData(CoreErrorCode.WindowGone, "WindowGone")]
    [InlineData(CoreErrorCode.CommandFailed, "ThumbnailUnavailable")]
    public void A_failed_resolve_shows_the_card_with_its_problem(CoreErrorCode code, string problem)
    {
        var decision = ThumbnailPolicy.Decide(Key, _ => Outcome<WindowTarget>.Fail(code, "no"), haveService: true);
        Assert.False(decision.Live);
        Assert.Equal(problem, decision.Problem?.ToString());
        Assert.Equal("no", decision.Detail);
    }

    [Fact]
    public void An_untracked_window_or_a_missing_service_is_unavailable()
    {
        var untracked = ThumbnailPolicy.Decide(Key, _ => Outcome<WindowTarget>.Success(new WindowTarget(42, null)), haveService: true);
        Assert.Equal(ThumbnailProblem.ThumbnailUnavailable, untracked.Problem);
        Assert.Equal((nint)42, untracked.Handle);

        var noService = ThumbnailPolicy.Decide(Key, Resolving(Window()), haveService: false);
        Assert.False(noService.Live);
        Assert.Equal(ThumbnailProblem.ThumbnailUnavailable, noService.Problem);
        Assert.Equal("Notepad", noService.Window!.Title);
    }

    [Fact]
    public void A_drawable_window_is_live()
    {
        var decision = ThumbnailPolicy.Decide(Key, Resolving(Window()), haveService: true);
        Assert.True(decision.Live);
        Assert.Null(decision.Problem);
        Assert.Equal((nint)42, decision.Handle);
    }

    [Theory]
    [InlineData(false, false, false, CloakState.None)]
    [InlineData(true, true, false, CloakState.None)]
    [InlineData(true, false, true, CloakState.None)]
    [InlineData(true, false, false, CloakState.Shell)]
    public void A_window_that_cannot_be_drawn_shows_its_card_without_a_problem(bool visible, bool minimised, bool shellHidden, CloakState cloak)
    {
        var window = Window() with { Visible = visible, Minimised = minimised, ShellHidden = shellHidden, Cloak = cloak };
        var decision = ThumbnailPolicy.Decide(Key, Resolving(window), haveService: true);
        Assert.False(decision.Live);
        Assert.Null(decision.Problem);
        Assert.Same(window, decision.Window);
    }

    [Fact]
    public void Each_problem_is_logged_once()
    {
        var log = new ThumbnailProblemLog();
        Assert.True(log.First(ThumbnailProblem.WindowGone));
        Assert.False(log.First(ThumbnailProblem.WindowGone));
        Assert.True(log.First(ThumbnailProblem.StaleWindowReference));
    }

    [Fact]
    public void Client_pixels_scale_and_round()
    {
        Assert.Equal(new RectInt(15, 30, 375, 234), ThumbnailPolicy.ClientPixels(10, 20, 240, 136, 1.5));
        Assert.Equal(new RectInt(10, 20, 250, 155), ThumbnailPolicy.ClientPixels(10, 20, 240, 135, 1));
    }

    [Fact]
    public void A_thumbnail_is_drawn_only_while_it_can_be_seen()
    {
        var inside = new RectInt(10, 10, 250, 145);
        Assert.True(ThumbnailPolicy.ShouldDraw(true, true, false, inside, 800, 600));
        Assert.False(ThumbnailPolicy.ShouldDraw(false, true, false, inside, 800, 600));
        Assert.False(ThumbnailPolicy.ShouldDraw(true, false, false, inside, 800, 600));
        Assert.False(ThumbnailPolicy.ShouldDraw(true, true, true, inside, 800, 600));
        Assert.False(ThumbnailPolicy.ShouldDraw(true, true, false, new RectInt(10, 10, 10, 145), 800, 600));
        Assert.False(ThumbnailPolicy.ShouldDraw(true, true, false, new RectInt(900, 10, 1140, 145), 800, 600));
        Assert.False(ThumbnailPolicy.ShouldDraw(true, true, false, new RectInt(-300, 10, -60, 145), 800, 600));
        Assert.True(ThumbnailPolicy.ShouldDraw(true, true, false, new RectInt(-100, 10, 140, 145), 800, 600));
    }

    [Fact]
    public void An_icon_by_window_key_is_told_from_one_by_handle()
    {
        Assert.Equal(Key, ThumbnailPolicy.IconWindowKey(new JsonObject { ["window"] = Key }));
        Assert.Null(ThumbnailPolicy.IconWindowKey(new JsonObject { ["window"] = 42 }));
        Assert.Null(ThumbnailPolicy.IconWindowKey(new JsonObject { ["named"] = "x" }));
        Assert.Null(ThumbnailPolicy.IconWindowKey(null));
    }

    [Fact]
    public void The_hover_popup_prop_defaults_its_delay()
    {
        // Props reach the host parsed from the wire, so the cases are parsed too.
        Assert.Equal(("p", TimeSpan.FromMilliseconds(400)), ThumbnailPolicy.HoverPopupOf(JsonNode.Parse("""{"popup": "p"}""")));
        Assert.Equal(("p", TimeSpan.FromMilliseconds(400)), ThumbnailPolicy.HoverPopupOf(JsonNode.Parse("""{"popup": "p", "delayMs": -5}""")));
        Assert.Equal(("p", TimeSpan.FromMilliseconds(400)), ThumbnailPolicy.HoverPopupOf(JsonNode.Parse("""{"popup": "p", "delayMs": "soon"}""")));
        Assert.Equal(("p", TimeSpan.Zero), ThumbnailPolicy.HoverPopupOf(JsonNode.Parse("""{"popup": "p", "delayMs": 0}""")));
        Assert.Equal(("p", TimeSpan.FromMilliseconds(900)), ThumbnailPolicy.HoverPopupOf(JsonNode.Parse("""{"popup": "p", "delayMs": 900}""")));
        Assert.Null(ThumbnailPolicy.HoverPopupOf(JsonNode.Parse("""{"popup": ""}""")));
        Assert.Null(ThumbnailPolicy.HoverPopupOf(JsonNode.Parse("""{"delayMs": 10}""")));
        Assert.Null(ThumbnailPolicy.HoverPopupOf(JsonNode.Parse("\"p\"")));
        Assert.Null(ThumbnailPolicy.HoverPopupOf(null));
    }

    [Fact]
    public void Resting_on_the_element_opens_the_popup_and_leaving_both_closes_it()
    {
        var hover = new HoverPopupState();
        Assert.Equal(HoverAction.StartOpenTimer, hover.ElementEntered());
        Assert.Equal(HoverPhase.Waiting, hover.Phase);
        Assert.Equal(HoverAction.Open, hover.TimerElapsed());
        Assert.Equal(HoverPhase.Open, hover.Phase);

        // Moving from the element into the popup keeps it open.
        Assert.Equal(HoverAction.StartCloseTimer, hover.ElementExited());
        Assert.Equal(HoverAction.StopTimer, hover.PopupEntered());
        Assert.Equal(HoverAction.None, hover.TimerElapsed());
        Assert.Equal(HoverPhase.Open, hover.Phase);

        Assert.Equal(HoverAction.StartCloseTimer, hover.PopupExited());
        Assert.Equal(HoverAction.Close, hover.TimerElapsed());
        Assert.Equal(HoverPhase.Idle, hover.Phase);
    }

    [Fact]
    public void Leaving_before_the_delay_opens_nothing()
    {
        var hover = new HoverPopupState();
        hover.ElementEntered();
        Assert.Equal(HoverAction.StopTimer, hover.ElementExited());
        Assert.Equal(HoverPhase.Idle, hover.Phase);
        Assert.Equal(HoverAction.None, hover.TimerElapsed());
    }

    [Fact]
    public void Coming_back_to_the_element_in_the_grace_keeps_the_popup()
    {
        var hover = new HoverPopupState();
        hover.ElementEntered();
        hover.TimerElapsed();
        Assert.Equal(HoverAction.StartCloseTimer, hover.ElementExited());
        Assert.Equal(HoverAction.StopTimer, hover.ElementEntered());
        Assert.Equal(HoverAction.None, hover.PopupExited());
        Assert.Equal(HoverPhase.Open, hover.Phase);
    }

    [Fact]
    public void A_popup_closed_elsewhere_returns_to_idle()
    {
        var hover = new HoverPopupState();
        hover.ElementEntered();
        hover.TimerElapsed();
        hover.PopupEntered();
        hover.Closed();
        Assert.Equal(HoverPhase.Idle, hover.Phase);
        Assert.Equal(HoverAction.None, hover.TimerElapsed());
        Assert.Equal(HoverAction.StartOpenTimer, hover.ElementEntered());
    }

    [Fact]
    public void The_sdk_builds_a_thumbnail_with_a_hover_popup_that_validates()
    {
        var node = SdkView.Thumbnail("thumb", Prop<string>.Item("windowKey"), ThumbnailSize.Large).WithHoverPopup("peek", 250);
        var json = JsonSerializer.SerializeToNode(node, ProtocolJson.Options)!;
        Assert.Equal("thumbnail", json["kind"]!.GetValue<string>());
        Assert.Equal("windowKey", json["props"]!["window"]!["$item"]!.GetValue<string>());
        Assert.Equal("large", json["props"]!["size"]!.GetValue<string>());
        Assert.Equal("peek", json["props"]!["hoverPopup"]!["popup"]!.GetValue<string>());
        Assert.Equal(250, json["props"]!["hoverPopup"]!["delayMs"]!.GetValue<int>());

        var list = new ViewNode("windows", ElementKind.List, Props: new Dictionary<string, JsonNode?>
        {
            ["automationName"] = "Windows",
            ["items"] = new JsonObject { ["$state"] = "windows" },
        }, ItemTemplate: node);
        var result = ViewValidator.Validate(list);
        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void The_validator_still_refuses_unknown_thumbnail_props()
    {
        var node = SdkView.Thumbnail("thumb", Key) with
        {
            Props = new Dictionary<string, JsonNode?> { ["window"] = Key, ["width"] = 300 },
        };
        Assert.Contains(ViewValidator.Validate(node).Issues, issue => issue.Code == ViewValidator.Codes.ForbiddenProp);
    }

    [Fact]
    public void A_window_icon_by_key_serialises_as_its_key()
    {
        var node = SdkView.Icon("icon", new SdkIconRef.Window(Key));
        var json = JsonSerializer.SerializeToNode(node, ProtocolJson.Options)!;
        Assert.Equal(Key, json["props"]!["icon"]!["window"]!.GetValue<string>());
        Assert.Equal(Key, ThumbnailPolicy.IconWindowKey(json["props"]!["icon"]));

        var byHandle = JsonSerializer.SerializeToNode(SdkView.Icon("icon", new SdkIconRef.Window(42L)), ProtocolJson.Options)!;
        Assert.Equal(42, byHandle["props"]!["icon"]!["window"]!.GetValue<long>());
    }

    [Fact]
    public void The_desktops_feed_reads_the_switch_in_flight_and_names()
    {
        var state = DesktopsFeedState.Parse(JsonNode.Parse("""
            {"revision": 9, "current": "d1", "busy": true, "switching": {"from": "d1", "to": "d2"},
             "desktops": [{"id": "d1", "windowCount": 3, "hasAttention": false, "name": "Work"}, {"id": "d2", "windowCount": 0, "hasAttention": true}]}
            """));
        Assert.NotNull(state);
        Assert.Equal(new DesktopSwitch("d1", "d2"), state.Switching);
        Assert.Equal("Work", state.Desktops[0].Name);
        Assert.Null(state.Desktops[1].Name);
        Assert.True(state.Desktops[1].HasAttention);

        var idle = DesktopsFeedState.Parse(JsonNode.Parse("""{"revision": 1, "current": "d1", "busy": false, "desktops": []}"""));
        Assert.Null(idle!.Switching);
    }

    private static Func<string, Outcome<WindowTarget>> Resolving(ManagedWindow window) =>
        _ => Outcome<WindowTarget>.Success(new WindowTarget(42, window));

    private static ManagedWindow Window() => new(
        new WindowKey(42, 7), new WindowFingerprint(100, 1, 200, "Notepad"), "Notepad", null, null, "notepad", null, "m1",
        Guid.Empty, PinnedToAll: false, Visible: true, CloakState.None, ShellHidden: false, ToolWindow: false, NoActivate: false,
        AppWindow: true, Titled: true, FirstSeen: 1, ActivatedAt: 1, Active: false, Minimised: false, Attention: false,
        WindowProgress.None, null);
}
