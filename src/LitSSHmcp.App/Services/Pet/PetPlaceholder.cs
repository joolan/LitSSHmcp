using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LitSSHmcp.App.Services.Pet;

public enum PetAction
{
    Idle,
    Walk,
    Drag,
    Click,
    Sleep,
    Stretch,
    Look,
    Work
}

public enum PetFacing
{
    Right,
    Left
}

public sealed class PetFrameSet
{
    private readonly Dictionary<PetAction, IReadOnlyList<BitmapSource>> _frames;
    private readonly Dictionary<PetAction, IReadOnlyList<int>> _delays;

    public PetFrameSet(Dictionary<PetAction, IReadOnlyList<BitmapSource>> frames, int fps = 10,
        Dictionary<PetAction, IReadOnlyList<int>>? delays = null, PetFacing facing = PetFacing.Right)
    {
        _frames = frames;
        _delays = delays ?? new();
        Fps = Math.Clamp(fps, 1, 24);
        Facing = facing;
    }

    public int Fps { get; }

    /// <summary>素材原始朝向；运行时据此决定是否水平翻转。</summary>
    public PetFacing Facing { get; }

    public long EstimatedBytes
    {
        get
        {
            var unique = new HashSet<BitmapSource>();
            foreach (var frames in _frames.Values)
            {
                foreach (var frame in frames)
                    unique.Add(frame);
            }
            long bytes = 0;
            foreach (var frame in unique)
                bytes += (long)frame.PixelWidth * frame.PixelHeight * 4;
            return bytes;
        }
    }

    public IReadOnlyList<BitmapSource> this[PetAction action] =>
        _frames.TryGetValue(action, out var frames) ? frames : _frames[PetAction.Idle];

    public int GetDelay(PetAction action, int frameIndex)
    {
        if (_delays.TryGetValue(action, out var delays) &&
            frameIndex >= 0 && frameIndex < delays.Count)
            return Math.Max(10, delays[frameIndex]);
        return Math.Max(10, (int)(1000.0 / Math.Max(1, Fps)));
    }
}

/// <summary>第一版程序内占位宠物：用 WPF 矢量绘制生成序列帧。</summary>
public static class PetPlaceholder
{
    public const int Size = 160;

    public static PetFrameSet Create()
    {
        return new PetFrameSet(new Dictionary<PetAction, IReadOnlyList<BitmapSource>>
        {
            [PetAction.Idle] = Enumerable.Range(0, 8).Select(i => Render(PetAction.Idle, i, 8)).ToArray(),
            [PetAction.Walk] = Enumerable.Range(0, 8).Select(i => Render(PetAction.Walk, i, 8)).ToArray(),
            [PetAction.Drag] = Enumerable.Range(0, 4).Select(i => Render(PetAction.Drag, i, 4)).ToArray(),
            [PetAction.Click] = Enumerable.Range(0, 4).Select(i => Render(PetAction.Click, i, 4)).ToArray(),
            [PetAction.Sleep] = Enumerable.Range(0, 4).Select(i => Render(PetAction.Sleep, i, 4)).ToArray(),
            [PetAction.Stretch] = Enumerable.Range(0, 6).Select(i => Render(PetAction.Stretch, i, 6)).ToArray(),
            [PetAction.Look] = Enumerable.Range(0, 6).Select(i => Render(PetAction.Look, i, 6)).ToArray(),
            [PetAction.Work] = Enumerable.Range(0, 6).Select(i => Render(PetAction.Work, i, 6)).ToArray()
        });
    }

    private static BitmapSource Render(PetAction action, int frame, int count)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var phase = count <= 0 ? 0 : frame / (double)count;
            var bob = action switch
            {
                PetAction.Walk => Math.Sin(phase * Math.PI * 2) * 4,
                PetAction.Idle => Math.Sin(phase * Math.PI * 2) * 2,
                PetAction.Drag => Math.Sin(phase * Math.PI * 4) * 2 - 5,
                PetAction.Click => Math.Abs(Math.Sin(phase * Math.PI)) * 6,
                PetAction.Sleep => -2 + Math.Sin(phase * Math.PI * 2),
                PetAction.Stretch => Math.Sin(phase * Math.PI) * 3,
                PetAction.Work => Math.Sin(phase * Math.PI * 4) * 1.5,
                _ => 0
            };
            var sway = action == PetAction.Walk ? Math.Sin(phase * Math.PI * 2) * 3 : 0;
            var squash = action switch
            {
                PetAction.Click => 1 + Math.Abs(Math.Sin(phase * Math.PI)) * 0.08,
                PetAction.Stretch => 1 - Math.Sin(phase * Math.PI) * 0.08,
                _ => 1.0
            };
            var stretch = action switch
            {
                PetAction.Drag => 1.06,
                PetAction.Stretch => 1 + Math.Sin(phase * Math.PI) * 0.08,
                PetAction.Work => 1 + Math.Sin(phase * Math.PI * 4) * 0.015,
                _ => 1.0
            };

            dc.PushTransform(new TranslateTransform(sway, bob));
            DrawPet(dc, action, frame, squash, stretch);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawPet(DrawingContext dc, PetAction action, int frame,
        double squash, double stretch)
    {
        var bodyBrush = new LinearGradientBrush(
            Color.FromRgb(121, 214, 255),
            Color.FromRgb(70, 145, 255),
            new Point(0, 0), new Point(0, 1));
        bodyBrush.Freeze();
        var bodyPen = new Pen(new SolidColorBrush(Color.FromRgb(36, 102, 190)), 2.5);
        bodyPen.Freeze();
        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(28, 61, 100)), 3.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        linePen.Freeze();

        var cx = 80.0;
        var bodyY = 96.0;
        var bodyW = 80.0 * squash;
        var bodyH = 66.0 * stretch;
        var breathe = action == PetAction.Idle ? 2 : 0;

        // 耳朵
        var earBrush = new SolidColorBrush(Color.FromRgb(91, 176, 244));
        earBrush.Freeze();
        var earPen = new Pen(new SolidColorBrush(Color.FromRgb(36, 102, 190)), 2);
        earPen.Freeze();
        var leftEar = new StreamGeometry();
        using (var ctx = leftEar.Open())
        {
            ctx.BeginFigure(new Point(cx - 39, bodyY - 22), true, true);
            ctx.LineTo(new Point(cx - 50, bodyY - 55), true, false);
            ctx.LineTo(new Point(cx - 15, bodyY - 34), true, false);
        }
        var rightEar = new StreamGeometry();
        using (var ctx = rightEar.Open())
        {
            ctx.BeginFigure(new Point(cx + 39, bodyY - 22), true, true);
            ctx.LineTo(new Point(cx + 50, bodyY - 55), true, false);
            ctx.LineTo(new Point(cx + 15, bodyY - 34), true, false);
        }
        dc.DrawGeometry(earBrush, earPen, leftEar);
        dc.DrawGeometry(earBrush, earPen, rightEar);

        // 尾巴
        var tail = new StreamGeometry();
        using (var ctx = tail.Open())
        {
            ctx.BeginFigure(new Point(cx + 34, bodyY + 22), false, false);
            ctx.BezierTo(new Point(cx + 68, bodyY + 10),
                new Point(cx + 66, bodyY - 16),
                new Point(cx + 49, bodyY - 10), true, false);
        }
        dc.DrawGeometry(null, linePen, tail);

        // 身体
        dc.DrawEllipse(bodyBrush, bodyPen,
            new Point(cx, bodyY), (bodyW / 2) + breathe, (bodyH / 2) + breathe);

        // 脚
        var footBrush = new SolidColorBrush(Color.FromRgb(235, 248, 255));
        footBrush.Freeze();
        var step = action == PetAction.Walk
            ? Math.Sin((frame / 8.0) * Math.PI * 2) * 5
            : 0;
        dc.DrawEllipse(footBrush, bodyPen, new Point(cx - 22 + step, bodyY + 35), 15, 9);
        dc.DrawEllipse(footBrush, bodyPen, new Point(cx + 22 - step, bodyY + 35), 15, 9);

        if (action == PetAction.Work)
        {
            var screenBrush = new SolidColorBrush(Color.FromRgb(23, 42, 66));
            screenBrush.Freeze();
            var screenPen = new Pen(new SolidColorBrush(Color.FromRgb(125, 211, 252)), 1.6);
            screenPen.Freeze();
            var screen = new Rect(cx - 27, bodyY + 15, 54, 22);
            dc.DrawRoundedRectangle(screenBrush, screenPen, screen, 4, 4);
            var caret = frame % 2 == 0 ? 0 : 3;
            dc.DrawLine(screenPen, new Point(cx - 20, bodyY + 21),
                new Point(cx - 7 + caret, bodyY + 21));
            dc.DrawLine(screenPen, new Point(cx - 20, bodyY + 27),
                new Point(cx + 2 + caret, bodyY + 27));
            dc.DrawLine(screenPen, new Point(cx - 20, bodyY + 33),
                new Point(cx - 2, bodyY + 33));
        }

        // 眼睛
        var blink = action == PetAction.Sleep || (action == PetAction.Idle && frame == 6);
        var eyeShift = action switch
        {
            PetAction.Look => frame < 3 ? 4 : -4,
            PetAction.Work => frame < 3 ? 1 : -1,
            _ => 0
        };
        var eyeBrush = new SolidColorBrush(Color.FromRgb(25, 51, 84));
        eyeBrush.Freeze();
        if (blink)
        {
            dc.DrawLine(linePen, new Point(cx - 22, bodyY - 12), new Point(cx - 8, bodyY - 12));
            dc.DrawLine(linePen, new Point(cx + 8, bodyY - 12), new Point(cx + 22, bodyY - 12));
        }
        else
        {
            dc.DrawEllipse(eyeBrush, null, new Point(cx - 15 + eyeShift, bodyY - 13), 5, 7);
            dc.DrawEllipse(eyeBrush, null, new Point(cx + 15 + eyeShift, bodyY - 13), 5, 7);
            var shine = new SolidColorBrush(Colors.White);
            shine.Freeze();
            dc.DrawEllipse(shine, null, new Point(cx - 13 + eyeShift, bodyY - 16), 1.8, 1.8);
            dc.DrawEllipse(shine, null, new Point(cx + 17 + eyeShift, bodyY - 16), 1.8, 1.8);
        }

        // 鼻子和嘴
        var nose = new SolidColorBrush(Color.FromRgb(255, 145, 176));
        nose.Freeze();
        dc.DrawEllipse(nose, null, new Point(cx, bodyY + 1), 4, 3);
        var mouth = new StreamGeometry();
        using (var ctx = mouth.Open())
        {
            ctx.BeginFigure(new Point(cx, bodyY + 4), false, false);
            ctx.QuadraticBezierTo(new Point(cx - 5, bodyY + 10),
                new Point(cx - 10, bodyY + 5), true, false);
            ctx.BeginFigure(new Point(cx, bodyY + 4), false, false);
            ctx.QuadraticBezierTo(new Point(cx + 5, bodyY + 10),
                new Point(cx + 10, bodyY + 5), true, false);
        }
        dc.DrawGeometry(null, linePen, mouth);

        // 拖动时添加小汗滴，点击时添加星形反馈
        if (action == PetAction.Drag)
        {
            var drop = new SolidColorBrush(Color.FromRgb(141, 209, 255));
            drop.Freeze();
            dc.DrawEllipse(drop, null, new Point(cx + 52, bodyY - 45 + (frame % 2) * 4), 3, 6);
        }
        else if (action == PetAction.Click)
        {
            var starPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 196, 64)), 2.4);
            starPen.Freeze();
            var offset = frame * 2;
            dc.DrawLine(starPen, new Point(cx - 52 - offset, bodyY - 48), new Point(cx - 42 - offset, bodyY - 38));
            dc.DrawLine(starPen, new Point(cx + 52 + offset, bodyY - 48), new Point(cx + 42 + offset, bodyY - 38));
        }
    }
}
