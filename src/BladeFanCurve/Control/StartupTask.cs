using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace BladeFanCurve.Control;

/// <summary>
/// Registers the app as an elevated logon task. A Scheduled Task is used rather than
/// the Run registry key because the app needs administrator rights, and a Run entry
/// would trigger a UAC prompt at every logon.
///
/// The task explicitly overrides the two defaults that would otherwise bite on a
/// laptop: tasks normally refuse to start on battery and stop when unplugged.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "BladeFanCurve";

    public static bool IsInstalled()
    {
        try
        {
            var (exitCode, _) = RunSchTasks($"/Query /TN \"{TaskName}\"");
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static (bool Ok, string Message) Install()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return (false, "Could not determine the executable path.");

            var user = WindowsIdentity.GetCurrent().Name;
            var xml = BuildTaskXml(exePath, user);

            var xmlPath = Path.Combine(Path.GetTempPath(), $"{TaskName}-{Guid.NewGuid():N}.xml");
            File.WriteAllText(xmlPath, xml, new UnicodeEncoding(false, true)); // schtasks wants UTF-16

            try
            {
                var (exitCode, output) = RunSchTasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
                return exitCode == 0
                    ? (true, "Blade Fan Curve will now start automatically at logon.")
                    : (false, $"schtasks failed ({exitCode}): {output.Trim()}");
            }
            finally
            {
                try { File.Delete(xmlPath); } catch { /* temp file */ }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string Message) Remove()
    {
        try
        {
            var (exitCode, output) = RunSchTasks($"/Delete /TN \"{TaskName}\" /F");
            return exitCode == 0
                ? (true, "Automatic startup removed.")
                : (false, $"schtasks failed ({exitCode}): {output.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string BuildTaskXml(string exePath, string user) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Temperature-driven fan control for Razer Blade laptops.</Description>
            <URI>\{TaskName}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
              <Delay>PT10S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
            <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>5</Priority>
            <RestartOnFailure>
              <Interval>PT1M</Interval>
              <Count>3</Count>
            </RestartOnFailure>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{System.Security.SecurityElement.Escape(exePath)}</Command>
              <Arguments>--tray</Arguments>
              <WorkingDirectory>{System.Security.SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? ".")}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    private static (int ExitCode, string Output) RunSchTasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null) return (-1, "could not start schtasks.exe");

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return (process.ExitCode, output);
    }
}
