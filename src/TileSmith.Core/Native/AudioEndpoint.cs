using System.Runtime.InteropServices;
using TileSmith.Core.Actions;

namespace TileSmith.Core.Native;

/// <summary>
/// Default speaker / microphone volume through the Core Audio API (IAudioEndpointVolume).
/// Calls the COM vtables directly, so it works without .NET's built-in COM interop (trimming/AOT safe).
/// </summary>
public static unsafe class AudioEndpoint
{
    private const int ERender = 0;
    private const int ECapture = 1;
    private const int EMultimedia = 1;
    private const int ClsctxAll = 23;
    private const int ClsctxInprocServer = 1;

    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidImmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    public static int GetVolume() => Use(ERender, v =>
    {
        float level;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, float*, int>)Slot(v, 9))(v, &level));
        return (int)Math.Round(level * 100);
    });

    public static void SetVolume(int percent)
    {
        var level = Math.Clamp(percent, 0, 100) / 100f;
        Use(ERender, v =>
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, float, Guid*, int>)Slot(v, 7))(v, level, null));
            return 0;
        });
    }

    public static bool GetMute(bool microphone = false) => Use(microphone ? ECapture : ERender, v =>
    {
        int muted;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(v, 15))(v, &muted));
        return muted != 0;
    });

    public static void SetMute(bool mute, bool microphone = false) => Use(microphone ? ECapture : ERender, v =>
    {
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, int>)Slot(v, 14))(v, mute ? 1 : 0, null));
        return 0;
    });

    private static T Use<T>(int flow, Func<IntPtr, T> action)
    {
        _ = CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED; harmless if already initialized
        IntPtr enumerator = 0, device = 0, volume = 0;
        try
        {
            var clsid = ClsidMmDeviceEnumerator;
            var iid = IidImmDeviceEnumerator;
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out enumerator));

            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)Slot(enumerator, 4))(enumerator, flow, EMultimedia, &device);
            if (hr != 0 || device == 0)
                throw new ActionException(flow == ECapture ? "No microphone is available." : "No speakers or headphones are available.");

            var volumeIid = IidAudioEndpointVolume;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, IntPtr, IntPtr*, int>)Slot(device, 3))(device, &volumeIid, ClsctxAll, IntPtr.Zero, &volume));
            return action(volume);
        }
        finally
        {
            Release(volume);
            Release(device);
            Release(enumerator);
        }
    }

    private static IntPtr Slot(IntPtr comObject, int index) => (*(IntPtr**)comObject)[index];

    private static void Release(IntPtr comObject)
    {
        if (comObject != 0) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(comObject, 2))(comObject);
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsContext, ref Guid iid, out IntPtr instance);
}
