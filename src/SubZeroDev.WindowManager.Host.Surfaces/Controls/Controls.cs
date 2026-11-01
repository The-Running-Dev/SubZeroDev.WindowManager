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

    public WmPanel()
    {
        DefaultStyleKey = typeof(WmPanel);
        IsTabStop = false;
        TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Cycle;
        XYFocusKeyboardNavigation = Microsoft.UI.Xaml.Input.XYFocusKeyboardNavigationMode.Enabled;
    }

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
    public WmZone()
    {
        DefaultStyleKey = typeof(WmZone);
        IsTabStop = false;
    }
}

/// <summary>The host frame around one placed contribution instance.</summary>
public sealed partial class WmContribution : ContentControl
{
    public WmContribution()
    {
        DefaultStyleKey = typeof(WmContribution);
        IsTabStop = false;
    }
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

    public WmPlaceholder()
    {
        DefaultStyleKey = typeof(WmPlaceholder);
        IsTabStop = false;
    }

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

/// <summary>A <c>Text</c> element. Restyled through <c>Wm.Element.Text</c> and <c>Wm.Element.Text.&lt;class&gt;</c>.</summary>
public sealed partial class WmText : Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(WmText), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty WrapProperty =
        DependencyProperty.Register(nameof(Wrap), typeof(bool), typeof(WmText), new PropertyMetadata(false));

    public WmText()
    {
        DefaultStyleKey = typeof(WmText);
        IsTabStop = false;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool Wrap
    {
        get => (bool)GetValue(WrapProperty);
        set => SetValue(WrapProperty, value);
    }
}

/// <summary>
/// An <c>Icon</c> element. <see cref="Icon"/> carries the resolved image; until icon resolution
/// arrives with asset packs, <see cref="IconName"/> carries the named id so a theme can map it.
/// </summary>
public sealed partial class WmIcon : Control
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(Microsoft.UI.Xaml.Media.ImageSource), typeof(WmIcon), new PropertyMetadata(null));

    public static readonly DependencyProperty IconNameProperty =
        DependencyProperty.Register(nameof(IconName), typeof(string), typeof(WmIcon), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(string), typeof(WmIcon), new PropertyMetadata("medium"));

    public WmIcon()
    {
        DefaultStyleKey = typeof(WmIcon);
        IsTabStop = false;
    }

    public Microsoft.UI.Xaml.Media.ImageSource? Icon
    {
        get => (Microsoft.UI.Xaml.Media.ImageSource?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string IconName
    {
        get => (string)GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    public string Size
    {
        get => (string)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }
}

/// <summary>
/// A <c>Stack</c> element. Its template must hold a <see cref="StackPanel"/> named <c>PART_Items</c>;
/// the children are placed there whenever the template is applied.
/// </summary>
public sealed partial class WmStack : Control
{
    public const string ItemsPart = "PART_Items";

    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(WmStack),
            new PropertyMetadata(Orientation.Horizontal, (d, _) => ((WmStack)d).UpdateOrientation()));

    private StackPanel? _items;

    public WmStack()
    {
        DefaultStyleKey = typeof(WmStack);
        IsTabStop = false;
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public IList<UIElement> Children { get; } = [];

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _items?.Children.Clear();
        _items = GetTemplateChild(ItemsPart) as StackPanel;
        if (_items is null)
        {
            return;
        }

        foreach (var child in Children)
        {
            _items.Children.Add(child);
        }

        UpdateOrientation();
    }

    private void UpdateOrientation()
    {
        if (_items is not null)
        {
            _items.Orientation = Orientation;
        }
    }
}

/// <summary>A <c>Spacer</c> element. <see cref="Flex"/> asks the template to take the free space.</summary>
public sealed partial class WmSpacer : Control
{
    public static readonly DependencyProperty FlexProperty =
        DependencyProperty.Register(nameof(Flex), typeof(bool), typeof(WmSpacer), new PropertyMetadata(false));

    public WmSpacer()
    {
        DefaultStyleKey = typeof(WmSpacer);
        IsTabStop = false;
    }

    public bool Flex
    {
        get => (bool)GetValue(FlexProperty);
        set => SetValue(FlexProperty, value);
    }
}

/// <summary>A <c>Separator</c> element.</summary>
public sealed partial class WmSeparator : Control
{
    public WmSeparator()
    {
        DefaultStyleKey = typeof(WmSeparator);
        IsTabStop = false;
    }
}

/// <summary>
/// A <c>Button</c> element. Besides <c>CommonStates</c> and <c>FocusStates</c>, the template gets
/// <c>ActiveStates</c> (<c>Inactive</c>, <c>Active</c>) and <c>AttentionStates</c> (<c>Calm</c>,
/// <c>Attention</c>). Restyled through <c>Wm.Element.Button</c>.
/// </summary>
public sealed partial class WmButton : Button
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(WmButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(Microsoft.UI.Xaml.Media.ImageSource), typeof(WmButton), new PropertyMetadata(null));

    public static readonly DependencyProperty IconNameProperty =
        DependencyProperty.Register(nameof(IconName), typeof(string), typeof(WmButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActiveProperty =
        DependencyProperty.Register(nameof(Active), typeof(bool), typeof(WmButton),
            new PropertyMetadata(false, (d, _) => ((WmButton)d).UpdateStates()));

    public static readonly DependencyProperty AttentionProperty =
        DependencyProperty.Register(nameof(Attention), typeof(bool), typeof(WmButton),
            new PropertyMetadata(false, (d, _) => ((WmButton)d).UpdateStates()));

    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.Register(nameof(Badge), typeof(string), typeof(WmButton), new PropertyMetadata(string.Empty));

    public WmButton() => DefaultStyleKey = typeof(WmButton);

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public Microsoft.UI.Xaml.Media.ImageSource? Icon
    {
        get => (Microsoft.UI.Xaml.Media.ImageSource?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string IconName
    {
        get => (string)GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    public bool Attention
    {
        get => (bool)GetValue(AttentionProperty);
        set => SetValue(AttentionProperty, value);
    }

    public string Badge
    {
        get => (string)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateStates();
    }

    private void UpdateStates()
    {
        VisualStateManager.GoToState(this, Active ? "Active" : "Inactive", useTransitions: false);
        VisualStateManager.GoToState(this, Attention ? "Attention" : "Calm", useTransitions: false);
    }
}

/// <summary>
/// A <c>Toggle</c> element: a button whose <see cref="Checked"/> shows as the <c>Active</c> state. It
/// never flips itself; the plugin's state decides. Restyled through <c>Wm.Element.Toggle</c>.
/// </summary>
public sealed partial class WmToggle : Button
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(WmToggle), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(Microsoft.UI.Xaml.Media.ImageSource), typeof(WmToggle), new PropertyMetadata(null));

    public static readonly DependencyProperty IconNameProperty =
        DependencyProperty.Register(nameof(IconName), typeof(string), typeof(WmToggle), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CheckedProperty =
        DependencyProperty.Register(nameof(Checked), typeof(bool), typeof(WmToggle),
            new PropertyMetadata(false, (d, _) => ((WmToggle)d).UpdateStates()));

    public WmToggle() => DefaultStyleKey = typeof(WmToggle);

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public Microsoft.UI.Xaml.Media.ImageSource? Icon
    {
        get => (Microsoft.UI.Xaml.Media.ImageSource?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string IconName
    {
        get => (string)GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    public bool Checked
    {
        get => (bool)GetValue(CheckedProperty);
        set => SetValue(CheckedProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateStates();
    }

    private void UpdateStates()
    {
        VisualStateManager.GoToState(this, Checked ? "Active" : "Inactive", useTransitions: false);
        VisualStateManager.GoToState(this, "Calm", useTransitions: false);
    }
}

/// <summary>A short message shown next to an element when its action fails. Restyled through <c>Wm.Notice</c>.</summary>
public sealed partial class WmNotice : Control
{
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(WmNotice), new PropertyMetadata(string.Empty));

    public WmNotice()
    {
        DefaultStyleKey = typeof(WmNotice);
        IsTabStop = false;
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
}
