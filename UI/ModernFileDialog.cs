using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using ChillFocusWhitelist.Core;

namespace ChillFocusWhitelist.UI;

internal static class ModernFileDialog
{
    public static string PickExecutablePath()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "chillclock_file_pick_" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            var script = new StringBuilder();
            script.AppendLine("Add-Type -AssemblyName System.Windows.Forms");
            script.AppendLine("$d = New-Object System.Windows.Forms.OpenFileDialog");
            script.AppendLine("$d.Filter = 'Executable (*.exe)|*.exe|All files (*.*)|*.*'");
            script.AppendLine("$d.CheckFileExists = $true");
            script.AppendLine("$d.Multiselect = $false");
            script.AppendLine("if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {");
            script.AppendLine("  $d.FileName | Out-File -FilePath '" + tempFile.Replace("'", "''") + "' -Encoding UTF8");
            script.AppendLine("}");

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -STA -ExecutionPolicy Bypass -Command " + Quote(script.ToString()),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                if (process != null)
                    process.WaitForExit();
            }

            if (File.Exists(tempFile))
            {
                var result = File.ReadAllText(tempFile).Trim();
                try { File.Delete(tempFile); } catch { }
                return string.IsNullOrEmpty(result) ? null : result;
            }

            return null;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] Modern file dialog failed: " + e);
            return null;
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
