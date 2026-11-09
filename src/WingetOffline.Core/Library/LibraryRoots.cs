namespace WingetOffline.Core.Library;

/// <summary>يحدّد المجلدات الجذرية التي تُفحص: مجلداتك المضافة، أو كل أقراص الكمبيوتر، أو الاثنان معاً.</summary>
public static class LibraryRoots
{
    /// <param name="wholeComputer">true: كل الأقراص الثابتة (Fixed).</param>
    /// <param name="includeRemovable">مع wholeComputer: أضف الأقراص القابلة للإزالة (USB وخارجية).</param>
    /// <param name="folders">مجلدات أضفتها يدوياً؛ يُهمَل منها ما يقع تحت قرص مشمول أصلاً.</param>
    public static List<string> Compute(bool wholeComputer, bool includeRemovable, IEnumerable<string> folders)
    {
        var roots = new List<string>();
        if (wholeComputer)
        {
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType == DriveType.Fixed || (includeRemovable && d.DriveType == DriveType.Removable))
                        roots.Add(d.RootDirectory.FullName);
                }
            }
            catch (IOException) { /* قائمة الأقراص غير متاحة: نكتفي بالمجلدات */ }
        }

        foreach (var f in folders)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            var full = f.Trim();
            if (!roots.Any(r => LibraryStore.IsUnderPublic(full, r)) && !roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                roots.Add(full);
        }
        return roots;
    }
}
