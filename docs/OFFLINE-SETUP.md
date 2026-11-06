# تجهيز جهاز جديد (أو معزول) خطوة بخطوة

لا حاجة لبناء الكتالوج مرة أخرى ولا لتنزيل `winget-pkgs` (نحو 240 MB) على الجهاز الجديد.

## ما تحتاج نقله (على فلاش أو عبر الشبكة)
| الملف | من أين | الحجم التقريبي |
|---|---|---|
| `WingetOffline.exe` | `dotnet publish` (انظر أدناه) أو Releases | ~150 MB (يتضمن .NET فلا يحتاج تثبيته) |
| `catalog.wglib` | الملف الذي بنيتَه سابقاً، أو الرابط اليومي من Releases | ~38 MB |
| مجلد مثبّتات (اختياري) | انظر الخطوة 4 | حسب ما تضع |
| `wgo.exe` (اختياري) | `dotnet publish` | ~70 MB |

## الخطوات
1. **جهّز الملفات على جهاز التطوير:**
   ```powershell
   cd C:\Users\iiiiiii\WingetOffline
   foreach ($p in 'App','Cli') {
     dotnet publish ".\src\WingetOffline.$p" -c Release -r win-x64 --self-contained `
       -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ".\out\$p"
   }
   $d = "C:\Users\iiiiiii\Transfer"; New-Item -ItemType Directory -Force $d | Out-Null
   Copy-Item .\out\App\WingetOffline.exe, .\out\Cli\wgo.exe $d
   Copy-Item C:\Users\iiiiiii\catalog.wglib $d
   ```
2. **انسخ مجلد `Transfer` إلى الجهاز الجديد** (مثلاً `D:\WingetOffline`). يحتاج الجهاز ويندوز 10 (1607 فأحدث) ومساحة حرة نحو 250 MB.
3. **شغّل `WingetOffline.exe`.** إن ظهرت شاشة SmartScreen فاختر More info ثم Run anyway (الملف غير موقّع).
   - Settings ← Catalog ← Browse ← `catalog.wglib` ← **Load**.
   - أول تحميل يفك الكتالوج ويتحقق من SHA256 (قد يستغرق نحو دقيقة)، ثم يُفحص الجهاز تلقائياً.
4. **المثبّتات (اختياري):** على جهاز متصل نزّل مثبّتات بنفس إصدار الكتالوج حتى تتطابق البصمة:
   ```powershell
   wgo search catalog.wglib winscp --limit 3                       # اعرف الإصدار (عمود Latest)
   winget download --id WinSCP.WinSCP --version <LATEST> --download-directory D:\Installers
   ```
   انقل المجلد، ثم Settings ← Installers ← Browse ← **Index now**، وافتح صفحة Installers.
5. **وضع محمول (اختياري):** أنشئ ملفاً فارغاً باسم `portable.txt` بجانب `WingetOffline.exe` فتُحفظ كل البيانات في `data\` بجانبه (يشمل الكاش ~165 MB).

## تحديث برامج الجهاز المعزول (الدورة الكاملة)
1. **على المعزول:** صفحة Updates ← **Export list** ← احفظ `updates-needed.json` (أو من سطر الأوامر: `wgo scan catalog.wglib --export-updates updates-needed.json`).
2. **على المتصل:** (ملف `WingetOffline.Builder.exe` موجود في ملف الإصدار)
   ```powershell
   WingetOffline.Builder.exe pack --catalog catalog.wglib --from-scan updates-needed.json --out D:\Pack
   ```
   يعرض الخطة (تُجرّب أولاً بـ `--dry-run`)، ثم ينزّل كل مثبّت مع تبعياته ويتحقق من SHA256 ويكتب `install-offline.ps1`.
   تبعيات الحزم تُسجَّل في الكتالوج الجديد فقط (المبني بإصدار 0.0.9 أو أحدث).
3. **على المعزول:** انسخ `D:\Pack`، ثم Settings ← Installers ← Browse ← **Index now**، وصفحة Updates ← **Update all**. أو شغّل `install-offline.ps1` بصلاحيات المدير دون التطبيق.

## تحديث الكتالوج بلا بناء
تبني GitHub Actions الكتالوج تلقائياً كل يوم (workflow `catalog.yml`) وتنشره في رابط ثابت:
```
https://github.com/<USER>/WingetOffline/releases/download/catalog/catalog.wglib
https://github.com/<USER>/WingetOffline/releases/download/catalog/catalog-meta.json   (تاريخ البناء + commit + SHA256 للملف)
```
نزّل الملف من أي جهاز متصل بالمتصفح، وانسخه للجهاز الجديد، ثم Settings ← Load. تُحذف نسخة الكاش القديمة تلقائياً.

## قبل أي تثبيت فعلي
جرّب أولاً أوامر `wgo install ... ` بدون `--run` (تجربة جافة)، وراجع الأمر المعروض في نافذة التأكيد.
