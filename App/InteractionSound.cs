using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
namespace ArmadaAnalyzer.Desktop;
public partial class MainWindow
{
    private long lastSound;
    private static readonly string ClickSoundPath = Path.Combine(AppContext.BaseDirectory, "Assets", "control.wav");
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);
    private void Sound_Click(object sender, RoutedEventArgs e)
    {
        if (SoundToggle.IsChecked != true) PlaySound(null, IntPtr.Zero, 0);
        SaveAppearance();
    }
    private void PlayInteractionSound(object sender, RoutedEventArgs e)
    {
        if (SoundToggle.IsChecked != true || !IsActive || e.OriginalSource is not ButtonBase button || button is RepeatButton) return;
        long now = Environment.TickCount64;
        if (now - lastSound < 70) return;
        lastSound = now;
        // Short low-amplitude local cue; async, no fallback system beep, never interrupts another sound.
        PlaySound(ClickSoundPath, IntPtr.Zero, 0x00020000 | 0x0001 | 0x0002 | 0x0010);
    }
}
