namespace WarThunderESP;

public sealed class WarThunderOverlay : IDisposable
{
    private readonly GameMemoryReader _reader;
    private readonly GraphicsWindow _window;

    private SolidBrush? _redBrush;
    private SolidBrush? _greenBrush;
    private SolidBrush? _whiteBrush;
    private SolidBrush? _yellowBrush;
    private Font? _font;

    private bool _espEnabled = true;
    private bool _f10WasDown;
    private const int VK_F10 = 0x79;

    public WarThunderOverlay(GameMemoryReader reader)
    {
        _reader = reader;

        var graphics = new Graphics
        {
            MeasureFPS = true,
            PerPrimitiveAntiAliasing = true,
            TextAntiAliasing = true
        };

        _window = new GraphicsWindow(0, 0, GetScreenWidth(), GetScreenHeight(), graphics)
        {
            FPS = 60,
            IsTopmost = true,
            IsVisible = true
        };

        _window.SetupGraphics += Window_SetupGraphics;
        _window.DrawGraphics += Window_DrawGraphics;
        _window.DestroyGraphics += Window_DestroyGraphics;
    }

    public void Start() => _window.Create();

    private static int GetScreenWidth() => GetSystemMetrics(0);

    private static int GetScreenHeight() => GetSystemMetrics(1);

    private static float Clamp(float value, float min, float max)
    {
        if (value < min)
            return min;

        if (value > max)
            return max;

        return value;
    }

    private void Window_SetupGraphics(object? sender, SetupGraphicsEventArgs e)
    {
        _redBrush?.Dispose();
        _greenBrush?.Dispose();
        _whiteBrush?.Dispose();
        _yellowBrush?.Dispose();

        var graphics = e.Graphics;

        _redBrush = graphics.CreateSolidBrush(255, 0, 0, 255);
        _greenBrush = graphics.CreateSolidBrush(0, 255, 0, 255);
        _whiteBrush = graphics.CreateSolidBrush(255, 255, 255, 255);
        _yellowBrush = graphics.CreateSolidBrush(255, 255, 0, 255);

        if (!e.RecreateResources)
        {
            _font?.Dispose();
            _font = graphics.CreateFont("Consolas", 14);
        }
    }

    private void UpdateHotkeys()
    {
        bool f10Down = (GetAsyncKeyState(VK_F10) & 0x8000) != 0;

        if (f10Down && !_f10WasDown)
            _espEnabled = !_espEnabled;

        _f10WasDown = f10Down;
    }

    private void Window_DrawGraphics(object? sender, DrawGraphicsEventArgs e)
    {
        if (_redBrush == null || _greenBrush == null || _whiteBrush == null || _yellowBrush == null || _font == null)
            return;

        var graphics = e.Graphics;
        graphics.ClearScene();
        UpdateHotkeys();

        float width = graphics.Width;
        float height = graphics.Height;

        if (!_espEnabled)
        {
            graphics.DrawText(
                _font,
                _whiteBrush,
                16,
                16,
                "ESP OFF | F10 to enable"
            );
            return;
        }

        float centerX = width / 2f;
        float centerY = height / 2f;

        var projected = _reader.GetProjectedObjects(width, height, width / 2.0f, height / 2.0f);

        float minX = float.MaxValue;
        float maxX = float.MinValue;
        float minY = float.MaxValue;
        float maxY = float.MinValue;

        foreach (var p in projected)
        {
            minX = Math.Min(minX, p.ScreenX);
            maxX = Math.Max(maxX, p.ScreenX);
            minY = Math.Min(minY, p.ScreenY);
            maxY = Math.Max(maxY, p.ScreenY);
        }

        if (projected.Count == 0)
        {
            minX = maxX = minY = maxY = 0;
        }

        int visible = 0;


        foreach (var obj in projected)
        {
            bool onScreen =
                obj.ScreenX >= 0 &&
                obj.ScreenX <= width &&
                obj.ScreenY >= 0 &&
                obj.ScreenY <= height;

            if (!onScreen)
                continue;

            visible++;

            float left = obj.BoxLeft;
            float top = obj.BoxTop;
            float right = obj.BoxRight;
            float bottom = obj.BoxBottom;
            float boxWidth = Math.Max(1.0f, right - left);

            graphics.DrawRectangle(_greenBrush, left, top, right, bottom, 2);

            graphics.DrawText(
                _font,
                _yellowBrush,
                right + 4,
                top,
                $"{obj.UnitName} | {obj.Distance:0}m | UID={_reader.ReadI16(obj.Address, 0xFE8)}"
            );
        }

        graphics.DrawText(
            _font,
            _whiteBrush,
            16,
            16,
            $"WT | F10 ESP ON | FPS {graphics.FPS} | objects {_reader.ObjectCount} | projected {projected.Count} | visible {visible} | X=({minX:0},{maxX:0}) Y=({minY:0},{maxY:0}) | scan {_reader.LastScanMs}ms | vp=VIEW+PROJ or GLOBTM fallback | FE8 combat + alive D1860=0 + FE0!=self({_reader.LastSelfTeam}) + ground/air vtable + air fresh globtm + no-lag strict bad-frame gate + self aircraft skip + names @0FF0 + 3D boxes"
        );
    }

    private void DrawCrosshair(Graphics graphics, float centerX, float centerY)
    {
        if (_redBrush == null)
            return;

        graphics.DrawLine(_redBrush, centerX - 10, centerY, centerX + 10, centerY, 2);
        graphics.DrawLine(_redBrush, centerX, centerY - 10, centerX, centerY + 10, 2);
    }

    private void Window_DestroyGraphics(object? sender, DestroyGraphicsEventArgs e)
    {
        _redBrush?.Dispose();
        _greenBrush?.Dispose();
        _whiteBrush?.Dispose();
        _yellowBrush?.Dispose();
        _font?.Dispose();

        _redBrush = null;
        _greenBrush = null;
        _whiteBrush = null;
        _yellowBrush = null;
        _font = null;
    }

    public void Dispose()
    {
        _window.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}

