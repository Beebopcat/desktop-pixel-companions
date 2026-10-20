using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AzerothCompanion.Controls;
using AzerothCompanion.Core;
using AzerothCompanion.Services;

namespace AzerothCompanion;

public partial class OverlayWindow : Window
{
    private readonly RoamingPlanner roaming = new();
    private readonly List<AvatarHostWindow> hosts = [];
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Random interactionRandom = new();
    private readonly string[] states = ["idle", "idle"];
    private readonly bool[] interacting = [false, false];
    private CharacterProfile first = Characters.All[0], second = Characters.Find("jaina");
    private PrimaryMonitorLayout? primary;
    private FloorHostWindow? floorHost;
    private string flooring = "Grass";
    private int floorHeight;
    private int floorSurfaceOffset;
    private static readonly string[] FloorOptions = ["None", "Grass", "Stone", "Wood", "Snow", "Arcane"];
    private double fraction = .04, avatarScale = .25, previousTick, previousMonitorCheck, nextDuoInteraction = 25;
    private bool clickThrough, duo = true, closed, microphoneEnabled, microphoneListening;

    public event Action? ReadRequested, QuietRequested, OpenRequested, OpenMicRequested;
    public event Action<string>? FlooringChanged;
    public string Flooring => flooring;
    public double AvatarScale => avatarScale;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            Native.ApplyStyle(new WindowInteropHelper(this).Handle, true);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(ControllerHook);
        };
        Loaded += (_, _) => ShowCompanions();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsLoaded || closed) return;
            if (IsVisible) ShowCompanions();
            else { timer.Stop(); foreach (var host in hosts) host.Hide(); floorHost?.Hide(); }
        };
        Closed += (_, _) =>
        {
            closed = true; timer.Stop();
            foreach (var host in hosts) host.Close();
            hosts.Clear();
            floorHost?.Close(); floorHost = null;
        };
        timer.Tick += (_, _) => Tick();
    }

    // Exposes actual avatar visuals for the isolated preview fixture. The controller
    // itself is intentionally invisible and is never a useful screenshot target.
    internal IReadOnlyList<FrameworkElement> AvatarVisuals => hosts.Where(h => h.IsVisible).Select(h => (FrameworkElement)h.Portrait).ToArray();
    internal FrameworkElement? FloorVisual => floorHost?.IsVisible == true ? floorHost.Art : null;

    public void SetFlooring(string? value)
    {
        var selected = FloorOptions.FirstOrDefault(option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase)) ?? "Grass";
        if (flooring == selected) return;
        flooring = selected;
        if (IsLoaded && IsVisible) RefreshLayout(true);
        FlooringChanged?.Invoke(flooring);
    }

    public void SetAvatarScale(double value)
    {
        var selected = double.IsFinite(value) ? Math.Clamp(value, .15, 1) : .25;
        if (Math.Abs(avatarScale - selected) < .00001) return;
        avatarScale = selected;
        if (IsLoaded && IsVisible) RefreshLayout(true);
    }

    public void SetProfile(CharacterProfile profile)
    {
        first = profile;
        if (hosts.Count > 0) hosts[0].SetCharacter(profile);
    }

    public void SetPartner(CharacterProfile profile, bool enabled)
    {
        second = profile; duo = enabled;
        if (hosts.Count > 1) hosts[1].SetCharacter(profile);
        if (IsLoaded && IsVisible) RefreshLayout(true);
    }

    public void SetState(string state, string? caption = null)
    {
        SetBodyState(0, state);
        SetBodyState(1, state == "speaking" ? "idle" : state);
    }

    public void SetSpeaker(string id, string state, string? caption = null)
    {
        SetBodyState(0, id == first.Id ? state : "idle");
        SetBodyState(1, id == second.Id ? state : "idle");
    }

    public void SetSpeechLevel(string id, float level)
    {
        if (hosts.Count > 0) hosts[0].Portrait.SetSpeechLevel(id == first.Id ? level : 0);
        if (hosts.Count > 1) hosts[1].Portrait.SetSpeechLevel(id == second.Id ? level : 0);
    }

    public void ConfigureFootprint(nint target, double areaFraction)
    {
        // target remains accepted for source compatibility. The selected game never
        // determines desktop-companion placement: Windows' primary flag does.
        fraction = double.IsFinite(areaFraction) ? Math.Clamp(areaFraction, .015, .045) : .04;
        if (IsLoaded && IsVisible) RefreshLayout(true);
    }

    public void SetClickThrough(bool value)
    {
        clickThrough = value;
        foreach (var host in hosts) host.SetClickThrough(value);
    }

    public void SetMicrophone(bool enabled, bool listening)
    { microphoneEnabled = enabled; microphoneListening = listening; }

    public void CheckMonitor()
    {
        if (!closed && IsLoaded && IsVisible) RefreshLayout(false);
    }

    private void SetBodyState(int index, string state)
    {
        states[index] = state;
        if (hosts.Count > index) hosts[index].Portrait.SetState(state);
        RefreshPause(index);
    }

    private void RefreshPause(int index) => roaming.PauseBody(index,
        FunctionalTestMode.Active || interacting[index] || states[index] is "speaking" or "listening" or "thinking" or "reading" or "quiet" or "paused"
        || hosts.Count > index && hosts[index].Portrait.IsPerforming);

    private void ShowCompanions()
    {
        if (closed) return;
        RefreshLayout(true);
        previousTick = clock.Elapsed.TotalSeconds;
        if (primary != null) timer.Start();
    }

    private void RefreshLayout(bool force)
    {
        PrimaryMonitorLayout current;
        try { current = MonitorDesktopService.GetPrimary(); }
        catch (InvalidOperationException)
        {
            // Fail closed during a display transition rather than leave an avatar
            // on a monitor whose primary status is no longer known.
            primary = null; foreach (var host in hosts) host.Hide(); floorHost?.Hide(); return;
        }
        if (!force && current == primary && hosts.Count == (duo ? 2 : 1) && hosts.All(h => h.IsVisible)) return;
        primary = current;
        var work = current.WorkArea; var bounds = current.Monitor;
        floorHeight = flooring == "None" ? 0 : Math.Min(6, Math.Min(work.Height - 2, (int)Math.Floor((double)bounds.Width * bounds.Height * fraction * .4 / work.Width)));
        floorHeight = Math.Max(0, floorHeight);
        // Soles meet the continuous turf, while the higher blades sit behind them.
        floorSurfaceOffset = Math.Min(2, floorHeight);
        roaming.Configure(work.Left, work.Top, work.Width, work.Height - floorSurfaceOffset, bounds.Width, bounds.Height, duo ? 2 : 1, fraction, (double)work.Width * floorHeight, avatarScale);
        if (floorHeight > 0)
        {
            floorHost ??= new FloorHostWindow(this);
            floorHost.Art.FloorStyle = flooring;
            if (!floorHost.IsVisible) floorHost.Show();
            floorHost.Place(); floorHost.Art.InvalidateVisual();
        }
        else floorHost?.Hide();
        while (hosts.Count > roaming.Bodies.Count) { hosts[^1].Close(); hosts.RemoveAt(hosts.Count - 1); }
        while (hosts.Count < roaming.Bodies.Count)
        {
            var host = new AvatarHostWindow(this, hosts.Count);
            hosts.Add(host); host.SetCharacter(hosts.Count == 1 ? first : second);
            host.Portrait.SetState(states[host.Index]); host.SetClickThrough(clickThrough);
        }
        for (var index = 0; index < hosts.Count; index++)
        {
            RefreshPause(index);
            if (!hosts[index].IsVisible) hosts[index].Show();
        }
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.Topmost, work.Left, work.Top, 1, 1, Native.NoActivate);
        ApplyPositions();
    }

    private void Tick()
    {
        if (!IsVisible || closed) return;
        var now = clock.Elapsed.TotalSeconds;
        if (now - previousMonitorCheck >= 1) { previousMonitorCheck = now; CheckMonitor(); }
        if (primary == null) { previousTick = now; return; }
        for (var index = 0; index < hosts.Count; index++) RefreshPause(index);
        TryDuoInteraction(now);
        roaming.Step(now - previousTick); previousTick = now;
        ApplyPositions();
        timer.Interval = TimeSpan.FromMilliseconds(roaming.Bodies.Any(b => b.Walking) ? 33 : 100);
    }

    private void TryDuoInteraction(double now)
    {
        if (FunctionalTestMode.Active) return;
        if (now < nextDuoInteraction || hosts.Count != 2 || states.Any(state => state != "idle") ||
            interacting.Any(value => value) || hosts.Any(host => host.Portrait.IsPerforming) || roaming.Bodies.Any(body => body.Walking)) return;
        var firstBody = roaming.Bodies[0]; var secondBody = roaming.Bodies[1];
        if (secondBody.X - firstBody.X - firstBody.Width > Math.Min(firstBody.Width, secondBody.Width) * .5) return;
        nextDuoInteraction = now + 35 + interactionRandom.NextDouble() * 40;
        firstBody.FacingLeft = false; secondBody.FacingLeft = true;
        hosts[0].Portrait.SetRoamingMotion(false, false); hosts[1].Portrait.SetRoamingMotion(false, true);
        string[] interactions = ["duo.wave", "duo.high-five", "duo.show-prop", "duo.soccer"];
        var interaction = interactions[interactionRandom.Next(interactions.Length)];
        hosts[0].Portrait.PlayAnimation(interaction); hosts[1].Portrait.PlayAnimation(interaction);
        RefreshPause(0); RefreshPause(1);
    }

    private void ApplyPositions()
    {
        foreach (var host in hosts)
        {
            if (host.Index >= roaming.Bodies.Count) continue;
            var body = roaming.Bodies[host.Index];
            host.Place(body);
            host.Portrait.SetRoamingMotion(body.Walking, body.FacingLeft);
        }
    }

    private void SetInteraction(int index, bool value)
    { interacting[index] = value; RefreshPause(index); }

    private nint ControllerHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE
        if (message is 0x007E or 0x001A or 0x02E0) Dispatcher.BeginInvoke(CheckMonitor);
        return 0;
    }

    internal object GeometryEvidence()
    {
        var monitor = primary ?? MonitorDesktopService.GetPrimary();
        var individual = hosts.Where(h => h.IsVisible).Select(h =>
        {
            var bounds = MonitorDesktopService.GetWindowBounds(h.Handle);
            return new { index = h.Index, character = h.Index == 0 ? first.Id : second.Id, left = bounds.Left, top = bounds.Top,
                right = bounds.Right, bottom = bounds.Bottom, width = bounds.Width, height = bounds.Height,
                withinPrimaryWorkArea = monitor.WorkArea.Contains(bounds), bottomAnchored = bounds.Bottom == monitor.WorkArea.Bottom - floorSurfaceOffset,
                referenceSide = roaming.Bodies[h.Index].ReferenceSide, linearScale = avatarScale,
                noActivate = (Native.GetWindowLongPtr(h.Handle, -20).ToInt64() & 0x08000000) != 0,
                clickThrough = (Native.GetWindowLongPtr(h.Handle, -20).ToInt64() & 0x20) != 0,
                topmost = (Native.GetWindowLongPtr(h.Handle, -20).ToInt64() & 8) != 0,
                dpi = Native.GetDpiForWindow(h.Handle) };
        }).ToArray();
        var floorBounds = floorHost?.IsVisible == true ? MonitorDesktopService.GetWindowBounds(floorHost.Handle) : default;
        var floorArea = (double)floorBounds.Width * floorBounds.Height;
        var floorStyle = floorHost?.IsVisible == true ? Native.GetWindowLongPtr(floorHost.Handle, -20).ToInt64() : 0;
        var area = individual.Sum(b => (double)b.width * b.height) + floorArea;
        var monitorArea = (double)monitor.Monitor.Width * monitor.Monitor.Height;
        var collisionFree = individual.Length < 2 || individual[0].right <= individual[1].left || individual[1].right <= individual[0].left;
        return new
        {
            widthPixels = individual.Sum(b => b.width), heightPixels = individual.Select(b => b.height).DefaultIfEmpty(0).Max(),
            monitorPixels = monitorArea, combinedAreaPixels = area, areaPercent = area / monitorArea * 100,
            withinCap = area <= monitorArea * .045, duo, avatarLinearScale = avatarScale, primaryDevice = monitor.Device, primaryFlagVerified = true,
            primaryBounds = monitor.Monitor, primaryWorkArea = monitor.WorkArea, individualBounds = individual,
            floor = new { style = flooring, visible = floorArea > 0, bounds = floorBounds, areaPixels = floorArea,
                surfaceY = monitor.WorkArea.Bottom - floorSurfaceOffset, bladeBandHeight = floorHeight - floorSurfaceOffset,
                withinPrimaryWorkArea = floorArea == 0 || monitor.WorkArea.Contains(floorBounds),
                bottomAnchored = floorArea == 0 || floorBounds.Bottom == monitor.WorkArea.Bottom,
                noActivate = floorArea == 0 || (floorStyle & 0x08000000) != 0, clickThrough = floorArea == 0 || (floorStyle & 0x20) != 0,
                topmost = floorArea == 0 || (floorStyle & 8) != 0 },
            withinPrimaryWorkArea = individual.All(b => b.withinPrimaryWorkArea) && (floorArea == 0 || monitor.WorkArea.Contains(floorBounds)), collisionFree,
            noFullscreenInputWindow = true, controllerPixels = 1, realGameCaptured = false, performanceBenchmarked = false
        };
    }

    internal static bool IsInteractiveSource(DependencyObject? source)
    {
        for (var node = source; node != null; node = (node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node))
            if (node is ButtonBase or Selector or System.Windows.Controls.TextBox) return true;
        return false;
    }

    private sealed class AvatarHostWindow : Window
    {
        private readonly OverlayWindow controller;
        private bool clickThrough, dragging;
        private int dragStart;
        private double bodyStart;
        public int Index { get; }
        public PortraitControl Portrait { get; } = new();
        public nint Handle => new WindowInteropHelper(this).Handle;

        public AvatarHostWindow(OverlayWindow owner, int index)
        {
            controller = owner; Index = index;
            Width = Height = 100; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
            ShowActivated = false; Focusable = false; Topmost = true; WindowStartupLocation = WindowStartupLocation.Manual;
            Portrait.Compact(); Portrait.Focusable = false;
            Content = new System.Windows.Controls.Viewbox { Stretch = Stretch.Uniform, StretchDirection = System.Windows.Controls.StretchDirection.Both, Child = Portrait };
            SourceInitialized += (_, _) =>
            {
                Native.ApplyStyle(Handle, clickThrough);
                HwndSource.FromHwnd(Handle)?.AddHook(Hook);
                if (controller.roaming.Bodies.Count > Index) Place(controller.roaming.Bodies[Index]);
            };
            MouseRightButtonUp += (_, e) => { if (!clickThrough) { ShowMenu(); e.Handled = true; } };
            MouseLeftButtonDown += (_, e) =>
            {
                if (clickThrough || !Native.GetCursorPos(out var cursor)) return;
                dragging = true; dragStart = cursor.X; bodyStart = controller.roaming.Bodies[Index].X;
                controller.SetInteraction(Index, true); CaptureMouse(); e.Handled = true;
            };
            MouseMove += (_, e) =>
            {
                if (!dragging || e.LeftButton != MouseButtonState.Pressed || !Native.GetCursorPos(out var cursor)) return;
                controller.roaming.SetPosition(Index, bodyStart + cursor.X - dragStart); controller.ApplyPositions();
            };
            MouseLeftButtonUp += (_, e) => { if (dragging) { EndDrag(); e.Handled = true; } };
            LostMouseCapture += (_, _) => EndDrag();
            Closed += (_, _) => { dragging = false; };
        }

        public void SetCharacter(CharacterProfile profile) { Title = "Desktop Pixel Companions · " + profile.Name; Portrait.SetCharacter(profile); }
        public void SetClickThrough(bool value)
        {
            clickThrough = value;
            if (value) EndDrag();
            if (Handle != 0) Native.ApplyStyle(Handle, value);
        }
        public void Place(RoamingBody body)
        {
            if (Handle == 0) return;
            Portrait.Width = Portrait.Height = body.ReferenceSide;
            var x = (int)Math.Round(body.X); var y = (int)Math.Round(body.Y);
            var width = (int)body.Width; var height = (int)body.Height;
            var current = MonitorDesktopService.GetWindowBounds(Handle);
            if (current.Left != x || current.Top != y || current.Width != width || current.Height != height)
                Native.SetWindowPos(Handle, Native.Topmost, x, y, width, height, Native.NoActivate);
        }
        private void EndDrag()
        {
            if (!dragging) return;
            dragging = false; if (IsMouseCaptured) ReleaseMouseCapture(); controller.SetInteraction(Index, false);
        }
        private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == 0x0021) { handled = true; return 3; }
            if (message == 0x0084 && clickThrough) { handled = true; return -1; } // HTTRANSPARENT
            if (message == 0x0046 && lParam != 0 && controller.primary != null && Index < controller.roaming.Bodies.Count)
            {
                // WPF's DPI resize, native move requests and scripted repositioning
                // all pass this gate before Windows applies any new geometry.
                var position = Marshal.PtrToStructure<Native.WindowPosition>(lParam);
                var body = controller.roaming.Bodies[Index]; var work = controller.primary.WorkArea;
                position.X = Math.Clamp((int)Math.Round(body.X), work.Left, work.Right - (int)body.Width);
                position.Y = work.Bottom - controller.floorSurfaceOffset - (int)body.Height;
                position.Width = (int)body.Width; position.Height = (int)body.Height;
                position.Flags = (position.Flags & ~3u) | Native.NoActivate;
                Marshal.StructureToPtr(position, lParam, false);
            }
            if (message is 0x02E0 or 0x007E or 0x001A) Dispatcher.BeginInvoke(controller.CheckMonitor);
            return 0;
        }

        private void ShowMenu()
        {
            if (!Native.GetCursorPos(out var cursor)) return;
            var menu = Native.CreatePopupMenu();
            if (menu == 0) return;
            controller.SetInteraction(Index, true);
            try
            {
                Native.AppendMenu(menu, 0, 1, "Read selected game window");
                Native.AppendMenu(menu, 0, 2, controller.microphoneListening ? "Close microphone (listening)" : controller.microphoneEnabled ? "Close microphone" : "Open microphone");
                Native.AppendMenu(menu, 0, 3, "Quiet");
                Native.AppendMenu(menu, 0, 4, "Open companion controls");
                Native.AppendMenu(menu, 0x800, 0, "");
                Native.AppendMenu(menu, FunctionalTestMode.Active ? 1u : 0u, 5, FunctionalTestMode.Active ? "Animations awaiting five visual passes" : "Preview a wave animation");
                if (!FunctionalTestMode.Active) Native.AppendMenu(menu, 0, 6, "Summon a character prop");
                var floorMenu = Native.CreatePopupMenu();
                if (floorMenu != 0)
                {
                    for (var index = 0; index < FloorOptions.Length; index++) Native.AppendMenu(floorMenu, controller.flooring == FloorOptions[index] ? 8u : 0u, (nuint)(20 + index), FloorOptions[index]);
                    Native.AppendMenu(menu, 0x10, (nuint)floorMenu, "Flooring");
                }
                var animations = Portrait.AnimationIds.ToArray();
                var animationMenu = FunctionalTestMode.Active ? (nint)0 : Native.CreatePopupMenu();
                if (animationMenu != 0)
                {
                    foreach (var group in animations.Select((id, index) => (Id: id, Index: index)).GroupBy(item => item.Id.Split('.')[0]))
                    {
                        var categoryMenu = Native.CreatePopupMenu();
                        if (categoryMenu == 0) continue;
                        foreach (var item in group)
                            Native.AppendMenu(categoryMenu, 0, (nuint)(100 + item.Index), item.Id[(item.Id.IndexOf('.') + 1)..].Replace('-', ' '));
                        Native.AppendMenu(animationMenu, 0x10, (nuint)categoryMenu, group.Key);
                    }
                    Native.AppendMenu(menu, 0x10, (nuint)animationMenu, $"Choose animation ({animations.Length})");
                }
                // Native popup tracking avoids a focusable WPF popup. Never call
                // SetForegroundWindow: only an explicit Open command may focus UI.
                var command = Native.TrackPopupMenuEx(menu, 0x100 | 0x80 | 2, cursor.X, cursor.Y, Handle, 0);
                switch (command)
                {
                    case 1: controller.ReadRequested?.Invoke(); break;
                    case 2: controller.OpenMicRequested?.Invoke(); break;
                    case 3: controller.QuietRequested?.Invoke(); break;
                    case 4: controller.OpenRequested?.Invoke(); break;
                    case 5: Portrait.PlayAnimation("gesture.wave"); break;
                    case 6: Portrait.SummonRandomProp(); break;
                    default:
                        if (command >= 20 && command < 20 + FloorOptions.Length) controller.SetFlooring(FloorOptions[command - 20]);
                        else if (command >= 100 && command < 100 + animations.Length) Portrait.PlayAnimation(animations[command - 100]);
                        break;
                }
            }
            finally { Native.DestroyMenu(menu); controller.SetInteraction(Index, false); }
        }
    }

    private sealed class FloorHostWindow : Window
    {
        private readonly OverlayWindow controller;
        public FloorArt Art { get; } = new();
        public nint Handle => new WindowInteropHelper(this).Handle;
        public FloorHostWindow(OverlayWindow controller)
        {
            this.controller = controller;
            Width = 100; Height = 6; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
            ShowActivated = false; Focusable = false; Topmost = true; IsHitTestVisible = false;
            WindowStartupLocation = WindowStartupLocation.Manual; Art.ClipToBounds = true; Content = Art;
            SourceInitialized += (_, _) => { Native.ApplyStyle(Handle, true); HwndSource.FromHwnd(Handle)?.AddHook(Hook); Place(); };
        }
        public void Place()
        {
            if (Handle == 0 || controller.primary == null) return;
            var work = controller.primary.WorkArea;
            Native.SetWindowPos(Handle, Native.Topmost, work.Left, work.Bottom - controller.floorHeight, work.Width, controller.floorHeight, Native.NoActivate);
        }
        private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == 0x0021) { handled = true; return 3; }
            if (message == 0x0084) { handled = true; return -1; }
            if (message == 0x0046 && lParam != 0 && controller.primary != null)
            {
                var position = Marshal.PtrToStructure<Native.WindowPosition>(lParam); var work = controller.primary.WorkArea;
                position.X = work.Left; position.Y = work.Bottom - controller.floorHeight;
                position.Width = work.Width; position.Height = controller.floorHeight;
                position.Flags = (position.Flags & ~3u) | Native.NoActivate;
                Marshal.StructureToPtr(position, lParam, false);
            }
            if (message is 0x02E0 or 0x007E or 0x001A) Dispatcher.BeginInvoke(controller.CheckMonitor);
            return 0;
        }
    }

    // Six physical pixels high, painted only on layout/style changes. Translucent
    // procedural pixel rows need no texture download, animation timer or input area.
    private sealed class FloorArt : FrameworkElement
    {
        public FloorArt() { SnapsToDevicePixels = true; RenderOptions.SetEdgeMode(this, EdgeMode.Aliased); }
        public string FloorStyle { get; set; } = "Grass";
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            var dpi = VisualTreeHelper.GetDpi(this); var px = 1 / dpi.DpiScaleX; var py = 1 / dpi.DpiScaleY;
            var rows = Math.Max(1, (int)Math.Round(ActualHeight / py));
            if (FloorStyle == "Grass")
            {
                var pixels = Math.Max(1, (int)Math.Round(ActualWidth / px));
                var palette = new[] { PixelBrush(170, 73, 103, 45), PixelBrush(155, 101, 132, 60), PixelBrush(165, 54, 85, 38), PixelBrush(150, 85, 118, 49) };
                var soil = PixelBrush(130, 54, 58, 31);
                // A continuous low turf row establishes the actual walking surface.
                // Uneven, forked tufts grow above it instead of suggesting a false
                // baseline at the highest isolated blade.
                var surface = Math.Max(0, rows - 2);
                drawing.DrawRectangle(palette[0], null, new Rect(0, surface * py, ActualWidth, py));
                if (rows > 1) drawing.DrawRectangle(soil, null, new Rect(0, (rows - 1) * py, ActualWidth, py));
                uint random = 0x4A37B291;
                uint Next() { random ^= random << 13; random ^= random >> 17; random ^= random << 5; return random; }
                void Pixel(int x, int y, Brush brush)
                { if (x >= 0 && x < pixels && y >= 0 && y < rows) drawing.DrawRectangle(brush, null, new Rect(x * px, y * py, px, py)); }
                for (var x = 0; x < pixels; x += 2 + (int)(Next() % 5))
                {
                    var height = 1 + (int)(Next() % (uint)Math.Max(1, surface));
                    var lean = (int)(Next() % 3) - 1; var tone = palette[(int)(Next() % (uint)palette.Length)];
                    for (var tip = 1; tip <= height; tip++) Pixel(x + (tip > 1 ? lean : 0), surface - tip, tone);
                    if (height > 1 && Next() % 3 == 0) Pixel(x - lean - 1, surface - 1, palette[1]);
                    Pixel(x, surface, palette[(int)(Next() % (uint)palette.Length)]);
                }
                return;
            }
            var colors = FloorStyle switch
            {
                "Stone" => (Color.FromArgb(100, 112, 120, 130), Color.FromArgb(120, 175, 185, 193)),
                "Wood" => (Color.FromArgb(105, 108, 69, 39), Color.FromArgb(120, 179, 119, 60)),
                "Snow" => (Color.FromArgb(80, 145, 180, 199), Color.FromArgb(120, 220, 239, 247)),
                "Arcane" => (Color.FromArgb(90, 66, 53, 115), Color.FromArgb(125, 174, 133, 225)),
                _ => (Color.FromArgb(100, 53, 75, 38), Color.FromArgb(120, 106, 139, 64))
            };
            var baseBrush = new SolidColorBrush(colors.Item1); baseBrush.Freeze();
            var detailBrush = new SolidColorBrush(colors.Item2); detailBrush.Freeze();
            drawing.DrawRectangle(baseBrush, null, new Rect(0, Math.Max(0, ActualHeight - 2 * py), ActualWidth, Math.Min(ActualHeight, 2 * py)));
            for (var pixel = 0; pixel < (int)Math.Ceiling(ActualWidth / px); pixel += 4)
            {
                var stripe = pixel / 4;
                var height = FloorStyle == "Grass" ? 2 + stripe * 7 % Math.Max(1, rows - 1) : FloorStyle == "Snow" ? 2 + stripe % 3 : rows - 2;
                height = Math.Clamp(height, 1, rows);
                var width = FloorStyle is "Stone" or "Wood" ? 3 : 1;
                drawing.DrawRectangle(detailBrush, null, new Rect(pixel * px, ActualHeight - height * py, Math.Max(0, Math.Min(width * px, ActualWidth - pixel * px)), height * py));
            }
        }
        private static Brush PixelBrush(byte alpha, byte red, byte green, byte blue)
        { var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue)); brush.Freeze(); return brush; }
    }

    private static class Native
    {
        public static readonly nint Topmost = new(-1);
        public const uint NoActivate = 0x10;
        public static void ApplyStyle(nint handle, bool clickThrough)
        {
            if (handle == 0) return;
            var style = GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000 | 0x80;
            style = clickThrough ? style | 0x20 : style & ~0x20;
            SetWindowLongPtr(handle, -20, new nint(style));
        }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct WindowPosition { public nint Hwnd, After; public int X, Y, Width, Height; public uint Flags; }
        [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point cursor);
        [DllImport("user32.dll")] public static extern nint CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
        [DllImport("user32.dll")] public static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);
        [DllImport("user32.dll")] public static extern bool DestroyMenu(nint menu);
    }
}
