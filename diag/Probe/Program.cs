using System.Drawing;

// 用法: PixelProbe <png路径> <x1> <x2> <yStart> —— 在 [x1,x2] 水平带内,
// 从 yStart 向下找第一行"深色像素"(RGB 均值 < 100), 打印 y
using var bmp = new Bitmap(args[0]);
int x1 = int.Parse(args[1]), x2 = int.Parse(args[2]), y0 = int.Parse(args[3]);
for (int y = y0; y < bmp.Height; y++)
{
    for (int x = x1; x <= x2; x++)
    {
        var p = bmp.GetPixel(x, y);
        if ((p.R + p.G + p.B) / 3 < 100)
        {
            Console.WriteLine($"{Path.GetFileName(args[0])}: first dark pixel at y={y}");
            return;
        }
    }
}
Console.WriteLine("no dark pixel found");
