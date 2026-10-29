using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

if (args.Length < 2)
{
    Console.WriteLine("usage: Shot <savePath> <windowTitleSubstring>");
    return;
}

var title = args[1];
IntPtr hwnd = IntPtr.Zero;
EnumWindows((h, _) =>
{
    var sb = new System.Text.StringBuilder(256);
    GetWindowText(h, sb, 256);
    if (h != GetShellWindow() && sb.ToString().Contains(title, StringComparison.OrdinalIgnoreCase))
    {
        hwnd = h;
        return false;
    }
    return true;
}, IntPtr.Zero);

if (hwnd == IntPtr.Zero)
{
    Console.WriteLine("window not found: " + title);
    return;
}

ShowWindow(hwnd, 9);           // SW_RESTORE
SetForegroundWindow(hwnd);
Thread.Sleep(1200);

GetWindowRect(hwnd, out var r);
using var bmp = new Bitmap(r.Width, r.Height);
using var g = Graphics.FromImage(bmp);
g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
bmp.Save(args[0]);
Console.WriteLine($"saved {args[0]} rect={r.Left},{r.Top} {r.Width}x{r.Height}");

[DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
[DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] static extern IntPtr GetShellWindow();
[DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
