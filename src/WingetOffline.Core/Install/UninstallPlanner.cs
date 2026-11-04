using System.Text.RegularExpressions;
using WingetOffline.Core.Scanning;

namespace WingetOffline.Core.Install;

/// <summary>يبني أمر إزالة لبرنامج مكتشف: يفضّل QuietUninstallString، ثم يحاول جعل UninstallString صامتاً بقواعد معروفة.</summary>
public static class UninstallPlanner
{
    private static readonly Regex GuidRx = new(
        @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.Compiled);
    private static readonly Regex SilentFlag = new(@"/(very)?silent|/s\b|/qn|/quiet", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static InstallPlan? Plan(InstalledApp app, bool interactive = false)
    {
        // برنامج محمول ثبّته winget: نحذف مجلده (بشرط أن يقع داخل WinGet\Packages)
        if (app.Source == "WinGetFolder")
        {
            var loc = app.InstallLocation;
            if (string.IsNullOrEmpty(loc) ||
                loc.IndexOf(@"\WinGet\Packages\", StringComparison.OrdinalIgnoreCase) < 0) return null;
            return new InstallPlan(PlanKind.RemoveFolder, loc, "", $"Remove portable package folder of {app.DisplayName}",
                app.Scope == "Machine", false);
        }

        bool quiet = !interactive && !string.IsNullOrWhiteSpace(app.QuietUninstallString);
        var raw = quiet ? app.QuietUninstallString : app.UninstallString;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var (file, args) = SplitCommand(Environment.ExpandEnvironmentVariables(raw!));
        string? warning = null;
        bool needsUi = interactive;
        bool machine = app.Scope == "Machine";

        if (!interactive && !quiet)
            (args, warning) = Silentize(file, args);
        if (warning != null && warning.StartsWith("Unknown", StringComparison.Ordinal)) needsUi = true;

        var plan = new InstallPlan(PlanKind.Process, file, args, $"Uninstall {app.DisplayName}",
            machine, needsUi, warning, null, IsUninstall: true);

        // بديل عند فشل الإزالة الصامتة: أمر الإزالة العادي كما هو مسجّل (بواجهته)
        if (!needsUi && !string.IsNullOrWhiteSpace(app.UninstallString))
        {
            var (ff, fa) = SplitCommand(Environment.ExpandEnvironmentVariables(app.UninstallString!));
            if (ff != plan.FileName || fa != plan.Arguments)
            {
                var fb = new InstallPlan(PlanKind.Process, ff, fa, plan.Description + " (interactive)",
                    machine, true, null, null, IsUninstall: true);
                plan = plan with { InteractiveFallback = fb };
            }
        }
        return plan;
    }

    /// <summary>يفصل "C:\Path\x.exe" /args أو C:\Path\x.exe /args إلى (ملف، وسيطات).</summary>
    public static (string File, string Args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (cmd[..(exe + 4)].Trim('"'), cmd[(exe + 4)..].Trim());
        int sp = cmd.IndexOf(' ');
        return sp > 0 ? (cmd[..sp], cmd[(sp + 1)..].Trim()) : (cmd, "");
    }

    private static (string Args, string? Warning) Silentize(string file, string args)
    {
        var name = Path.GetFileName(file).ToLowerInvariant();

        if (name is "msiexec.exe" or "msiexec")
        {
            var m = GuidRx.Match(args);
            return m.Success
                ? ($"/x {m.Value} /qn /norestart", null)
                : (args, "Unknown MSI command (no product code); it may show its UI.");
        }
        if (name.StartsWith("unins", StringComparison.Ordinal) && name.EndsWith(".exe", StringComparison.Ordinal))
        {
            return SilentFlag.IsMatch(args)
                ? (args, null)
                : ((args + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART").Trim(), null);
        }
        if (name is "uninst.exe" or "uninstall.exe" or "uninstaller.exe")
        {
            return SilentFlag.IsMatch(args)
                ? (args, null)
                : ((args + " /S").Trim(), "NSIS-style uninstaller: '/S' was assumed; verify the result.");
        }
        return (args, "Unknown uninstaller type: silent switches are not known and its UI may appear.");
    }
}
