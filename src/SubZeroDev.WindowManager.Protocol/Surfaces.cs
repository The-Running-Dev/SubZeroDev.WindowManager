namespace SubZeroDev.WindowManager.Protocol;

public enum Orientation { Horizontal, Vertical }

public enum PanelEdge { Top, Bottom, Left, Right }

public sealed record SurfaceContext(string MonitorId, double Scale, Orientation PanelAxis, PanelEdge? Edge);
