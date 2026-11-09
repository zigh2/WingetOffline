<div align="center">

# WingetOffline

**A package manager for Windows 10/11 machines that have no internet, built on the `winget-pkgs` repository.**

[![CI](https://github.com/zigh2/WingetOffline/actions/workflows/ci.yml/badge.svg)](https://github.com/zigh2/WingetOffline/actions/workflows/ci.yml)
![License](https://img.shields.io/badge/license-MIT-blue)
![Version](https://img.shields.io/badge/version-0.1.0-informational)

[English](#english) · [العربية](#arabic)

</div>

<a id="english"></a>

# English

WingetOffline searches a local catalog of about 16,000 packages, scans what is installed on your PC (installed and portable programs), compares it with the latest versions, and installs, removes and updates from installers you carried over on a flash drive.

Version: see `VERSION` (0.1.0) · [Changelog](CHANGELOG.md) · [Detailed comparison](docs/COMPARISON.md) · [Test plan](docs/TEST-PLAN.md)

## How it works

```
Connected machine                                 Offline machine
─────────────────                                 ───────────────
Builder: builds catalog.wglib from winget-pkgs ─► App / wgo:
Installers (you download them now)             ─►   • scans your programs and compares them with the catalog
                                                    • recognises installers by SHA256 fingerprint
                                                    • installs / removes / updates (with your confirmation)
```

## Components

| Project | Role |
|---|---|
| `src/WingetOffline.Core` | Manifest parser, catalog (SQLite + FTS5), scanner, matcher, install engine, exporters, local-library readers |
| `src/WingetOffline.Builder` | **Connected machine only.** Builds `catalog.wglib` and collects installers (`pack`) with SHA256 verification |
| `src/WingetOffline.Cli` | `wgo`, the command-line tool for the offline machine |
| `src/WingetOffline.App` | WPF app (Discover · Installed · Updates · Installers · Local · Settings) |
| `tests/WingetOffline.Tests` | xUnit tests (169 cases) |

`WingetOffline.Builder.exe` talks to the internet and prepares material. `wgo.exe` and the app never touch the network; they work with what you carried over. They are kept as separate programs on purpose: the offline tools contain no download code at all, so that guarantee can be checked.

## Quick start

```powershell
# 1) On a connected machine: build the catalog
git clone --depth 1 https://github.com/microsoft/winget-pkgs
dotnet run -c Release --project src/WingetOffline.Builder -- .\winget-pkgs catalog.wglib

# 2) Copy catalog.wglib (+ an installers folder) to the offline machine and start the app
dotnet run -c Release --project src/WingetOffline.App
#    Settings → choose catalog.wglib → Load → choose installers folder → Index now
```

You can skip step 1: GitHub Actions builds the catalog daily and publishes it at a fixed link  
`https://github.com/<USER>/WingetOffline/releases/download/catalog/catalog.wglib`  
Full setup guide for a new or offline machine: [docs/OFFLINE-SETUP.md](docs/OFFLINE-SETUP.md).

## Downloads

Each release has three packages. All contain the same three programs (`WingetOffline.exe`, `wgo.exe`, `WingetOffline.Builder.exe`).

| Package | What is different | Needs .NET installed? |
|---|---|---|
| `…-win-x64-lite.zip` | .NET is **not** inside the files: the smallest download | **Yes**: .NET 8 Desktop Runtime (x64) |
| `…-win-x64-standalone.zip` | .NET inside each file, compressed | No |
| `…-win-x64-full.zip` | standalone **plus** `catalog.wglib` as it was at release time | No |

The compressed standalone files start a couple of seconds slower the first time. Real sizes are printed in the release run summary (Actions → Release → "Package sizes") and are not guessed here.

## Moving software to another machine

WingetOffline does **not** copy an installed program from one PC to another. It moves the program's **installer**, and the target machine installs it.

```
Source machine                  Connected machine                               Target machine
──────────────                  ─────────────────                               ──────────────
Installed → Export  ─ .json ─►  Builder pack --from-scan list.json --out D:\Pack ─►  Settings → Installers → Index now
(or Updates → Export list)      (downloads installers + dependencies,                 Updates → Update all
                                 verifies SHA256, writes install-offline.ps1)         (or run install-offline.ps1)
```

```powershell
WingetOffline.Builder.exe pack --catalog catalog.wglib --from-scan updates.json --out D:\Pack
WingetOffline.Builder.exe pack --catalog catalog.wglib --ids Git.Git,VideoLAN.VLC --out D:\Pack --dry-run
```

Other inputs: `--list ids.txt` (one package per line, `Id` or `Id@Version`), `--arch`, `--scope`, `--no-deps`. A file whose SHA256 does not match the catalog is deleted and never enters the pack.

## Features

| Feature | Details |
|---|---|
| Local catalog | Instant search + filters: publisher, license, tag, installer type, architecture, scope, silent install, portable, ProductCode |
| Machine scan | Registry (32/64-bit, machine/user) + winget portables + your own folders |
| Matching with confidence | High/Medium/Low with the reason for every match (ProductCode → ARP name → command → name+publisher → fuzzy) |
| Installer recognition | SHA256 fingerprint against the catalog, no extra data file |
| Install and uninstall | MSI/WiX/Burn/Inno/NSIS/EXE/MSIX/ZIP/portable; silent switches; UAC when needed; a log for every operation |
| Safe execution | Confirmation shows the full command · dry run in `wgo` · retry with the installer's own window when silent mode fails |
| Update management | Update / Update all from your folder · ignore a package's updates · export the needed-updates list · "No uninstall command" filter |
| **Local library** | **Indexes your own exe/msi/msix/zip/7z/iso collection locally (new files only), with filters, export and matching against installed programs** |
| **Export** | **Installed, Updates and Local pages export to Excel (.xlsx), CSV and JSON. UTF-8 throughout, so Arabic and every other language are preserved** |
| **One-click theme** | **Button at the bottom of the sidebar switches Light/Dark instantly and shows the current theme** |
| Installer collection | `Builder pack`: one installer per package plus its **dependencies**, SHA256 check, resume, `pack-manifest.json` + `install-offline.ps1` |
| Version accuracy | Prefers the version in the name when `DisplayVersion` is odd · "Newer than catalog" status · `wgo audit-versions` checks version order against real release dates |
| Automation | `wgo` + `--json` output + `--export-updates` + standalone PowerShell script (`wgo script`) |
| Portable mode | Put a `portable.txt` next to the program and all data goes to a `data` folder |

### Exporting data

| Page | Button | Formats | Notes |
|---|---|---|---|
| Installed | Export | Excel · CSV · JSON | Exports the rows currently shown (after filters). The JSON is also accepted by `Builder pack --from-scan` (weak matches are skipped) |
| Updates | Export list | JSON · Excel · CSV | JSON is the file the connected machine reads; Excel/CSV are for people |
| Local | Export | Excel · CSV · JSON | The rows shown (after filters), 21 columns; size is a real number and date a real date in Excel |

CSV is UTF-8 with a BOM so Excel reads Arabic correctly, and cells that start with `=`, `+`, `-` or `@` are neutralised against CSV injection. If your Excel uses `;` as the list separator, open the CSV with Data → From Text/CSV, or simply use the Excel format. Excel sheets are switched to right-to-left automatically when most of the text is Arabic, Hebrew or Persian.

### Local library (your own installers)

The **Local** page indexes your own collection (exe, msi, msix, zip, 7z, iso...). It reads only file headers, never runs anything, saves the result locally and re-reads only new or changed files (1,500 test files: first scan about 1.4 s, next scan 30 ms). Identified files show with many filters and can be exported. When a file matches an installed program, the **Installed** page shows it too, whether the library copy is newer, the same or older. Details, accuracy and limits: [docs/LIBRARY.md](docs/LIBRARY.md).

## `wgo` (command line)

```
wgo stats | search | scan | index | install | uninstall | script | audit-versions | library
wgo index   catalog.wglib D:\Installers            # which file matches which package?   (--json)
wgo install catalog.wglib app.exe [--run]          # dry run; --run executes
wgo uninstall WinSCP [--run]
wgo script  catalog.wglib D:\Installers            # standalone install-offline.ps1
wgo scan    catalog.wglib --updates --json
wgo scan    catalog.wglib --export-updates updates.json   # list for the connected machine
wgo audit-versions catalog.wglib                           # check the version comparer on real data
wgo library scan D:\Software --export library.xlsx          # index your own installers (new files only)
wgo library match catalog.wglib --newer                    # installed programs whose library copy is newer
```

## Comparison with similar projects

> Full details (description and code) in [docs/COMPARISON.md](docs/COMPARISON.md). No code was copied; ideas only.

| Project | What it is | What we took | Status |
|---|---|---|---|
| [UniGetUI](https://github.com/Devolutions/UniGetUI) | Unified UI for package managers (MIT) | Ignore a package's updates · portable mode · JSON output · tests + CI | ✅ |
| | | Bundles · operation history · scheduling · translation | 🕓 |
| [winget-cli](https://github.com/microsoft/winget-cli) | The official manager (`winget download`) | Installer-pack model with dependencies (`Builder pack`) | ✅ 0.0.9 |
| [Chocolatey GUI](https://github.com/chocolatey/ChocolateyGUI) | GUI for Chocolatey | Valid exit codes (1605/1614 on uninstall) | ✅ |
| [Scoop](https://github.com/ScoopInstaller/Scoop) | Portable command-line installer | Package hold · portable mode | ✅ |
| [Winget-AutoUpdate](https://github.com/Romanitho/Winget-AutoUpdate) | Scheduled automatic updates | Exclusion lists | ✅ |
| | | Scheduling · notification levels · hooks · log rotation | 🕓 |
| [Bulk Crap Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller) | Bulk uninstall | Retry with the program's own window · exit code 1627 for NSIS · readable error messages · dry-run · "invalid uninstaller" filter | ✅ |
| | | Digital-signature column · restore point · MSIX/Store | 🕓 |
| winstall | Pick programs and generate a script | Install-script generation (`wgo script`) | ✅ |
| WingetBatch (PowerShell) | `Export-WingetOffline` | Offline repository + script + hash check + dependencies | ✅ |
| WSUS Offline Update | Windows updates without a connection | Separating the generator from the applier (Builder / app) | ✅ concept |

**Where we stand out:** designed for a machine with no internet (local catalog + fingerprint recognition + matching that explains itself).  
**Where we fall short:** more than one package source, MSIX/Store, scheduling, operation history, Bundles, translation, signature verification.  
**⛔ Will not be taken:** other package managers · cloud Gist backup · NativeAOT/Avalonia · system-tray icon · PWA (reasons in the document).

## Roadmap

| Version | Content |
|---|---|
| ✅ 0.0.8 | Documented comparison + improvements derived from it + tests |
| ✅ 0.0.9 | Installer collector (`pack`) + dependencies + needed-updates export + version audit tool |
| ✅ 0.0.9.1 | Rewritten version comparer · PowerToys-style channel names · `audit-versions --csv` |
| ✅ 0.0.9.2 | Export to Excel/CSV/JSON (Installed + Updates) · one-click theme button |
| ✅ 0.1.0 | **Local library** (Local page, incremental, filters, export, matching with Installed, `wgo library`) · **three download packages** (lite / standalone / full) |
| 🕓 0.2.0 | Bundles (export/import) · operation history page |
| 🕓 later | Translation (Arabic first) · scheduling · digital signature · restore point · MSIX |

## Publishing and updating through GitHub

- **Release:** raise `VERSION`, then create a release (Releases → Create a new release) with the tag `v` + the exact `VERSION` (for example `v0.0.9.2`). The workflow builds the three packages (lite, standalone, full).
- **Full update:** put **one zip file** in `incoming/` and push it. The Action replaces the project files (except `.git`, `.github` and `incoming`) after a verification build ([details](incoming/README.md)).
- In the repository settings: Actions → Workflow permissions → **Read and write**.

## Verification status (honest)

| | |
|---|---|
| Tried for real | Catalog build locally and on GitHub (Linux, about one minute) · scan and matching · the UI in both themes · applying it on a second machine · **automated tests 98/98 on Windows and Linux** · release publishing and catalog download with fingerprint check · `wgo stats/search/audit-versions` |
| Not tried yet | **Real install and uninstall** · `Builder pack` and downloading · `wgo script` · MSIX install · the tests added in 0.0.9.1 (17 cases) · on Windows: the theme button, the Export buttons and the whole **Local** page (the exporters' and library readers' tests pass in a Linux sandbox: 54 cases, an MSI read from a real file built with wixl, .xlsx opened in openpyxl and LibreOffice, but not yet on CI, on Windows or in Excel) · **the three-package release workflow** (never run) · reading your real, varied installers (T8.4) |

## Security and license

- The app contains no network code. The only connected part is the build tool (Builder).
- Nothing is installed or removed without explicit confirmation; every operation is logged in `logs/`.
- License: MIT (see `LICENSE`).

<br>

---

<a id="arabic"></a>

<div dir="rtl" lang="ar">

# العربية

**مدير برامج لويندوز 10/11 يعمل على جهاز بلا إنترنت، مبني على مستودع `winget-pkgs`.**

يبحث في كتالوج محلي يضم نحو 16,000 حزمة، ويفحص برامج جهازك (المثبّتة والمحمولة) ويقارنها بأحدث الإصدارات، ثم يثبّت ويزيل ويحدّث من مثبّتات نقلتَها بالفلاش.

الإصدار: انظر `VERSION` (0.1.0) · [سجل التغييرات](CHANGELOG.md) · [المقارنة التفصيلية](docs/COMPARISON.md) · [خطة الاختبار](docs/TEST-PLAN.md)

## كيف يعمل

```
جهاز متصل                                    الجهاز المعزول (بلا إنترنت)
─────────────                                ───────────────────────────
Builder: يبني catalog.wglib من winget-pkgs ─► الواجهة / wgo:
مثبّتات (تنزّلها بنفسك الآن)               ─►   • تفحص البرامج وتقارنها بالكتالوج
                                                • تتعرّف على المثبّتات بالبصمة SHA256
                                                • تثبّت / تزيل / تحدّث (مع تأكيدك)
```

## المكوّنات

| المشروع | الدور |
|---|---|
| `src/WingetOffline.Core` | المحلل والكتالوج (SQLite + FTS5) والفاحص والمطابقة ومحرك التثبيت ومكتبة التصدير وقارئات المكتبة المحلية |
| `src/WingetOffline.Builder` | **الجهاز المتصل فقط:** يبني `catalog.wglib` ويجمع المثبّتات (`pack`) مع التحقق من SHA256 |
| `src/WingetOffline.Cli` | `wgo`: أداة سطر الأوامر للجهاز المعزول |
| `src/WingetOffline.App` | واجهة WPF (Discover · Installed · Updates · Installers · Local · Settings) |
| `tests/WingetOffline.Tests` | اختبارات xUnit (169 حالة) |

الفرق بين الأداتين: `WingetOffline.Builder.exe` يتصل بالإنترنت ويجهّز المواد، أما `wgo.exe` والواجهة فلا يلمسان الشبكة أبداً ويعملان بما نقلتَه إليهما. فُصلتا عمداً: الأدوات غير المتصلة لا تحوي أي كود تنزيل، فيمكن التحقق من هذا الضمان.

## البدء السريع

```powershell
# 1) على الجهاز المتصل: ابنِ الكتالوج
git clone --depth 1 https://github.com/microsoft/winget-pkgs
dotnet run -c Release --project src/WingetOffline.Builder -- .\winget-pkgs catalog.wglib

# 2) انقل catalog.wglib (+ مجلد مثبّتات) إلى الجهاز المعزول ثم شغّل الواجهة
dotnet run -c Release --project src/WingetOffline.App
#    Settings ← اختر catalog.wglib ← Load ← اختر مجلد المثبّتات ← Index now
```

يمكنك تخطي الخطوة 1: تبني GitHub Actions الكتالوج يومياً وتنشره في رابط ثابت  
`https://github.com/<USER>/WingetOffline/releases/download/catalog/catalog.wglib`  
دليل تجهيز جهاز جديد أو معزول: [docs/OFFLINE-SETUP.md](docs/OFFLINE-SETUP.md).

## التنزيلات

لكل إصدار ثلاث حزم، وكلها تحوي البرامج الثلاثة نفسها (`WingetOffline.exe` و`wgo.exe` و`WingetOffline.Builder.exe`).

| الحزمة | الفرق | تحتاج .NET مثبّتاً؟ |
|---|---|---|
| `…-win-x64-lite.zip` | .NET **ليس** داخل الملفات: أصغر تنزيل | **نعم**: .NET 8 Desktop Runtime (x64) |
| `…-win-x64-standalone.zip` | .NET داخل كل ملف ومضغوط | لا |
| `…-win-x64-full.zip` | standalone **مع** `catalog.wglib` كما كان وقت الإصدار | لا |

الملفات المضغوطة في standalone تبدأ أبطأ بثوانٍ في أول مرة. الأحجام الفعلية تُطبع في ملخص تشغيل الإصدار (Actions ← Release ← "Package sizes") ولا أخمّنها هنا.

## نقل برنامج إلى جهاز آخر

المشروع **لا ينسخ برنامجاً مثبّتاً** من جهاز إلى آخر، بل ينقل **مثبّته** ثم يثبّته الجهاز الهدف.

```
الجهاز المصدر                   الجهاز المتصل                                  الجهاز الهدف
─────────────                   ─────────────                                  ─────────────
Installed ← Export ─ .json ─►   Builder pack --from-scan list.json --out D:\Pack ─►  Settings ← Installers ← Index now
(أو Updates ← Export list)      (ينزّل المثبّتات + التبعيات،                          Updates ← Update all
                                 يتحقق من SHA256، يكتب install-offline.ps1)           (أو تشغيل install-offline.ps1)
```

```powershell
WingetOffline.Builder.exe pack --catalog catalog.wglib --from-scan updates.json --out D:\Pack
WingetOffline.Builder.exe pack --catalog catalog.wglib --ids Git.Git,VideoLAN.VLC --out D:\Pack --dry-run
```

مدخلات أخرى: `--list ids.txt` (سطر لكل حزمة، `Id` أو `Id@Version`)، و`--arch`، و`--scope`، و`--no-deps`. الملف الذي لا يطابق SHA256 الكتالوج يُحذف ولا يدخل الحزمة.

## المزايا

| الميزة | التفاصيل |
|---|---|
| كتالوج محلي | بحث فوري + فلاتر: الناشر، الرخصة، الوسم، نوع المثبّت، المعمارية، النطاق، تثبيت صامت، محمول، ProductCode |
| فحص الجهاز | السجل (32/64 بت، آلة/مستخدم) + محمولات winget + مجلداتك |
| مطابقة بدرجات ثقة | High/Medium/Low مع سبب كل مطابقة (ProductCode ← اسم ARP ← أمر ← اسم+ناشر ← تقريبي) |
| التعرّف على المثبّتات | بصمة SHA256 مقابل الكتالوج، بلا ملف بيانات إضافي |
| تثبيت وإزالة | MSI/WiX/Burn/Inno/NSIS/EXE/MSIX/ZIP/محمول؛ مفاتيح صامتة؛ UAC عند الحاجة؛ سجل لكل عملية |
| أمان التشغيل | تأكيد يعرض الأمر الكامل · تجربة جافة في `wgo` · إعادة المحاولة بواجهة المثبّت عند فشل الصامت |
| إدارة التحديثات | تحديث/تحديث الكل من مجلدك · تجاهل تحديثات حزمة · تصدير قائمة التحديثات المطلوبة · فلتر "لا يوجد أمر إزالة" |
| **المكتبة المحلية** | **تفهرس مجموعة exe/msi/msix/zip/7z/iso عندك محلياً (الجديد فقط) بفلاتر وتصدير ومطابقة مع البرامج المثبّتة** |
| **التصدير** | **صفحات Installed وUpdates وLocal تصدّر إلى Excel (.xlsx) وCSV وJSON. الترميز UTF-8 في كل الصيغ، فتُحفظ العربية وكل اللغات الأخرى** |
| **ثيم بضغطة واحدة** | **زر أسفل القائمة الجانبية يبدّل بين الفاتح والداكن فوراً ويعرض اسم الثيم الحالي** |
| جمع المثبّتات | `Builder pack`: مثبّت واحد لكل حزمة مع **تبعياتها**، تحقق SHA256، استئناف، `pack-manifest.json` + `install-offline.ps1` |
| دقة الإصدارات | يفضّل إصدار الاسم عند اعوجاج `DisplayVersion` · حالة "Newer than catalog" · `wgo audit-versions` يقارن ترتيب الإصدارات بتواريخ الإصدار الحقيقية |
| الأتمتة | `wgo` + مخرجات `--json` + `--export-updates` + توليد سكربت PowerShell مستقل (`wgo script`) |
| الوضع المحمول | ضع ملف `portable.txt` بجانب البرنامج فتُحفظ البيانات في مجلد `data` |

### تصدير البيانات

| الصفحة | الزر | الصيغ | ملاحظات |
|---|---|---|---|
| Installed | Export | Excel · CSV · JSON | يصدّر الصفوف الظاهرة الآن (بعد الفلاتر). ملف JSON يقبله أيضاً `Builder pack --from-scan` (تُتجاهل المطابقات الضعيفة) |
| Updates | Export list | JSON · Excel · CSV | JSON هو الملف الذي يقرؤه الجهاز المتصل؛ وExcel وCSV للقراءة البشرية |
| Local | Export | Excel · CSV · JSON | الصفوف الظاهرة (بعد الفلاتر)، 21 عموداً؛ الحجم رقم حقيقي والتاريخ تاريخ حقيقي في Excel |

ملف CSV بترميز UTF-8 مع علامة BOM ليقرأ Excel العربية صحيحة، والخلايا التي تبدأ بـ `=` أو `+` أو `-` أو `@` تُعطَّل لمنع حقن الصيغ. إن كان Excel عندك يستخدم `;` فاصلاً للقوائم فافتح CSV عبر Data ← From Text/CSV أو استخدم صيغة Excel مباشرة. وتتحول أوراق Excel إلى اتجاه اليمين لليسار تلقائياً حين يغلب النص العربي أو العبري أو الفارسي.

### المكتبة المحلية (مثبّتاتك أنت)

صفحة **Local** تفهرس مجموعتك (exe وmsi وmsix وzip و7z وiso...). تقرأ رؤوس الملفات فقط ولا تشغّل شيئاً، وتحفظ النتيجة محلياً ولا تعيد قراءة إلا الجديد أو المتغيّر (1500 ملف تجريبي: الفحص الأول نحو 1.4 ثانية والتالي 30 ميلي ثانية). تظهر الملفات المعرَّفة بفلاتر كثيرة وتُصدَّر، وعند تطابق ملف مع برنامج مثبّت تعرضه صفحة **Installed** أيضاً، سواء كانت نسخة المكتبة أحدث أو مطابقة أو أقدم. التفاصيل والدقة والحدود: [docs/LIBRARY.md](docs/LIBRARY.md).

## `wgo` (سطر الأوامر)

```
wgo stats | search | scan | index | install | uninstall | script | audit-versions
wgo index   catalog.wglib D:\Installers            # أي ملف يطابق أي حزمة؟   (--json)
wgo install catalog.wglib app.exe [--run]          # تجربة جافة؛ --run للتنفيذ
wgo uninstall WinSCP [--run]
wgo script  catalog.wglib D:\Installers            # install-offline.ps1 مستقل
wgo scan    catalog.wglib --updates --json
wgo scan    catalog.wglib --export-updates updates.json   # قائمة للجهاز المتصل
wgo audit-versions catalog.wglib                           # فحص مقارِن الإصدارات على بيانات حقيقية
wgo library scan D:\Software --export library.xlsx          # فهرسة مثبّتاتك (الجديد فقط)
wgo library match catalog.wglib --newer                    # برامج مثبّتة وفي مكتبتك نسخة أحدث منها
```

## المقارنة مع المشاريع المشابهة

> التفاصيل الكاملة (وصفاً وكوداً) في [docs/COMPARISON.md](docs/COMPARISON.md). لم يُنسخ أي كود؛ أفكار فقط.

| المشروع | ما هو | ما جلبناه | الحالة |
|---|---|---|---|
| [UniGetUI](https://github.com/Devolutions/UniGetUI) | واجهة موحّدة لمديري الحزم (MIT) | تجاهل تحديثات حزمة · وضع محمول · مخرجات JSON · اختبارات + CI | ✅ |
| | | Bundles · سجل العمليات · جدولة · ترجمة | 🕓 |
| [winget-cli](https://github.com/microsoft/winget-cli) | المدير الرسمي (`winget download`) | نموذج جمع المثبّتات مع التبعيات (`Builder pack`) | ✅ 0.0.9 |
| [Chocolatey GUI](https://github.com/chocolatey/ChocolateyGUI) | واجهة Chocolatey | رموز الخروج الصالحة (1605/1614 عند الإزالة) | ✅ |
| [Scoop](https://github.com/ScoopInstaller/Scoop) | مثبّت محمول بسطر الأوامر | تجميد الحزم (hold) · الوضع المحمول | ✅ |
| [Winget-AutoUpdate](https://github.com/Romanitho/Winget-AutoUpdate) | تحديث تلقائي مجدول | قوائم الاستبعاد | ✅ |
| | | جدولة · مستويات إشعارات · خطّافات · تدوير سجلات | 🕓 |
| [Bulk Crap Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller) | إزالة جماعية | إعادة المحاولة بواجهة البرنامج · كود 1627 لـ NSIS · رسائل أخطاء مفهومة · dry-run · فلتر "مزيل غير صالح" | ✅ |
| | | عمود توقيع رقمي · نقطة استعادة · MSIX/Store | 🕓 |
| winstall | اختيار برامج وتوليد سكربت | توليد سكربت تثبيت (`wgo script`) | ✅ |
| WingetBatch (PowerShell) | `Export-WingetOffline` | مستودع غير متصل + سكربت + تحقق هاش + تبعيات | ✅ |
| WSUS Offline Update | تحديثات Windows دون اتصال | فصل المولّد عن المطبِّق (Builder / التطبيق) | ✅ مفهوم |

**أين نتميّز:** تصميم لجهاز بلا إنترنت (كتالوج محلي + تعرّف بالبصمة + مطابقة تشرح نفسها).  
**أين ينقصنا:** أكثر من مصدر حزم، MSIX/Store، جدولة، سجل عمليات، Bundles، ترجمة، تحقق من التوقيع.  
**⛔ لن يؤخذ:** مديرو حزم أخرى · نسخ Gist السحابي · NativeAOT/Avalonia · أيقونة النظام (Tray) · PWA (الأسباب في الوثيقة).

## خارطة الطريق

| الإصدار | المحتوى |
|---|---|
| ✅ 0.0.8 | مقارنة موثّقة + تحسينات منها + اختبارات |
| ✅ 0.0.9 | أداة جمع المثبّتات (`pack`) + التبعيات + تصدير قائمة التحديثات + أداة تدقيق الإصدارات |
| ✅ 0.0.9.1 | مقارِن إصدارات أُعيدت كتابته · أسماء القنوات مثل PowerToys · `audit-versions --csv` |
| ✅ 0.0.9.2 | التصدير إلى Excel/CSV/JSON (Installed وUpdates) · زر الثيم بضغطة واحدة |
| ✅ 0.1.0 | **المكتبة المحلية** (صفحة Local، تزايدية، فلاتر، تصدير، مطابقة مع Installed، و`wgo library`) · **ثلاث حزم تنزيل** (lite / standalone / full) |
| 🕓 0.2.0 | Bundles (تصدير/استيراد) · صفحة سجل العمليات |
| 🕓 لاحقاً | ترجمة (العربية أولاً) · جدولة · توقيع رقمي · نقطة استعادة · MSIX |

## النشر والتحديث عبر GitHub

- **إصدار:** ارفع `VERSION` ثم أنشئ إصداراً (Releases ← Create a new release) بوسم `v` + رقم `VERSION` تماماً (مثل `v0.0.9.2`)، فيبني الـ workflow ثلاث حزم (lite وstandalone وfull).
- **تحديث كامل:** ضع **ملف zip واحداً** في `incoming/` وادفعه؛ يستبدل الـ Action ملفات المشروع (عدا `.git` و`.github` و`incoming`) بعد بناء تحقق ([التفاصيل](incoming/README.md)).
- في إعدادات المستودع: Actions ← Workflow permissions ← **Read and write**.

## حالة التحقق (بصراحة)

| | |
|---|---|
| جُرّب فعلاً | بناء الكتالوج محلياً وعلى GitHub (Linux، دقيقة واحدة) · الفحص والمطابقة · الواجهة بالثيمين · التطبيق على جهاز ثانٍ · **الاختبارات الآلية 98/98 على Windows وLinux** · نشر الإصدار وتنزيل الكتالوج والتحقق من بصمته · `wgo stats/search/audit-versions` |
| لم يُجرَّب بعد | **التثبيت والإزالة الفعليان** · `Builder pack` والتنزيل · `wgo script` · تثبيت MSIX · اختبارات 0.0.9.1 (17 حالة) · وعلى Windows: زر الثيم وأزرار Export وصفحة **Local** كاملةً (اختبارات المصدِّرات وقارئات المكتبة نجحت في بيئة Linux تجريبية: 54 حالة، وقُرئ MSI حقيقي بُني بـ wixl، وفُتح ملف .xlsx في openpyxl وLibreOffice، لكن لم يُجرَّب بعد على CI ولا على Windows ولا في Excel) · **workflow الإصدار بحزمه الثلاث** (لم يُشغَّل قط) · قراءة مثبّتاتك الحقيقية المتنوعة (T8.4) |

## الأمان والترخيص

- التطبيق لا يحتوي أي كود شبكي. الجزء المتصل الوحيد هو أداة البناء (Builder).
- لا يُنفَّذ تثبيت/إزالة إلا بتأكيد صريح؛ كل عملية تُسجَّل في `logs/`.
- الترخيص: MIT (انظر `LICENSE`).

</div>
