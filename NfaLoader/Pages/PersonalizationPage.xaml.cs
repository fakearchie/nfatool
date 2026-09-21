using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using NfaLoader.Localization;
using NfaLoader.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using ShapesPath = Microsoft.UI.Xaml.Shapes.Path;

namespace NfaLoader.Pages;

public sealed partial class PersonalizationPage : Page, INotifyPropertyChanged
{
    private const uint OutputSize = 512;     // Exported avatar side length (pixels)
    private const uint PreviewPx = 184;      // Main preview bitmap resolution (largest size is 184, 64/32 are shown scaled down from it)
    private const double StageW = 388;       // Crop stage content width (DIP, = crop stage 420 - padding 16×2)
    private const double StageH = 288;       // Crop stage content height (DIP, = crop stage 320 - padding 16×2)
    private const double MaxZoom = 5;        // Max zoom of the background image (relative to "fit")
    private const double HandleHit = 16;     // Handle hit radius (DIP)
    private const double HandleSize = 10;    // Handle visual side length (DIP)
    private const double MinSourcePx = 184;  // Steam requires avatars ≥184px, so the smallest selection maps to this many source pixels
    private const double MinSelFloor = 28;   // Floor for the smallest displayed selection side length (DIP)
    private const int NicknameMaxLength = 64;

    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    // Current source image (raw bytes, decoded and cropped again by the selection for export/preview). null = no image loaded yet.
    private byte[]? _sourceBytes;
    private double _srcW;                 // Source image pixel width (with EXIF orientation applied)
    private double _srcH;
    private double _fitScale;             // Display scale at "fit": crop stage DIP / source pixels (= zoom 1)
    private double _dispW;                // Whole image display width at fit (DIP) = source width × _fitScale
    private double _dispH;
    private double _zoom = 1;             // Background image zoom factor (≥1, 1 = fit)
    private double _panX;                 // Offset of the background image top-left corner in crop stage coordinates (DIP)
    private double _panY;
    private double _selX;                 // Top-left X of the square selection (crop stage coordinates, DIP)
    private double _selY;
    private double _selSize;              // Side length of the square selection (DIP)

    private enum DragMode { None, Pan, TL, T, TR, R, BR, B, BL, L }
    private DragMode _dragMode;
    private Point _dragStartPointer;
    private double _startPanX, _startPanY;

    // Overlay shapes (built on the first image load, after that only position/geometry changes).
    private bool _overlayBuilt;
    private ShapesPath? _dim;             // Darkening mask outside the selection (EvenOdd cutout)
    private Rectangle? _selBorder;        // White selection border
    private readonly Line[] _grid = new Line[4];       // Rule-of-thirds lines
    private readonly Rectangle[] _handles = new Rectangle[8]; // 8 handles: TL,T,TR,R,BR,B,BL,L

    private bool _loadingSettings;        // Blocks change write-back while nickname / summary / alias history option are first filled in
    private bool _previewRendering;       // Preview is rendering asynchronously
    private bool _previewPending;         // A new framing arrived during rendering, render again when done (latest wins)

    public PersonalizationPage()
    {
        InitializeComponent();

        // Clip the background image that overflows after zoom/pan to the crop stage.
        CropArea.Clip = new RectangleGeometry { Rect = new Rect(0, 0, StageW, StageH) };

        Loc.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML binding entry point: {x:Bind Strings.Get('Key'), Mode=OneWay}.</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _loadingSettings = true;
        var settings = AppState.SettingsService.Load();
        NicknameBox.Text = settings.PersonaName ?? string.Empty;
        RealNameBox.Text = settings.ProfileRealName ?? string.Empty;
        SummaryBox.Text = settings.ProfileSummary ?? string.Empty;
        ClearAliasCheckBox.IsChecked = settings.ClearAliasHistoryOnPersonalize;
        _loadingSettings = false;
        UpdateNicknameCounter();

        // On first visit, if a saved avatar exists, load it into the crop area as a source image that can be fine-tuned further.
        if (_sourceBytes is null)
        {
            var avatarPath = AppState.SettingsService.PersonalizationAvatarPath;
            if (File.Exists(avatarPath))
            {
                _ = LoadSavedAvatarAsync(avatarPath);
            }
        }
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings))));
    }

    // ---- Nickname / summary / alias history option (saved to disk as you type) ----

    private void NicknameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateNicknameCounter();
        SaveSettings(settings => settings.PersonaName = NicknameBox.Text);
    }

    private void RealNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveSettings(settings => settings.ProfileRealName = RealNameBox.Text);
    }

    private void SummaryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveSettings(settings => settings.ProfileSummary = SummaryBox.Text);
    }

    private void ClearAliasCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        SaveSettings(settings => settings.ClearAliasHistoryOnPersonalize = ClearAliasCheckBox.IsChecked == true);
    }

    private void SaveSettings(Action<AppSettings> apply)
    {
        if (_loadingSettings)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        apply(settings);
        AppState.SettingsService.Save(settings);
    }

    private void UpdateNicknameCounter()
    {
        NicknameCounter.Text = $"{NicknameBox.Text.Length}/{NicknameMaxLength}";
    }

    // ---- Pick / load image ----

    private async void PickImageButton_Click(object sender, RoutedEventArgs e)
    {
        // Use the classic Win32 file dialog instead of WinRT FileOpenPicker: with unpackaged WinUI + running elevated,
        // the WinRT picker often silently fails to open (the broker fails without throwing), while Win32 GetOpenFileName works as usual.
        try
        {
            var path = PickImageFileWin32(MainWindow.Hwnd);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            await LoadSourceAsync(await File.ReadAllBytesAsync(path));
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to open the avatar image picker.", ex);
            // Use the global status bar (always visible) and include the reason, so it never again "does nothing" with no visible error.
            AppState.ShowStatus($"{Loc.T("Personalization_ImageLoadFailed")} {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task LoadSavedAvatarAsync(string path)
    {
        try
        {
            await LoadSourceAsync(await File.ReadAllBytesAsync(path));
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to load the saved avatar.", ex);
        }
    }

    private async Task LoadSourceAsync(byte[] bytes)
    {
        double w, h;
        using (var stream = new InMemoryRandomAccessStream())
        {
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            w = decoder.OrientedPixelWidth;
            h = decoder.OrientedPixelHeight;
        }

        if (w <= 0 || h <= 0)
        {
            ShowInfo(Loc.T("Personalization_ImageLoadFailed"), InfoBarSeverity.Error);
            return;
        }

        _srcW = w;
        _srcH = h;
        _sourceBytes = bytes;

        // Scale the whole image proportionally to fit inside the crop stage (zoom 1 = fit).
        _fitScale = Math.Min(StageW / _srcW, StageH / _srcH);
        _dispW = _srcW * _fitScale;
        _dispH = _srcH * _fitScale;

        // Decode the display bitmap at 2x the crop stage size (sharp at fit; when zoomed in RenderTransform scales it, slightly soft, but preview/export stay sharp).
        // Do not dispose displayBitmap: SoftwareBitmapSource holds a reference to it internally, and disposing early makes rendering hit a released bitmap,
        // which crashes natively (0xc000027b) during fast navigation. Let the source hold it, it gets GC'd once the old source is replaced.
        var displayBitmap = await DecodeScaledAsync(bytes, StageW * 2, StageH * 2);
        var displaySource = new SoftwareBitmapSource();
        await displaySource.SetBitmapAsync(displayBitmap);

        CropImage.Width = _dispW;
        CropImage.Height = _dispH;
        CropImage.Source = displaySource;

        // Initial state: fit, centered.
        _zoom = 1;
        _panX = (StageW - _dispW) / 2;
        _panY = (StageH - _dispH) / 2;
        ApplyImageTransform();

        // Initial selection: centered on the crop stage, the largest square that fits in the image. The selection always stays centered, framing is done by panning/zooming the background image.
        CenterBox(Math.Min(_dispW, _dispH));

        CropArea.Visibility = Visibility.Visible;
        CropPlaceholder.Visibility = Visibility.Collapsed;

        BuildOverlay();
        UpdateOverlay();
        RenderPreviews();

        SaveAvatarButton.IsEnabled = true;
    }

    // Decode the whole image proportionally to fit within maxW×maxH (no upscaling) and return a BGRA8 SoftwareBitmap.
    private static async Task<SoftwareBitmap> DecodeScaledAsync(byte[] bytes, double maxW, double maxH)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(bytes.AsBuffer());
        input.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(input);
        double w = decoder.OrientedPixelWidth;
        double h = decoder.OrientedPixelHeight;

        var scale = Math.Min(Math.Min(maxW / w, maxH / h), 1.0);
        var sw = Math.Max(1u, (uint)Math.Round(w * scale));
        var sh = Math.Max(1u, (uint)Math.Round(h * scale));

        var transform = new BitmapTransform
        {
            ScaledWidth = sw,
            ScaledHeight = sh,
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return SoftwareBitmap.CreateCopyFromBuffer(
            pixels.DetachPixelData().AsBuffer(),
            BitmapPixelFormat.Bgra8,
            (int)sw, (int)sh,
            BitmapAlphaMode.Premultiplied);
    }

    private void ApplyImageTransform()
    {
        CropImageTransform.ScaleX = _zoom;
        CropImageTransform.ScaleY = _zoom;
        CropImageTransform.TranslateX = _panX;
        CropImageTransform.TranslateY = _panY;
    }

    // Constrain panning so the background image always covers the selection (keeps the crop area inside the image).
    private void ConstrainPan()
    {
        var iw = _dispW * _zoom;
        var ih = _dispH * _zoom;
        _panX = ClampSafe(_panX, _selX + _selSize - iw, _selX);
        _panY = ClampSafe(_panY, _selY + _selSize - ih, _selY);
    }

    // The selection always stays centered on the crop stage, only its size changes.
    private void CenterBox(double size)
    {
        _selSize = size;
        _selX = (StageW - size) / 2;
        _selY = (StageH - size) / 2;
    }

    // Clamp that tolerates floating point error: when max is slightly below min due to rounding, return min instead of throwing.
    private static double ClampSafe(double value, double min, double max)
        => max <= min ? min : Math.Clamp(value, min, max);

    // ---- Selection overlay ----

    private void BuildOverlay()
    {
        if (_overlayBuilt)
        {
            return;
        }

        _dim = new ShapesPath
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
            IsHitTestVisible = false
        };
        CropOverlay.Children.Add(_dim);

        for (var i = 0; i < 4; i++)
        {
            _grid[i] = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            CropOverlay.Children.Add(_grid[i]);
        }

        _selBorder = new Rectangle
        {
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
        CropOverlay.Children.Add(_selBorder);

        for (var i = 0; i < 8; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x33, 0x33)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            CropOverlay.Children.Add(_handles[i]);
        }

        _overlayBuilt = true;
    }

    private void UpdateOverlay()
    {
        if (!_overlayBuilt || _dim is null || _selBorder is null)
        {
            return;
        }

        // Darkening mask: outer frame + selection as two rectangles, EvenOdd cuts out the selection.
        _dim.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children =
            {
                new RectangleGeometry { Rect = new Rect(0, 0, StageW, StageH) },
                new RectangleGeometry { Rect = new Rect(_selX, _selY, _selSize, _selSize) }
            }
        };

        Canvas.SetLeft(_selBorder, _selX);
        Canvas.SetTop(_selBorder, _selY);
        _selBorder.Width = _selSize;
        _selBorder.Height = _selSize;

        // Rule-of-thirds lines
        var t3 = _selSize / 3;
        SetLine(_grid[0], _selX + t3, _selY, _selX + t3, _selY + _selSize);
        SetLine(_grid[1], _selX + 2 * t3, _selY, _selX + 2 * t3, _selY + _selSize);
        SetLine(_grid[2], _selX, _selY + t3, _selX + _selSize, _selY + t3);
        SetLine(_grid[3], _selX, _selY + 2 * t3, _selX + _selSize, _selY + 2 * t3);

        // 8 handles (centered on each point)
        var cx = _selX + _selSize / 2;
        var cy = _selY + _selSize / 2;
        var r = _selX + _selSize;
        var b = _selY + _selSize;
        PlaceHandle(0, _selX, _selY);  // TL
        PlaceHandle(1, cx, _selY);     // T
        PlaceHandle(2, r, _selY);      // TR
        PlaceHandle(3, r, cy);         // R
        PlaceHandle(4, r, b);          // BR
        PlaceHandle(5, cx, b);         // B
        PlaceHandle(6, _selX, b);      // BL
        PlaceHandle(7, _selX, cy);     // L
    }

    private static void SetLine(Line line, double x1, double y1, double x2, double y2)
    {
        line.X1 = x1;
        line.Y1 = y1;
        line.X2 = x2;
        line.Y2 = y2;
    }

    private void PlaceHandle(int i, double x, double y)
    {
        Canvas.SetLeft(_handles[i], x - HandleSize / 2);
        Canvas.SetTop(_handles[i], y - HandleSize / 2);
    }

    // ---- Selection drag / resize / background pan ----

    private void CropOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_sourceBytes is null)
        {
            return;
        }

        var pos = e.GetCurrentPoint(CropOverlay).Position;
        _dragMode = HitTest(pos);
        if (_dragMode == DragMode.None)
        {
            return;
        }

        _dragStartPointer = pos;
        _startPanX = _panX;
        _startPanY = _panY;
        CropOverlay.CapturePointer(e.Pointer);
    }

    private void CropOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_sourceBytes is null)
        {
            return;
        }

        var pos = e.GetCurrentPoint(CropOverlay).Position;

        if (_dragMode == DragMode.None)
        {
            UpdateHoverCursor(HitTest(pos));
            return;
        }

        if (_dragMode == DragMode.Pan)
        {
            _panX = _startPanX + (pos.X - _dragStartPointer.X);
            _panY = _startPanY + (pos.Y - _dragStartPointer.Y);
            ConstrainPan();
            ApplyImageTransform();
        }
        else
        {
            ApplyDrag(_dragMode, pos);
            ConstrainPan();          // A larger selection may need the background image moved to keep it covered
            ApplyImageTransform();
            UpdateOverlay();
        }

        RenderPreviews();
    }

    private void CropOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragMode = DragMode.None;
        CropOverlay.ReleasePointerCapture(e.Pointer);
    }

    // Mouse wheel zooms the background image (selection size and position stay the same): scroll up zooms in, scroll down zooms out, anchored at the cursor.
    private void CropOverlay_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_sourceBytes is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(CropOverlay);
        var cx = point.Position.X;
        var cy = point.Position.Y;

        var factor = point.Properties.MouseWheelDelta > 0 ? 1.1 : 1.0 / 1.1;
        var newZoom = ClampSafe(_zoom * factor, 1, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 1e-6)
        {
            e.Handled = true;
            return;
        }

        // Zoom around the cursor: the image point under the cursor stays under the cursor.
        _panX = cx - (cx - _panX) * (newZoom / _zoom);
        _panY = cy - (cy - _panY) * (newZoom / _zoom);
        _zoom = newZoom;
        ConstrainPan();
        ApplyImageTransform();
        RenderPreviews();
        e.Handled = true;   // Stop the wheel event bubbling up to the page ScrollViewer
    }

    private DragMode HitTest(Point p)
    {
        var cx = _selX + _selSize / 2;
        var cy = _selY + _selSize / 2;
        var r = _selX + _selSize;
        var b = _selY + _selSize;

        // Hitting a handle = resize the selection; anything else (inside or outside the box) pans the background image.
        if (Near(p, _selX, _selY)) return DragMode.TL;
        if (Near(p, r, _selY)) return DragMode.TR;
        if (Near(p, r, b)) return DragMode.BR;
        if (Near(p, _selX, b)) return DragMode.BL;
        if (Near(p, cx, _selY)) return DragMode.T;
        if (Near(p, r, cy)) return DragMode.R;
        if (Near(p, cx, b)) return DragMode.B;
        if (Near(p, _selX, cy)) return DragMode.L;
        return DragMode.Pan;
    }

    private static bool Near(Point p, double x, double y)
        => Math.Abs(p.X - x) <= HandleHit && Math.Abs(p.Y - y) <= HandleHit;

    private void UpdateHoverCursor(DragMode mode)
    {
        var shape = mode switch
        {
            DragMode.TL or DragMode.BR => InputSystemCursorShape.SizeNorthwestSoutheast,
            DragMode.TR or DragMode.BL => InputSystemCursorShape.SizeNortheastSouthwest,
            DragMode.T or DragMode.B => InputSystemCursorShape.SizeNorthSouth,
            DragMode.L or DragMode.R => InputSystemCursorShape.SizeWestEast,
            DragMode.Pan => InputSystemCursorShape.SizeAll,
            _ => InputSystemCursorShape.Arrow
        };
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    // Corner/edge resize of the selection (always centered, stays square): the distance from the crop stage center to the cursor sets the side length.
    // The upper limit is the largest square at "fit", min(dispW,dispH), so at any zoom the selection is ≤ the background image and can always be covered.
    private void ApplyDrag(DragMode mode, Point pos)
    {
        var cx0 = StageW / 2;
        var cy0 = StageH / 2;
        var maxSel = Math.Min(_dispW, _dispH);
        var minSel = ClampSafe(MinSourcePx * _zoom * _fitScale, MinSelFloor, maxSel);

        var half = mode switch
        {
            DragMode.TL or DragMode.TR or DragMode.BR or DragMode.BL
                => Math.Max(Math.Abs(pos.X - cx0), Math.Abs(pos.Y - cy0)),
            DragMode.T or DragMode.B => Math.Abs(pos.Y - cy0),
            DragMode.L or DragMode.R => Math.Abs(pos.X - cx0),
            _ => _selSize / 2
        };

        CenterBox(ClampSafe(2 * half, minSel, maxSel));
    }

    // Selection (crop stage coordinates) → source image pixels. After the background image is zoomed/panned, source coords = (selection - pan) / (zoom × fitScale).
    private (double X, double Y, double Size) SelectionInSource()
    {
        var s = _zoom * _fitScale;
        return ((_selX - _panX) / s, (_selY - _panY) / s, _selSize / s);
    }

    // ---- Preview (184 / 64 / 32)----

    // Crop the source image to a 184² bitmap by the current selection, shared by all three preview sizes (smaller sizes are scaled down by Image).
    // Async, latest wins: while dragging, decode speed throttles it naturally, and it always renders the latest framing.
    private async void RenderPreviews()
    {
        if (_sourceBytes is null)
        {
            return;
        }

        if (_previewRendering)
        {
            _previewPending = true;
            return;
        }

        _previewRendering = true;
        try
        {
            do
            {
                _previewPending = false;
                var bytes = _sourceBytes;
                if (bytes is null)
                {
                    break;
                }

                var (cropX, cropY, cropSize) = SelectionInSource();

                // Use BitmapImage (encoded to an in-memory PNG, then decoded) instead of SoftwareBitmapSource: the latter cannot be attached to several
                // Image controls at once, and its SoftwareBitmap cannot be disposed after set, otherwise fast navigation crashes natively (0xc000027b).
                // BitmapImage owns its data, can be shared by several Image controls, and stays stable during navigation.
                var image = await CropToBitmapImageAsync(bytes, cropX, cropY, cropSize, PreviewPx);
                Preview184.Source = image;
                Preview64.Source = image;
                Preview32.Source = image;
            }
            while (_previewPending);
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to render the avatar preview.", ex);
        }
        finally
        {
            _previewRendering = false;
        }
    }

    // ---- Export avatar ----

    private async void SaveAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceBytes is null)
        {
            return;
        }

        try
        {
            var (cropX, cropY, cropSize) = SelectionInSource();
            var outputPath = AppState.SettingsService.PersonalizationAvatarPath;
            await CropAndSaveJpegAsync(_sourceBytes, cropX, cropY, cropSize, outputPath);

            ShowInfo(Loc.T("Personalization_AvatarSaved"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to save the personalization avatar.", ex);
            ShowInfo(Loc.T("Personalization_AvatarSaveFailed"), InfoBarSeverity.Error);
        }
    }

    // Source image → selection area → outSize² BGRA8(Premultiplied) pixels. Preview and export share the same crop geometry, so what you see is what you get.
    private static async Task<byte[]> CropToBgraPixelsAsync(
        byte[] sourceBytes, double cropX, double cropY, double cropSize, uint outSize)
    {
        using var inputStream = new InMemoryRandomAccessStream();
        await inputStream.WriteAsync(sourceBytes.AsBuffer());
        inputStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(inputStream);
        double srcW = decoder.OrientedPixelWidth;
        double srcH = decoder.OrientedPixelHeight;

        cropSize = Math.Min(cropSize, Math.Min(srcW, srcH));
        cropX = ClampSafe(cropX, 0, srcW - cropSize);
        cropY = ClampSafe(cropY, 0, srcH - cropSize);

        // BitmapTransform scales first, then crops by Bounds: scale the whole image by f so the crop area lands exactly on outSize².
        var f = outSize / cropSize;
        var scaledW = (uint)Math.Round(srcW * f);
        var scaledH = (uint)Math.Round(srcH * f);
        var boundsX = (uint)Math.Round(cropX * f);
        var boundsY = (uint)Math.Round(cropY * f);
        if (boundsX + outSize > scaledW)
        {
            boundsX = scaledW - outSize;
        }

        if (boundsY + outSize > scaledH)
        {
            boundsY = scaledH - outSize;
        }

        var transform = new BitmapTransform
        {
            ScaledWidth = scaledW,
            ScaledHeight = scaledH,
            Bounds = new BitmapBounds { X = boundsX, Y = boundsY, Width = outSize, Height = outSize },
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return pixels.DetachPixelData();
    }

    // Source image → selection area → outSize² BitmapImage (via in-memory PNG encode/decode). BitmapImage owns its pixels, can be shared by several
    // Image controls and stays stable during navigation, avoiding the SoftwareBitmapSource crash trap of "cannot be shared / cannot dispose after set".
    private static async Task<BitmapImage> CropToBitmapImageAsync(
        byte[] sourceBytes, double cropX, double cropY, double cropSize, uint outSize)
    {
        var pixels = await CropToBgraPixelsAsync(sourceBytes, cropX, cropY, cropSize, outSize);

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            outSize, outSize,
            96, 96,
            pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }

    private static async Task CropAndSaveJpegAsync(
        byte[] sourceBytes, double cropX, double cropY, double cropSize, string outputPath)
    {
        var pixels = await CropToBgraPixelsAsync(sourceBytes, cropX, cropY, cropSize, OutputSize);

        using var outputStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            OutputSize, OutputSize,
            96, 96,
            pixels);
        await encoder.FlushAsync();

        outputStream.Seek(0);
        var encodedBytes = new byte[outputStream.Size];
        await outputStream.ReadAsync(encodedBytes.AsBuffer(), (uint)outputStream.Size, InputStreamOptions.None);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);

        // Atomic write: write a temp file first, then replace it in one step, so readers never hit a half-written JPEG.
        var tempPath = outputPath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, encodedBytes);
        File.Move(tempPath, outputPath, overwrite: true);
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        PageInfoBar.Message = message;
        PageInfoBar.Severity = severity;
        PageInfoBar.IsOpen = true;
    }

    // ---- Win32 file dialog (replaces WinRT FileOpenPicker, which is unreliable when elevated / unpackaged)----

    [LibraryImport("comdlg32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOpenFileNameW(ref OpenFileName ofn);

    private static unsafe string? PickImageFileWin32(nint owner)
    {
        const int ofnExplorer = 0x00080000;
        const int ofnFileMustExist = 0x00001000;
        const int ofnPathMustExist = 0x00000800;
        const int ofnNoChangeDir = 0x00000008;
        const int maxFile = 4096;

        // Filter format: "label\0pattern\0\0" (double null terminated).
        var filter = $"{Loc.T("Personalization_Avatar_FileType")}\0*.jpg;*.jpeg;*.png;*.bmp;*.webp\0\0";
        var title = Loc.T("Personalization_Btn_PickImage");
        var fileBuffer = new char[maxFile];

        fixed (char* filterPtr = filter)
        fixed (char* titlePtr = title)
        fixed (char* filePtr = fileBuffer)
        {
            var ofn = new OpenFileName
            {
                lStructSize = sizeof(OpenFileName),
                hwndOwner = owner,
                lpstrFilter = (nint)filterPtr,
                nFilterIndex = 1,
                lpstrFile = (nint)filePtr,
                nMaxFile = maxFile,
                lpstrTitle = (nint)titlePtr,
                Flags = ofnExplorer | ofnFileMustExist | ofnPathMustExist | ofnNoChangeDir
            };

            return GetOpenFileNameW(ref ofn) ? new string(filePtr) : null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public nint lpstrFilter;
        public nint lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public nint lpstrFile;
        public int nMaxFile;
        public nint lpstrFileTitle;
        public int nMaxFileTitle;
        public nint lpstrInitialDir;
        public nint lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public nint lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public nint lpTemplateName;
        public nint pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }
}
