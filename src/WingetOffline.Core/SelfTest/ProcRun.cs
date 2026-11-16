using System.Diagnostics;
using System.Text;

namespace WingetOffline.Core.SelfTest;

internal sealed record ProcResult(bool Started, int ExitCode, string Output, bool TimedOut);

/// <summary>تشغيل برنامج خارجي بمهلة والتقاط مخرجاته (للفحوص التي تستدعي wgo وBuilder وdotnet).</summary>
internal static class ProcRun
{
    public static ProcResult Run(string file, string arguments, int timeoutMs, string? workDir = null, IDictionary<string, string>? env = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workDir ?? Environment.CurrentDirectory,
            };
            if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
            using var p = Process.Start(psi);
            if (p == null) return new ProcResult(false, -1, "", false);
            p.StandardInput.Close();   // لا ننتظر إدخالاً أبداً

            var sb = new StringBuilder();
            var gate = new object();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new ProcResult(true, -1, sb.ToString(), true);
            }
            p.WaitForExit();   // يفرغ ما تبقى من المخرجات
            lock (gate) return new ProcResult(true, p.ExitCode, sb.ToString(), false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcResult(false, -1, ex.Message, false);
        }
    }

    /// <summary>يبحث عن أداة باسم من الأسماء في مجلد الأدوات ثم مجلد البرنامج ثم PATH.</summary>
    public static string? FindTool(SelfTestContext c, params string[] baseNames)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.Options.ToolsDirectory)) dirs.Add(c.Options.ToolsDirectory!);
        dirs.Add(AppContext.BaseDirectory);
        if (!string.IsNullOrEmpty(Environment.ProcessPath)) dirs.Add(Path.GetDirectoryName(Environment.ProcessPath)!);
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe" } : new[] { "", ".exe" };
        foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var n in baseNames)
                foreach (var e in exts)
                {
                    try
                    {
                        var p = Path.Combine(d.Trim('"'), n + e);
                        if (File.Exists(p)) return p;
                    }
                    catch (ArgumentException) { }
                }
        // أسماء بالإصدار مثل wgo-v0.1.4-win-x64-lite.exe
        foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var n in baseNames)
            {
                try
                {
                    var dir = d.Trim('"');
                    if (!Directory.Exists(dir)) continue;
                    var hit = Directory.EnumerateFiles(dir, n + "*.exe").OrderBy(f => f.Length).FirstOrDefault();
                    if (hit != null) return hit;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        return null;
    }
}
