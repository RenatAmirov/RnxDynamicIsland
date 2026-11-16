using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DynamicIsland;

internal sealed class FrameSurface : IDisposable
{
    private readonly IntPtr dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
    private IntPtr dib, original;
    private Bitmap? bitmap;

    public Bitmap? Resize(Size size)
    {
        if (bitmap?.Size == size)
            return bitmap;

        var info = new NativeMethods.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
            Width = size.Width,
            Height = -size.Height,
            Planes = 1,
            BitCount = 32
        };
        IntPtr next = NativeMethods.CreateDIBSection(dc, ref info, 0, out IntPtr pixels, IntPtr.Zero, 0);
        if (next == IntPtr.Zero)
            return null;

        var nextBitmap = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, pixels);
        IntPtr previous = NativeMethods.SelectObject(dc, next);
        if (previous == IntPtr.Zero || previous == new IntPtr(-1))
        {
            nextBitmap.Dispose();
            NativeMethods.DeleteObject(next);
            return null;
        }

        bitmap?.Dispose();
        if (dib != IntPtr.Zero)
            NativeMethods.DeleteObject(dib);
        else
            original = previous;
        bitmap = nextBitmap;
        dib = next;
        return bitmap;
    }

    public void Present(IntPtr window, Rectangle bounds)
    {
        var position = new NativeMethods.NativePoint(bounds.X, bounds.Y);
        var size = new NativeMethods.NativePoint(bounds.Width, bounds.Height);
        var origin = new NativeMethods.NativePoint();
        var blend = new NativeMethods.BlendFunction { Opacity = 255, AlphaFormat = 1 };
        NativeMethods.UpdateLayeredWindow(window, IntPtr.Zero, ref position, ref size, dc, ref origin, 0, ref blend, 2);
    }

    public void Dispose()
    {
        bitmap?.Dispose();
        if (dib != IntPtr.Zero)
        {
            NativeMethods.SelectObject(dc, original);
            NativeMethods.DeleteObject(dib);
        }
        NativeMethods.DeleteDC(dc);
    }
}

internal sealed class FrameClock : IDisposable
{
    private readonly ManualResetEvent stop = new(false);
    private readonly Thread thread;
    private readonly IntPtr window;
    private int refreshRate = 60;
    private int pending;

    public FrameClock(IntPtr window, int refreshRate)
    {
        this.window = window;
        this.refreshRate = refreshRate;
        thread = new Thread(Run) { IsBackground = true, Name = "Island frame clock" };
        thread.Start();
    }

    public int RefreshRate { set => Volatile.Write(ref refreshRate, value); }
    public void AcknowledgeFrame() => Interlocked.Exchange(ref pending, 0);

    private void Run()
    {
        const uint access = 0x100002; // SYNCHRONIZE | TIMER_MODIFY_STATE
        var handle = NativeMethods.CreateWaitableTimer(IntPtr.Zero, IntPtr.Zero, 2, access);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = NativeMethods.CreateWaitableTimer(IntPtr.Zero, IntPtr.Zero, 0, access);
        }

        using (handle)
        using (var timer = new EventWaitHandle(false, EventResetMode.AutoReset))
        {
            if (!handle.IsInvalid)
            {
                timer.SafeWaitHandle.Dispose();
                timer.SafeWaitHandle = handle;
            }
            WaitHandle[] waits = [stop, timer];
            long next = Stopwatch.GetTimestamp();
            while (!stop.WaitOne(0))
            {
                if (Interlocked.Exchange(ref pending, 1) == 0 &&
                    !NativeMethods.PostMessage(window, NativeMethods.FrameMessage, IntPtr.Zero, IntPtr.Zero))
                    AcknowledgeFrame();

                long interval = Stopwatch.Frequency / Volatile.Read(ref refreshRate);
                long now = Stopwatch.GetTimestamp();
                next += interval;
                if (next <= now)
                    next = now + interval;

                double seconds = (next - now) / (double)Stopwatch.Frequency;
                long due = -Math.Max(1, (long)Math.Ceiling(seconds * TimeSpan.TicksPerSecond));
                if (!handle.IsInvalid && NativeMethods.SetWaitableTimer(handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    if (WaitHandle.WaitAny(waits) == 0)
                        return;
                }
                else if (stop.WaitOne((int)Math.Ceiling(seconds * 1000)))
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        stop.Set();
        thread.Join();
        stop.Dispose();
    }
}
