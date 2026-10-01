using System.Runtime.InteropServices;

namespace PowerMateBT;

/// <summary>
/// Sends the same virtual keys as a keyboard's media keys, so Windows shows its normal volume
/// pop-up and whichever media player is active responds.
/// </summary>
internal static class MediaKeys
{
    const ushort VK_VOLUME_DOWN = 0xAE;
    const ushort VK_VOLUME_UP = 0xAF;
    const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002;

    public static void VolumeUp() => Press(VK_VOLUME_UP);
    public static void VolumeDown() => Press(VK_VOLUME_DOWN);
    public static void PlayPause() => Press(VK_MEDIA_PLAY_PAUSE);
    public static void NextTrack() => Press(VK_MEDIA_NEXT_TRACK);
    public static void PreviousTrack() => Press(VK_MEDIA_PREV_TRACK);

    static void Press(ushort vk)
    {
        var inputs = new[] { Key(vk, 0), Key(vk, KEYEVENTF_KEYUP) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static INPUT Key(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags | KEYEVENTF_EXTENDEDKEY } },
    };

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // MOUSEINPUT is the largest member, so it must be present for the union to have the right size.
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
