using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;

namespace MapDropStatistics;

public class MapDropStatisticsSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);
    public DisplaySettings Display { get; set; } = new();
    public TrackingSettings Tracking { get; set; } = new();
    public ActionSettings Actions { get; set; } = new();
    public DebugSettings Debug { get; set; } = new();
}

[Submenu]
public class TrackingSettings
{
    public ToggleNode IncludeEmptyAreasInAverage { get; set; } = new(false);
    public ToggleNode ShowWhenNotTrackingArea { get; set; } = new(true);
    public RangeNode<int> FailUniqueThreshold { get; set; } = new(1000, 0, 20000);
    public ToggleNode CountFailMapToStatistic { get; set; } = new(true);
    public ContentNode<TextNode> CustomTrackedCurrencyItems { get; set; } = new()
    {
        EnableControls = true,
        UseFlatItems = true,
        ItemFactory = () => new TextNode("")
    };
}

[Submenu]
public class ActionSettings
{
    [IgnoreMenu]
    public ButtonNode ResetSessionAverage { get; set; } = new();
    [IgnoreMenu]
    public ButtonNode AddPendingMapToAverage { get; set; } = new();
    [IgnoreMenu]
    public ToggleNode SaveMapStats { get; set; } = new(true);
    [IgnoreMenu]
    public RangeNode<int> MaxSavedMapStats { get; set; } = new(20, 1, 500);
    [IgnoreMenu]
    public ButtonNode ClearSavedMapStats { get; set; } = new();
    [IgnoreMenu]
    public ToggleNode AutoLoadLastSession { get; set; } = new(false);
    [IgnoreMenu]
    public ButtonNode LoadLastSession { get; set; } = new();
}

[Submenu]
public class DisplaySettings
{
    public PositionSettings Position { get; set; } = new();
    public VisualSettings Visuals { get; set; } = new();
    public VisibilitySettings Visibility { get; set; } = new();
    public UIPanelSettings Panels { get; set; } = new();
}

[Submenu]
public class PositionSettings
{
    public RangeNode<float> XPos { get; set; } = new(2, 0, 100);
    public RangeNode<float> YPos { get; set; } = new(7, 0, 100);
}

[Submenu]
public class VisualSettings
{
    public ToggleNode UseCustomFont { get; set; } = new(false);
    public TextNode CustomLoadedFont { get; set; } = new("default:16");
    public RangeNode<int> TextSpacing { get; set; } = new(6, 0, 50);
    public RangeNode<float> BorderPadding { get; set; } = new(6, 1, 40);
    public RangeNode<int> BorderThickness { get; set; } = new(1, 1, 8);
    public RangeNode<float> BorderRounding { get; set; } = new(4, 0, 30);
    public ColorNode HeaderColor { get; set; } = new(new Color(255, 230, 170, 255));
    public ColorNode TextColor { get; set; } = new(Color.White);
    public ColorNode UniqueValueColor { get; set; } = new(new Color(139, 90, 43, 255));
    public ColorNode CurrencyValueColor { get; set; } = new(new Color(255, 215, 0, 255));
    public ColorNode BorderColor { get; set; } = new(new Color(190, 190, 190, 255));
    public ColorNode BackgroundColor { get; set; } = new(new Color(10, 10, 10, 190));
}

[Submenu]
public class VisibilitySettings
{
    public ToggleNode ShowHeader { get; set; } = new(true);
    public ToggleNode ShowArea { get; set; } = new(true);
    public ToggleNode ShowMapsAndFails { get; set; } = new(true);
    public ToggleNode ShowMapTime { get; set; } = new(true);
    public ToggleNode ShowHoTime { get; set; } = new(true);
    public ToggleNode ShowUniqueItems { get; set; } = new(true);
    public ToggleNode ShowT0Uniques { get; set; } = new(true);
    public ToggleNode ShowCurrencyQuantity { get; set; } = new(true);
    public ToggleNode ShowDivineOrbs { get; set; } = new(true);
    public ToggleNode ShowValdosBox { get; set; } = new(true);
    public ToggleNode ShowCustomTrackedItems { get; set; } = new(true);
}

[Submenu(CollapsedByDefault = true)]
public class UIPanelSettings
{
    public ToggleNode RenderOnFullPanels { get; set; } = new(false);
    public ToggleNode RenderOnLargePanels { get; set; } = new(false);
    public ToggleNode RenderOnLeftPanels { get; set; } = new(false);
    public ToggleNode RenderOnRightPanels { get; set; } = new(false);
}

[Submenu(CollapsedByDefault = true)]
public class DebugSettings
{
    public ToggleNode EnableOverlay { get; set; } = new(false);
}
