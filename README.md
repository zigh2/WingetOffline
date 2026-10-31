# WingetOffline

واجهة/أدوات لويندوز 10 للتعامل مع مستودع `winget-pkgs` على أجهزة **بلا إنترنت**:
كتالوج شامل، فحص البرامج المثبتة والمحمولة، فلاتر قوية، وتثبيت/إزالة من مثبّتات منقولة.

الإصدار الحالي: انظر ملف `VERSION` (0.0.1 — إصدار مبدئي).

## بناء الكتالوج (على الجهاز المتصل)
```
git clone --depth 1 https://github.com/microsoft/winget-pkgs
dotnet run --project src/WingetOffline.Builder -- .\winget-pkgs catalog.wglib
```
انقل `catalog.wglib` بالفلاش إلى الجهاز المعزول.

## فحص الجهاز ومطابقته مع الكتالوج (الجهاز المعزول، بلا إنترنت)
```
dotnet run -c Release --project .\src\WingetOffline.Cli -- stats .\catalog.wglib
dotnet run -c Release --project .\src\WingetOffline.Cli -- scan  .\catalog.wglib --csv scan.csv
dotnet run -c Release --project .\src\WingetOffline.Cli -- scan  .\catalog.wglib --portable D:\Tools --updates
```
- `Conf`: High (ProductCode/مجلد winget/اسم ARP مع الإصدار) · Medium (اسم+ناشر) · Low (يحتاج مراجعة).
- النتائج Low التي تظهر "UPDATE" تحتاج تحققاً يدوياً قبل أي تحديث.
- غير مدعوم بعد: حزم MSIX/Appx، و UpgradeCode، وربط المطابقة يدوياً.

## النشر على GitHub لأول مرة
```
git init -b main
git add -A
git commit -m "Initial release 0.0.1"
git remote add origin https://github.com/<user>/WingetOffline.git
git push -u origin main
git tag v0.0.1
git push origin v0.0.1      # يشغّل Release تلقائياً (pre-release)
```
شرط: وسم الإصدار يجب أن يطابق `VERSION` (مثلاً `v0.0.1`).

## تحديث المشروع عبر incoming/
ضع ملف zip واحداً فيه النسخة الجديدة داخل `incoming/` وادفعه إلى `main`.
يفكّه GitHub Action ويستبدل كل ملفات المشروع (عدا `.git` و`.github` و`incoming`)،
يبني المشروع للتحقق، ثم يعمل commit ويحذف الـ zip. التفاصيل في `incoming/README.md`.

**مهم:** غيّر رقم `VERSION` داخل الـ zip عند كل إصدار، وأضف سطراً في `CHANGELOG.md`.
في إعدادات المستودع: Settings ← Actions ← General ← Workflow permissions ← **Read and write permissions**.

## خارطة الطريق
1. ✅ محلل المانيفست + كتالوج SQLite/FTS5 + Builder
2. ✅ (0.0.3) فاحص البرامج المثبتة/المحمولة + المطابقة (CLI)
3. واجهة WPF على نمط UniGetUI
4. محرك التثبيت والإزالة (Silent + SHA256 + سجل)
5. Bundles وحزم التحديث غير المتصلة
