using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace WingetOffline.Core.Install;

public sealed record RunResult(bool Success, int ExitCode, bool RebootRequired, string LogPath, string Message);

/// <summary>ينفّذ خطة تثبيت/إزالة، ويكتب سجلاً في %LOCALAPPDATA%\WingetOffline\logs ويعيد رمز الخروج.</summary>
public static class PlanRunner
{
    public static string LogDir => AppPaths.Combine("logs");

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static RunResult Run(InstallPlan plan, string label, bool allowElevation, TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(LogDir);
        var logPath = Path.Combine(LogDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{InstallPlanner.SafeName(label)}.log");
        var log = new StringBuilder();
        log.AppendLine($"[{DateTime.Now:O}] {plan.Description}");
        log.AppendLine($"Command: {plan.CommandLine}");

        RunResult result;
        try
        {
            result = plan.Kind switch
            {
                PlanKind.Portable => RunPortable(plan, label, logPath, log),
                PlanKind.RemoveFolder => RunRemove(plan, logPath, log),
                _ => RunProcess(plan, allowElevation, timeout ?? TimeSpan.FromMinutes(30), logPath, log),
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            result = new RunResult(false, 1223, false, logPath, "Elevation (UAC) was cancelled.");
        }
        catch (Exception ex)
        {
            result = new RunResult(false, -1, false, logPath, "Failed: " + ex.Message);
        }

        log.AppendLine($"Result: success={result.Success} exit={result.ExitCode} reboot={result.RebootRequired}");
        log.AppendLine(result.Message);
        try { File.WriteAllText(logPath, log.ToString()); } catch { }
        return result;
    }

    private static RunResult RunProcess(InstallPlan plan, bool allowElevation, TimeSpan timeout, string logPath, StringBuilder log)
    {
        bool elevateNow = allowElevation && plan.NeedsElevation && !IsElevated();
        var psi = new ProcessStartInfo(plan.FileName, plan.Arguments);
        bool redirect = !plan.Interactive && !elevateNow;

        if (redirect)
        {
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
        }
        else
        {
            psi.UseShellExecute = true;               // مطلوب لطلب صلاحيات المدير (UAC) أو لعرض واجهة المثبّت
            if (elevateNow) psi.Verb = "runas";
        }
        log.AppendLine($"Elevated via UAC: {elevateNow} | Redirected output: {redirect}");

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("The process did not start.");
        if (redirect)
        {
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine("ERR: " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            return new RunResult(false, -2, false, logPath, $"Timed out after {timeout.TotalMinutes:0} minutes; the process was left running.");
        p.WaitForExit(); // يفرّغ مخرجات القراءة غير المتزامنة

        int code = p.ExitCode;
        bool reboot = ExitCodes.IsRebootCode(code);
        bool nsisStyle = Path.GetFileName(plan.FileName).StartsWith("uninst", StringComparison.OrdinalIgnoreCase);
        bool ok = ExitCodes.IsSuccess(code, plan.IsUninstall, nsisStyle);
        var msg = ExitCodes.Describe(code);
        if (ok && !reboot && code != 0)
            msg = $"Completed (exit code {code} counts as success for this operation).";
        return new RunResult(ok, code, reboot, logPath, msg);
    }

    private static RunResult RunPortable(InstallPlan plan, string label, string logPath, StringBuilder log)
    {
        PortableInstaller.Install(plan.FileName, plan.Target!);
        PortableInstaller.Record(new PortableEntry(label, "", plan.Target!, DateTime.UtcNow));
        log.AppendLine("Extracted/copied to " + plan.Target);
        return new RunResult(true, 0, false, logPath, "Installed to " + plan.Target);
    }

    private static RunResult RunRemove(InstallPlan plan, string logPath, StringBuilder log)
    {
        var path = plan.FileName;
        if (path.IndexOf(@"\WinGet\Packages\", StringComparison.OrdinalIgnoreCase) < 0)
            return new RunResult(false, -3, false, logPath, "Refused: the folder is not inside WinGet\\Packages.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
        log.AppendLine("Deleted " + path);
        return new RunResult(true, 0, false, logPath, "Removed " + path);
    }
}
