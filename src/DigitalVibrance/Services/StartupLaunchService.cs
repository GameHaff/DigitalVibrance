using System;
using System.IO;
using Microsoft.Win32;

namespace DigitalVibrance.Services;

public sealed class StartupLaunchService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "DigitalVibrance";

    public bool IsEnabled()
    {
        string? rawCommand = GetValueFromStartupRegistry();
        string? expectedCommand = BuildStartupCommand();

        if (string.IsNullOrWhiteSpace(rawCommand) || string.IsNullOrWhiteSpace(expectedCommand))
            return false;

        return string.Equals(ExtractExecutable(rawCommand), ExtractExecutable(expectedCommand), StringComparison.OrdinalIgnoreCase);
    }

    public bool SetEnabled(bool enabled)
    {
        string? startupCommand = BuildStartupCommand();

        if (enabled && string.IsNullOrWhiteSpace(startupCommand))
            return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;

            if (enabled)
            {
                key.SetValue(EntryName, startupCommand, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(EntryName, false);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    private static string? GetValueFromStartupRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(EntryName) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? BuildStartupCommand()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return null;

        return $"\"{exePath}\"";
    }

    private static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        string trimmed = command.Trim();

        if (trimmed.Length == 0) return null;

        if (trimmed.StartsWith('"'))
        {
            int closing = trimmed.IndexOf('"', 1);
            if (closing > 1)
                return trimmed[1..closing];
        }

        int whitespace = trimmed.IndexOf(' ');
        return whitespace > 0 ? trimmed[..whitespace] : trimmed;
    }
}
