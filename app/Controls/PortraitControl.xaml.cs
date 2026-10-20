using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AzerothCompanion.Core;

namespace AzerothCompanion.Controls;

public partial class PortraitControl : UserControl
{
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly PortraitAnimation animation = new();
    private BitmapSource[] frames = [];
    private bool pixelSheet;
    private int shownFrame = -1;
    private string state = "idle", characterId = "thrall";
    private float speechLevel;
    private long speechLevelAt;
    private bool walking, facingLeft;
    public const double AnimationSpeed = 3;

    public void SetRoamingMotion(bool walking, bool facingLeft)
    {
        if(FunctionalTestMode.Active)return;
        if (this.walking == walking && this.facingLeft == facingLeft) return;
        if(this.walking!=walking)gait.Reset();
        this.walking = walking; this.facingLeft = facingLeft;
        UpdatePose();
    }

    public PortraitControl()
    {
        InitializeComponent();
        timer.Tick += (_, _) => UpdatePose();
        Loaded += (_, _) => { UpdatePose(); UpdateTimer(); };
        SizeChanged += (_, _) => UpdatePose();
        Unloaded += (_, _) => timer.Stop();
        IsVisibleChanged += (_, _) => UpdateTimer();
    }

    public void Compact() => ActivityBadge.Visibility = Visibility.Collapsed;

    public void SetCharacter(CharacterProfile profile)
    {
        characterId = profile.Id;
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets");
        var candidates = new[]
        {
            (Path: Path.Combine(folder, profile.Id + "-pixel-v1.png"), Columns: 3, Rows: 4, Pixel: true),
            (Path: Path.Combine(folder, profile.Id + "-sheet.png"), Columns: 2, Rows: 3, Pixel: false),
            (Path: Path.Combine(folder, profile.Id + ".png"), Columns: 1, Rows: 1, Pixel: false)
        };
        frames = []; pixelSheet = false; shownFrame = -1; Portrait.Source = null;
        Fallback.Text = profile.Name + "\nAvatar artwork pending";
        Fallback.Visibility = Visibility.Visible;
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate.Path)) continue;
            try
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(candidate.Path); image.EndInit(); image.Freeze();
                int width = image.PixelWidth / candidate.Columns, height = image.PixelHeight / candidate.Rows;
                if (width < 1 || height < 1) continue;
                // Read complete cells at actual output size. Exclude any far-edge
                // remainder; full-body artwork is never cropped into a face portrait.
                frames = Enumerable.Range(0, candidate.Columns * candidate.Rows).Select(index =>
                {
                    var frame = new CroppedBitmap(image, new Int32Rect(index % candidate.Columns * width,
                        index / candidate.Columns * height, width, height));
                    frame.Freeze(); return (BitmapSource)frame;
                }).ToArray();
                pixelSheet = candidate.Pixel;
                Fallback.Visibility = Visibility.Collapsed;
                break;
            }
            catch (Exception error) when (error is IOException or NotSupportedException or FormatException or ArgumentException)
            {
                // An incomplete new sheet must not hide a usable legacy asset.
                frames = [];
            }
        }
        InitializePerformance(profile.Id);
        InitializeWeapons(profile.Id);
        RenderOptions.SetBitmapScalingMode(Portrait, pixelSheet ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        ActivityDot.Fill = (Brush)new BrushConverter().ConvertFromString(profile.Accent)!;
        animation.Reset(clock.Elapsed.TotalSeconds * AnimationSpeed, profile.Id.Sum(character => character));
        animation.SetState(state, clock.Elapsed.TotalSeconds * AnimationSpeed);
        SetSpeechLevel(0);
        UpdatePose(); UpdateTimer();
    }

    public void SetState(string value)
    {
        state = value;
        animation.SetState(value, clock.Elapsed.TotalSeconds * AnimationSpeed);
        if(value is not ("idle" or "amused" or "wave" or "greeting")) StopPerformance();
        if (value != "speaking") SetSpeechLevel(0);
        ActivityText.Text = value switch
        {
            "speaking" => "Speaking", "thinking" => "Considering your question",
            "listening" => "Listening to you", "reading" => "Reading the selected window",
            "quiet" => "Quiet mode", "paused" => "Observation paused", "amused" => "Amused",
            "wave" or "greeting" => "Hello", _ => "Ready when you are"
        };
        UpdatePose(); UpdateTimer();
    }

    /// <summary>Thread-safe normalized 0..1 audio amplitude, not phoneme lip-sync.
    /// Silence or a feed older than 350ms closes the mouth automatically.</summary>
    public void SetSpeechLevel(float level)
    {
        Volatile.Write(ref speechLevel, float.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0);
        Interlocked.Exchange(ref speechLevelAt, Stopwatch.GetTimestamp());
    }

    private void UpdateTimer()
    {
        if(FunctionalTestMode.Active){timer.Stop();return;}
        if (IsLoaded && IsVisible && frames.Length > 0 && state is not ("quiet" or "paused")) timer.Start();
        else timer.Stop();
    }

    private void UpdatePose()
    {
        if(FunctionalTestMode.Active)
        {
            timer.Stop();StopPerformance();
            if(frames.Length>0){Portrait.Source=frames[0];shownFrame=0;}
            Breath.ScaleX=Breath.ScaleY=1;Tilt.Angle=0;
            BodyMotion.X=BodyMotion.Y=FrameAlignment.X=FrameAlignment.Y=0;
            if(pixelSheet)AlignFeetToFloor();
            return;
        }
        var now = clock.Elapsed.TotalSeconds * AnimationSpeed;
        if(TryUpdateWeapon(now)) return;
        if (UpdatePerformance(now)) return;
        var age = (Stopwatch.GetTimestamp() - Interlocked.Read(ref speechLevelAt)) / (double)Stopwatch.Frequency;
        var pose = animation.Sample(now, age <= .35 ? Volatile.Read(ref speechLevel) : 0);
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(30,pose.IntervalMilliseconds / AnimationSpeed));
        int index = pixelSheet ? pose.Frame : LegacyFrame(pose.Frame);
        if (frames.Length > 0)
        {
            index = Math.Clamp(index, 0, frames.Length - 1);
            if (shownFrame != index) { Portrait.Source = frames[index]; shownFrame = index; }
        }
        // Whole-cell Uniform fit plus integer translations keeps pixel art crisp;
        // the six-pixel margin leaves room for these small body movements.
        Breath.ScaleX = facingLeft ? -1 : 1; Breath.ScaleY = 1;
        Tilt.Angle = 0;
        BodyMotion.X = pose.OffsetX; BodyMotion.Y = pose.OffsetY;
        FrameAlignment.X = FrameAlignment.Y = 0;
        if(pixelSheet)AlignFeetToFloor();
        if (!pixelSheet && frames.Length >= 6)
        {
            // Keep the established view-only alignment of legacy 2x3 portraits.
            double scale = Math.Min(Math.Max(1, Portrait.ActualWidth) / 512.0, Math.Max(1, Portrait.ActualHeight) / 512.0);
            FrameAlignment.X = (characterId switch
            { "thrall" => index % 2 == 1 ? 20.5 : 0, "jaina" => index % 2 == 1 ? 28.5 : 0, "varian" => index % 2 == 1 ? 23.5 : 0, _ => 0 }) * scale;
            FrameAlignment.Y = (characterId switch
            { "thrall" => index >= 2 ? 3 : 0, "jaina" => index >= 4 ? -4 : index >= 2 ? -3 : 0, "varian" => index >= 4 ? 14 : index >= 2 ? 9 : 0, _ => 0 }) * scale;
        }
    }

    private int LegacyFrame(int pixelFrame)
    {
        if (frames.Length < 6) return 0;
        return pixelFrame switch { 1 => 1, 4 or 6 => 2, 5 => 3, 8 or 9 => 4, 10 or 11 => 5, _ => 0 };
    }
}
