using Microsoft.Win32;

namespace MsgImageExtractor.Core;

/// <summary>
/// Registers / unregisters the app to launch at Windows sign-in via the per-user
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run key (no admin rights needed).
/// Launched with <see cref="MinimizedArg"/> so startup is silent and tray-only.
/// </summary>
public static class StartupRegistration
{
    public const string MinimizedArg = "--minimized";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MsgImageExtractor";

    /// <summary>Build the Run-key command line for an executable path.</summary>
    public static string BuildCommand(string exePath) => $"\"{exePath}\" {MinimizedArg}";

    /// <summary>
    /// Bring the Run key in line with <paramref name="enabled"/>. When enabled, the entry
    /// is rewritten with the current exe path so a moved exe keeps auto-starting.
    /// Returns false on failure (caller logs).
    /// </summary>
    public static bool Apply(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    error = "Could not determine executable path.";
                    return false;
                }
                key.SetValue(ValueName, BuildCommand(exe), RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
