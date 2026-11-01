using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SubZeroDev.WindowManager.Host.Surfaces.Controls;

/// <summary>
/// The templated root of a panel window. Themes restyle it through the <c>Wm.Panel</c> key; the
/// <c>Start</c>, <c>Center</c> and <c>End</c> slots hold the panel's zones. <see cref="Edge"/> and
/// <see cref="Behaviour"/> are strings (<c>bottom</c>, <c>reserve</c>) so a loose XAML theme can
/// trigger on them without referencing host enums.
/// </summary>
public sealed partial class WmPanel : Control
{
    public static readonly DependencyProperty EdgeProperty =
        DependencyProperty.Register(nameof(Edge), typeof(string), typeof(WmPanel), new PropertyMetadata("bottom"));

    public static readonly DependencyProperty BehaviourProperty =
        DependencyProperty.Register(nameof(Behaviour), typeof(string), typeof(WmPanel), new PropertyMetadata("reserve"));

    public static readonly DependencyProperty StartProperty =
        DependencyProperty.Register(nameof(Start), typeof(object), typeof(WmPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty CenterProperty =
        DependencyProperty.Register(nameof(Center), typeof(object), typeof(WmPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty EndProperty =
        DependencyProperty.Register(nameof(End), typeof(object), typeof(WmPanel), new PropertyMetadata(null));

    public WmPanel() => DefaultStyleKey = typeof(WmPanel);

    public string Edge
    {
        get => (string)GetValue(EdgeProperty);
        set => SetValue(EdgeProperty, value);
    }

    public string Behaviour
    {
        get => (string)GetValue(BehaviourProperty);
        set => SetValue(BehaviourProperty, value);
    }

    public object? Start
    {
        get => GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    public object? Center
    {
        get => GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    public object? End
    {
        get => GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }
}

/// <summary>One zone of a panel (<c>start</c>, <c>center</c> or <c>end</c>); its items are contributions.</summary>
public sealed partial class WmZone : ItemsControl
{
    public WmZone() => DefaultStyleKey = typeof(WmZone);
}

/// <summary>The host frame around one placed contribution instance.</summary>
public sealed partial class WmContribution : ContentControl
{
    public WmContribution() => DefaultStyleKey = typeof(WmContribution);
}

/// <summary>
/// Stands in for a contribution whose plugin cannot show a view (I10). <see cref="Reason"/> names a
/// visual state in the <c>ReasonStates</c> group: <c>Missing</c>, <c>Disabled</c>, <c>Faulted</c>,
/// <c>Incompatible</c> or <c>Starting</c>.
/// </summary>
public sealed partial class WmPlaceholder : Control
{
    public const string ReasonStatesGroup = "ReasonStates";

    public static readonly DependencyProperty PluginNameProperty =
        DependencyProperty.Register(nameof(PluginName), typeof(string), typeof(WmPlaceholder), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ReasonProperty =
        DependencyProperty.Register(nameof(Reason), typeof(string), typeof(WmPlaceholder),
            new PropertyMetadata("Missing", (d, _) => ((WmPlaceholder)d).UpdateState()));

    public static readonly DependencyProperty DetailProperty =
        DependencyProperty.Register(nameof(Detail), typeof(string), typeof(WmPlaceholder), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty RetryCommandProperty =
        DependencyProperty.Register(nameof(RetryCommand), typeof(ICommand), typeof(WmPlaceholder), new PropertyMetadata(null));

    public WmPlaceholder() => DefaultStyleKey = typeof(WmPlaceholder);

    public string PluginName
    {
        get => (string)GetValue(PluginNameProperty);
        set => SetValue(PluginNameProperty, value);
    }

    public string Reason
    {
        get => (string)GetValue(ReasonProperty);
        set => SetValue(ReasonProperty, value);
    }

    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public ICommand? RetryCommand
    {
        get => (ICommand?)GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateState();
    }

    private void UpdateState() => VisualStateManager.GoToState(this, Reason, useTransitions: false);
}
