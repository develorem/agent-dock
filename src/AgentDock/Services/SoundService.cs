using System.Media;
using Microsoft.Win32;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services;

/// <summary>
/// Plays Windows system sounds by their registry event name.
/// </summary>
public sealed class SoundService : ISoundService
{
    public void PlayDeviceConnect() => PlayRegistrySound("DeviceConnect");
    public void PlayDeviceDisconnect() => PlayRegistrySound("DeviceDisconnect");
    public void PlayMessageNudge() => PlayRegistrySound("MessageNudge");

    /// <summary>
    /// Sound for the agent stopping to ask something (a question or a permission prompt),
    /// deliberately distinct from <see cref="PlayMessageNudge"/> so a prompt that blocks the
    /// turn is audibly different from the turn simply finishing. Uses the instant-message
    /// notification rather than the obvious-sounding "SystemQuestion" event, which still
    /// exists in the registry but ships with no .wav assigned on Windows 10/11 — it would
    /// silently play nothing.
    /// </summary>
    public void PlayQuestionPrompt() => PlayRegistrySound("Notification.IM");

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
