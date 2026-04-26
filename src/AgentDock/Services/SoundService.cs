using System.Media;
using Microsoft.Win32;

namespace AgentDock.Services;

/// <summary>
/// Plays Windows system sounds by their registry event name.
/// </summary>
public static class SoundService
{
    public static void PlayDeviceConnect() => PlayRegistrySound("DeviceConnect");
    public static void PlayDeviceDisconnect() => PlayRegistrySound("DeviceDisconnect");
    public static void PlayMessageNudge() => PlayRegistrySound("MessageNudge");

    private static void PlayRegistrySound(string eventName)
    {
        try
        {
            var keyPath = $@"AppEvents\Schemes\Apps\.Default\{eventName}\.Current";
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            var wavPath = key?.GetValue(null) as string;

            if (!string.IsNullOrEmpty(wavPath) && System.IO.File.Exists(wavPath))
            {
                using var player = new SoundPlayer(wavPath);
                player.Play(); // async, non-blocking
            }
        }
        catch
        {
            // Sound is best-effort — never crash for a missing sound
        }
    }
}
