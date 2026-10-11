using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using TileSmith.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TileSmith.Services;

/// <summary>
/// Development aid: <c>TileSmith.exe --capture &lt;folder&gt;</c> renders the panel (in several states) and every
/// settings page to PNG files, then quits. Used to review the UI without clicking through it by hand.
/// </summary>
public static class DevCapture
{
    public sealed record Shot(string Name, Func<Task<UIElement?>> Prepare);

    public static async Task RunAsync(string folder, IEnumerable<Shot> shots, Action done)
    {
        Directory.CreateDirectory(folder);
        foreach (var shot in shots)
        {
            try
            {
                var element = await shot.Prepare();
                if (element is null) continue;
                await Task.Delay(900); // entrance animations and state queries settle
                await SaveAsync(element, Path.Combine(folder, shot.Name + ".png"));
                Log.Info($"Captured {shot.Name}");
            }
            catch (Exception ex)
            {
                Log.Error($"Capture of {shot.Name} failed", ex);
            }
        }
        done();
    }

    public static async Task SaveAsync(UIElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, bytes);
    }
}
