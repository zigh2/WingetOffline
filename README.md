<div align="center">

# WingetOffline

**A package manager for Windows 10/11 machines that have no internet, built on the `winget-pkgs` repository.**

[![CI](https://github.com/zigh2/WingetOffline/actions/workflows/ci.yml/badge.svg)](https://github.com/zigh2/WingetOffline/actions/workflows/ci.yml)
![License](https://img.shields.io/badge/license-MIT-blue)
![Version](https://img.shields.io/badge/version-0.1.3-informational)

[English](#english) · [العربية](#arabic)

</div>

<a id="english"></a>

# English

WingetOffline searches a local catalog of about 16,000 packages, scans what is installed on your PC (installed and portable programs), compares it with the latest versions, and installs, removes and updates from installers you carried over on a flash drive.

Version: see `VERSION` (0.1.3) · [Changelog](CHANGELOG.md) · [Detailed comparison](docs/COMPARISON.md) · [Test plan](docs/TEST-PLAN.md)

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
| `tests/WingetOffline.Tests` | xUnit tests (238 cases) |

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

**Smallest download (no .NET inside the file)** — a single `.exe`, direct from GitHub. It needs the .NET 8 (or newer) runtime installed once:

| Program | Direct link (always the newest release) |
|---|---|
| The app | [`WingetOffline-lite.exe`](https://github.com/zigh2/WingetOffline/releases/latest/download/WingetOffline-lite.exe) |
| Command line (offline machine) | [`wgo-lite.exe`](https://github.com/zigh2/WingetOffline/releases/latest/download/wgo-lite.exe) |
| Builder (connected machine) | [`WingetOffline.Builder-lite.exe`](https://github.com/zigh2/WingetOffline/releases/latest/download/WingetOffline.Builder-lite.exe) |

Each of them is also attached with the **version in its name** (for example `WingetOffline-v0.1.3-win-x64-lite.exe`), and the version is in the file properties and printed by `wgo --version` / `WingetOffline.Builder --version`. The app shows its version at the top of the sidebar.

Each release also has three zip packages with the same three programs:

| Package | What is different | Needs .NET installed? |
|---|---|---|
| `…-win-x64-lite.zip` | .NET is **not** inside the files: the smallest zip | **Yes**: .NET 8+ Desktop Runtime (x64) |
| `…-win-x64-standalone.zip` | .NET inside each file, compressed | No |
| `…-win-x64-full.zip` | standalone **plus** `catalog.wglib` as it was at release time | No |

Real sizes are printed in the release run summary (Actions → Release) and are not guessed here. For reference, the earlier uncompressed self-contained build was about 68 MB for `wgo.exe`, 68 MB for the Builder and 160 MB for the app.

**`wgo` and `Builder` are command-line tools.** Double-clicking them opens a console window that closes at once, because they need arguments. Open PowerShell in their folder instead, for example `.\WingetOffline.Builder-lite.exe pack --catalog catalog.wglib --out D:\Pack --ids Git.Git` or `.\wgo-lite.exe stats catalog.wglib`. From 0.1.3 a double-click shows an explanation and waits for a key. If nothing at all appears even in PowerShell, the .NET runtime is missing: run `dotnet --list-runtimes`.

**Which .NET?** The programs are built for .NET 8. A newer runtime does not replace it automatically, which is why a machine with only .NET 10 used to be asked for .NET 8. From 0.1.2 the programs allow rolling forward to a newer major version (`RollForward=Major`), so .NET 9 or 10 works when .NET 8 is absent; .NET 8 is still preferred when present. The standalone and full packages need no .NET at all. A WPF app cannot be made both tiny and runtime-free.

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
| **Settings by page** | **Every option sits under the name of the page that uses it (Updates · Installed, Installers, Local, ...); Updates is the first page of the sidebar** |
| **Local library** | **Indexes your own exe/msi/msix/zip/7z/iso collection locally (new files only), with filters, export and matching against installed programs** |
| **Export** | **Installed, Updates and Local pages export to Excel (.xlsx), CSV and JSON. UTF-8 throughout, so Arabic and every other language are preserved** |
| **Import** | **Installed, Updates and Local each import only a file exported from that same page (Excel, CSV or JSON), as a read-only snapshot: a file from another page is rejected, and nothing in a snapshot can be installed, updated or uninstalled** |
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

### Importing

Each of the three pages has an **Import** button that opens a file previously exported from **that same page**. Every export carries its page (`kind`: `installed`, `updates` or `library`); CSV and Excel files are recognised by their columns. A file from another page is refused with a message that names the right page, for example *“…was exported from the Local page, so it cannot be imported on the Installed page”*.

* The result is a **read-only snapshot** with the page's usual table and filters, a banner, and a **Back to live data** button. Scanning, install, update, uninstall and ignore are disabled while it is shown, and an imported uninstall command is never run.
* It lets you look at another computer's installed list, a list of needed updates, or the file list of another drive, without touching the data of this computer.
* Files saved again by Excel are read too. Exports from older versions (13-column Installed CSV, kind-less updates JSON) still import.

### Local library (your own installers)

The **Local** page indexes your own collection (exe, msi, msix, zip, 7z, iso...). It reads only file headers, never runs anything, saves the result locally and re-reads only new or changed files (1,500 test files: first scan about 1.4 s, next scan 30 ms). Identified files show with many filters and can be exported. When a file matches an installed program, the **Installed** page shows it too, whether the library copy is newer, the same or older. File hashes are optional and configurable: **xxHash64 (default, the fastest), SHA256 or MD5**, over the **whole file**, or in **Quick mode** (files up to 30 MB are hashed completely, bigger ones only over their first 10 MB; both limits are editable). Everything computed is **remembered by size + modified time**, so a file that was moved or renamed is not read or hashed again. Only a full SHA256 can be matched with the winget catalog; xxHash64, MD5 and partial hashes are fingerprints for recognising files. In **Settings → Local library** you can search the **whole computer** (all fixed drives, optionally removable ones) and write **exclusions** in one text box: wildcard paths such as `C:\Windows\*`, extensions such as `.tmp`, plain text, or regular expressions (`re:...`); the defaults skip `C:\Windows\*` and `C:\Program Files\*`. Details, accuracy and limits: [docs/LIBRARY.md](docs/LIBRARY.md).

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
wgo library scan --all-drives --exclude ".tmp"             # search every fixed drive (default exclusions apply)
wgo library scan D:\Software --hash-mode quick --algo md5  # first 10 MB of files over 30 MB; MD5
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

Design decisions (why C#, what `wgo` is for, why `wgo` and `Builder` are separate): [docs/DECISIONS.md](docs/DECISIONS.md).

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
| ✅ 0.1.1 | Local library options in Settings: whole-computer search, removable drives, SHA256 matching, and one exclusions box (paths, extensions, text, regex) with defaults |
| ✅ 0.1.2 | Small direct `.exe` downloads (no .NET inside) · `RollForward=Major` (runs on .NET 9/10) · quick/partial hashing with SHA256 or MD5 · hash and identification cache that survives moves and renames |
| ✅ 0.1.3 | **Import** for Installed, Updates and Local (read-only snapshots; a file from another page is rejected) · **xxHash64** default hash · Settings grouped by page · version shown in the sidebar and in the release file names · Updates first · friendly double-click for `wgo`/`Builder` |
| 🕓 0.2.0 | Bundles (export/import) · operation history page |
| 🕓 0.3.0 | **Fleet management over the local network** (admin console + agents, password pairing, encrypted channel, per-device changing ports, remote install/remove/update with audit) — see [docs/FLEET.md](docs/FLEET.md) |
| 🕓 later | Translation (Arabic first) · scheduling · digital signature · restore point · MSIX |

## Planned: v0.3 — Fleet management over the local network

> **Status: design proposal, not implemented.** Full design and security requirements: [docs/FLEET.md](docs/FLEET.md).

One administrator sees **every program installed on every machine of the local network** and manages them (install, remove, update) from one place, with no internet and no external server.

* **Separate, opt-in components.** An `Agent` on each managed machine and an `Admin` console. The current app and `wgo` stay free of network code; the new components are disabled by default and refuse any non-private address.
* **Pairing with a shared password.** The password is typed on both sides during a short pairing window. It is used only inside a password-authenticated key exchange (PAKE), so it never crosses the network and cannot be brute-forced from a captured session. After pairing, each machine keeps pinned public keys and the password is no longer used.
* **Encrypted, mutually authenticated channel** with forward secrecy and replay protection, built on reviewed libraries (no home-made cryptography).
* **Changing ports.** After pairing, admin and device agree — inside the encrypted channel — on a random port, **different for each device**, rotated periodically. This reduces noise and random scanning; it is not a security layer by itself. Security comes from authentication, encryption and authorization.
* **What the admin sees:** all installed programs per machine with the same matching and update information as the Installed page, and a search across all machines.
* **What the admin manages:** install, remove, update — **no free-form commands**. Only a fixed set of operations on named packages with a verified SHA256, run by the same install engine; roles (read-only / manage), optional approval on the device, a tamper-evident audit log, per-device unpairing and rate limits.
* **Independent security review** is a release requirement, because remote management is remote code execution by design.

## Publishing and updating through GitHub

- **Release:** raise `VERSION`, then create a release (Releases → Create a new release) with the tag `v` + the exact `VERSION` (for example `v0.0.9.2`). The workflow builds the three packages (lite, standalone, full).
- **Full update:** put **one zip file** in `incoming/` and push it. The Action replaces the project files (except `.git`, `.github` and `incoming`) after a verification build ([details](incoming/README.md)).
- In the repository settings: Actions → Workflow permissions → **Read and write**.
- Files under `.github/` are **never** updated by the `incoming/` zip. `release.yml` is therefore deliberately thin and calls `scripts/release.ps1` (which the zip does update); replace `release.yml` by hand only when it itself changes.

## Verification status (honest)

| | |
|---|---|
| Tried for real | Catalog build locally and on GitHub (Linux, about one minute) · scan and matching · the UI in both themes · applying it on a second machine · **automated tests 98/98 on Windows and Linux** · release publishing and catalog download with fingerprint check · `wgo stats/search/audit-versions` |
| Not tried yet | **Real install and uninstall** · `Builder pack` and downloading · `wgo script` · MSIX install · the tests added in 0.0.9.1 (17 cases) · on Windows: the theme button, the Export buttons and the whole **Local** page and the new Settings options (the exporters' and library tests pass in a Linux sandbox: 123 cases, an MSI read from a real file built with wixl, .xlsx opened in openpyxl and LibreOffice, but not yet on CI, on Windows or in Excel) · **the three-package release workflow** (never run) · reading your real, varied installers (T8.4) |

## Security and license

- The app and `wgo` contain no network code. The only connected part is the build tool (Builder). The planned v0.3 fleet features will live in separate, opt-in components so this stays true for the offline tools.
- Nothing is installed or removed without explicit confirmation; every operation is logged in `logs/`.
- License: MIT (see `LICENSE`).

<br>

---

<a id="arabic"></a>

<div dir="rtl" lang="ar">

# العربية

**مدير برامج لويندوز 10/11 يعمل على جهاز بلا إنترنت، مبني على مستودع `winget-pkgs`.**

يبحث في كتالوج محلي يضم نحو 16,000 حزمة، ويفحص برامج جهازك (المثبّتة والمحمولة) ويقارنها بأحدث الإصدارات، ثم يثبّت ويزيل ويحدّث من مثبّتات نقلتَها بالفلاش.

الإصدار: انظر `VERSION` (0.1.3) · [سجل التغييرات](CHANGELOG.md) · [المقارنة التفصيلية](docs/COMPARISON.md) · [خطة الاختبار](docs/TEST-PLAN.md)

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
| `tests/WingetOffline.Tests` | اختبارات xUnit (238 حالة) |

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

**أصغر تنزيل (بلا .NET داخل الملف)**: ملف `.exe` واحد مباشرة من GitHub، ويحتاج تثبيت .NET 8 (أو أحدث) مرة واحدة:

| البرنامج | الرابط المباشر (دائماً آخر إصدار) |
|---|---|
| التطبيق | [`WingetOffline-lite.exe`](https://github.com/zigh2/WingetOffline/releases/latest/download/WingetOffline-lite.exe) |
| سطر الأوامر (الجهاز المعزول) | [`wgo-lite.exe`](https://github.com/zigh2/WingetOffline/releases/latest/download/wgo-lite.exe) |
| Builder (الجهاز المتصل) | [`WingetOffline.Builder-lite.exe`](https://github.com/zigh2/WingetOffline/releases/latest/download/WingetOffline.Builder-lite.exe) |

وكل واحد منها مرفق أيضاً **باسم يحمل رقم الإصدار** (مثل `WingetOffline-v0.1.3-win-x64-lite.exe`)، والإصدار في خصائص الملف ويطبعه `wgo --version` و`WingetOffline.Builder --version`. ويعرض التطبيق إصداره أعلى القائمة الجانبية.

ولكل إصدار أيضاً ثلاث حزم zip بالبرامج الثلاثة نفسها:

| الحزمة | الفرق | تحتاج .NET مثبّتاً؟ |
|---|---|---|
| `…-win-x64-lite.zip` | .NET **ليس** داخل الملفات: أصغر zip | **نعم**: .NET 8+ Desktop Runtime (x64) |
| `…-win-x64-standalone.zip` | .NET داخل كل ملف ومضغوط | لا |
| `…-win-x64-full.zip` | standalone **مع** `catalog.wglib` كما كان وقت الإصدار | لا |

الأحجام الفعلية تُطبع في ملخص تشغيل الإصدار (Actions ← Release) ولا أخمّنها هنا. للمقارنة: البناء السابق (self-contained غير مضغوط) كان نحو 68MB لـ `wgo.exe` و68MB لـ Builder و160MB للتطبيق.

**`wgo` و`Builder` أداتا سطر أوامر.** النقر المزدوج عليهما يفتح نافذة أوامر تُغلق فوراً لأنهما تحتاجان وسائط. افتح PowerShell في مجلدهما بدلاً من ذلك، مثل `.\WingetOffline.Builder-lite.exe pack --catalog catalog.wglib --out D:\Pack --ids Git.Git` أو `.\wgo-lite.exe stats catalog.wglib`. ومن 0.1.3 يعرض النقر المزدوج شرحاً وينتظر ضغطة مفتاح. وإن لم يظهر شيء حتى في PowerShell فالسبب غياب .NET: شغّل `dotnet --list-runtimes`.

**أي نسخة .NET؟** البرامج مبنية لـ .NET 8، ونسخة أحدث لا تحل محلها تلقائياً؛ لذلك كان الجهاز الذي فيه .NET 10 فقط يُطلب منه .NET 8. من 0.1.2 تسمح البرامج بالانتقال إلى إصدار رئيسي أحدث (`RollForward=Major`)، فيعمل .NET 9 أو 10 عند غياب 8، ويبقى 8 مفضَّلاً إن وُجد. أما حزمتا standalone وfull فلا تحتاجان .NET إطلاقاً. وتطبيق WPF لا يمكن أن يكون صغيراً جداً وبلا runtime في آن واحد.

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
| **Settings بحسب الصفحة** | **كل خيار تحت اسم الصفحة التي تستعمله (Updates · Installed وInstallers وLocal ...)، وصفحة Updates هي الأولى في القائمة الجانبية** |
| **المكتبة المحلية** | **تفهرس مجموعة exe/msi/msix/zip/7z/iso عندك محلياً (الجديد فقط) بفلاتر وتصدير ومطابقة مع البرامج المثبّتة** |
| **التصدير** | **صفحات Installed وUpdates وLocal تصدّر إلى Excel (.xlsx) وCSV وJSON. الترميز UTF-8 في كل الصيغ، فتُحفظ العربية وكل اللغات الأخرى** |
| **الاستيراد** | **كل من Installed وUpdates وLocal يستورد فقط ملفاً صُدّر من الصفحة نفسها (Excel أو CSV أو JSON) كلقطة للعرض فقط: ملف صفحة أخرى يُرفض، ولا يمكن تثبيت أو تحديث أو إزالة أي شيء من اللقطة** |
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

### الاستيراد

لكل صفحة من الصفحات الثلاث زر **Import** يفتح ملفاً صُدّر سابقاً من **الصفحة نفسها**. كل تصدير يحمل صفحته (`kind`: `installed` أو `updates` أو `library`)، وملفات CSV وExcel تُعرف بأعمدتها. ملف صفحة أخرى يُرفض برسالة تسمّي الصفحة الصحيحة، مثل *"…was exported from the Local page, so it cannot be imported on the Installed page"*.

* الناتج **لقطة للعرض فقط** بجدول الصفحة وفلاترها المعتادة، مع شريط تنبيه وزر **Back to live data**. يتعطل الفحص والتثبيت والتحديث والإزالة والتجاهل ما دامت اللقطة معروضة، ولا يُنفَّذ أبداً أمر إزالة مستورد.
* تتيح لك الاطلاع على قائمة برامج جهاز آخر، أو قائمة تحديثات مطلوبة، أو قائمة ملفات قرص آخر، دون المساس ببيانات هذا الجهاز.
* تُقرأ أيضاً الملفات التي حفظها Excel من جديد، وتبقى تصديرات الإصدارات الأقدم مقبولة (CSV لـ Installed بـ 13 عموداً، وJSON للتحديثات بلا kind).

### المكتبة المحلية (مثبّتاتك أنت)

صفحة **Local** تفهرس مجموعتك (exe وmsi وmsix وzip و7z وiso...). تقرأ رؤوس الملفات فقط ولا تشغّل شيئاً، وتحفظ النتيجة محلياً ولا تعيد قراءة إلا الجديد أو المتغيّر (1500 ملف تجريبي: الفحص الأول نحو 1.4 ثانية والتالي 30 ميلي ثانية). تظهر الملفات المعرَّفة بفلاتر كثيرة وتُصدَّر، وعند تطابق ملف مع برنامج مثبّت تعرضه صفحة **Installed** أيضاً، سواء كانت نسخة المكتبة أحدث أو مطابقة أو أقدم. البصمات اختيارية وقابلة للضبط: **xxHash64 (الافتراضية والأسرع) أو SHA256 أو MD5**، على **الملف كاملاً** أو في **الوضع السريع** (الملفات حتى 30MB تُبصَم كاملة، وما فوقها على أول 10MB فقط؛ وكلا الحدّين قابل للتعديل). وكل ما يُحسب **يُحفظ بالحجم + تاريخ التعديل**، فالملف الذي نُقل أو أُعيدت تسميته لا يُقرأ ولا يُبصَم ثانيةً. وحدها بصمة SHA256 الكاملة تطابق كتالوج winget؛ أما xxHash64 وMD5 والبصمة الجزئية فهي بصمات تعريف للملفات. ومن **Settings ← Local library** يمكنك البحث في **كامل الكمبيوتر** (كل الأقراص الثابتة، ومعها القابلة للإزالة إن شئت) وكتابة **الاستثناءات** في مربع نص واحد: مسارات بعلامات مثل `C:\Windows\*` أو لواحق مثل `.tmp` أو نص عادي أو تعابير نمطية (`re:...`)؛ والافتراضي يتخطى `C:\Windows\*` و`C:\Program Files\*`. التفاصيل والدقة والحدود: [docs/LIBRARY.md](docs/LIBRARY.md).

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
wgo library scan --all-drives --exclude ".tmp"             # بحث في كل الأقراص الثابتة (تُطبَّق الاستثناءات الافتراضية)
wgo library scan D:\Software --hash-mode quick --algo md5  # أول 10MB من الملفات فوق 30MB؛ MD5
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

قرارات التصميم (لماذا C#، وما دور `wgo`، ولماذا `wgo` و`Builder` منفصلان): [docs/DECISIONS.md](docs/DECISIONS.md).

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
| ✅ 0.1.1 | خيارات المكتبة المحلية في Settings: البحث في كامل الكمبيوتر، والأقراص القابلة للإزالة، ومطابقة SHA256، ومربع استثناءات واحد (مسارات ولواحق ونصوص وregex) بقيم افتراضية |
| ✅ 0.1.2 | تنزيل `.exe` مباشر وصغير (بلا .NET داخله) · `RollForward=Major` (يعمل على .NET 9/10) · بصمة سريعة/جزئية بـ SHA256 أو MD5 · ذاكرة بصمات وتعريف تنجو من النقل وإعادة التسمية |
| ✅ 0.1.3 | **الاستيراد** لصفحات Installed وUpdates وLocal (لقطات للعرض فقط؛ ملف صفحة أخرى يُرفض) · بصمة **xxHash64** الافتراضية · Settings مقسّمة بحسب الصفحة · الإصدار في القائمة الجانبية وفي أسماء ملفات الإصدار · Updates أولاً · نقر مزدوج ودّي لـ `wgo` و`Builder` |
| 🕓 0.2.0 | Bundles (تصدير/استيراد) · صفحة سجل العمليات |
| 🕓 0.3.0 | **إدارة الأجهزة عبر الشبكة المحلية** (لوحة مسؤول + Agents، اقتران بكلمة سر، قناة مشفرة، منفذ متغير لكل جهاز، تثبيت/إزالة/تحديث عن بُعد مع سجل تدقيق) — انظر [docs/FLEET.md](docs/FLEET.md) |
| 🕓 لاحقاً | ترجمة (العربية أولاً) · جدولة · توقيع رقمي · نقطة استعادة · MSIX |

## مخطط: الإصدار 0.3 — إدارة الأجهزة عبر الشبكة المحلية

> **الحالة: مقترح تصميم، لم يُنفَّذ.** التصميم الكامل ومتطلبات الأمان: [docs/FLEET.md](docs/FLEET.md).

مسؤول واحد يرى **كل البرامج المثبّتة على كل أجهزة الشبكة المحلية** ويديرها (تثبيت وإزالة وتحديث) من مكان واحد، دون إنترنت ودون خادم خارجي.

* **مكوّنات منفصلة واختيارية.** `Agent` على كل جهاز مُدار ولوحة `Admin`. التطبيق الحالي و`wgo` يبقيان بلا أي كود شبكي؛ والمكوّنات الجديدة معطّلة افتراضياً وترفض أي عنوان غير خاص.
* **اقتران بكلمة سر مشتركة.** تُكتب الكلمة عند الطرفين في نافذة اقتران قصيرة، وتُستخدم فقط داخل اتفاق مفاتيح بكلمة سر (PAKE)، فلا تعبر الشبكة ولا يمكن تخمينها من اتصال مسجَّل. بعد الاقتران يحتفظ كل جهاز بمفاتيح عامة مثبّتة ولا تُستخدم الكلمة ثانيةً.
* **قناة مشفرة بمصادقة متبادلة** مع سرية أمامية وحماية من إعادة الإرسال، ببناء على مكتبات مراجَعة (لا تشفير من تأليفنا).
* **منافذ متغيرة.** بعد الاقتران يتفق المسؤول والجهاز — داخل القناة المشفرة — على منفذ عشوائي **مختلف لكل جهاز** يُدوَّر دورياً. هذا يقلل الضجيج والمسح العشوائي لكنه ليس طبقة أمان بحد ذاته؛ الأمان من المصادقة والتشفير والتفويض.
* **ما يراه المسؤول:** كل البرامج المثبّتة على كل جهاز بمعلومات المطابقة والتحديث نفسها في صفحة Installed، مع بحث عبر كل الأجهزة.
* **ما يديره المسؤول:** تثبيت وإزالة وتحديث — **بلا أوامر حرة**. مجموعة ثابتة من العمليات على حزم مسمّاة بـ SHA256 موثَّق ينفّذها محرك التثبيت نفسه؛ مع أدوار (قراءة فقط / إدارة) وموافقة اختيارية على الجهاز وسجل تدقيق يكشف التلاعب وإلغاء اقتران لكل جهاز وحدود معدل.
* **مراجعة أمنية مستقلة** شرط للإصدار، لأن الإدارة عن بُعد تعني تنفيذ تعليمات عن بُعد بطبيعتها.

## النشر والتحديث عبر GitHub

- **إصدار:** ارفع `VERSION` ثم أنشئ إصداراً (Releases ← Create a new release) بوسم `v` + رقم `VERSION` تماماً (مثل `v0.0.9.2`)، فيبني الـ workflow ثلاث حزم (lite وstandalone وfull).
- **تحديث كامل:** ضع **ملف zip واحداً** في `incoming/` وادفعه؛ يستبدل الـ Action ملفات المشروع (عدا `.git` و`.github` و`incoming`) بعد بناء تحقق ([التفاصيل](incoming/README.md)).
- في إعدادات المستودع: Actions ← Workflow permissions ← **Read and write**.
- ملفات `.github/` **لا** يحدّثها zip الخاص بـ `incoming/` أبداً. لذلك `release.yml` رقيق عمداً ويستدعي `scripts/release.ps1` (الذي يحدّثه الـ zip)؛ استبدل `release.yml` يدوياً فقط عندما يتغيّر هو نفسه.

## حالة التحقق (بصراحة)

| | |
|---|---|
| جُرّب فعلاً | بناء الكتالوج محلياً وعلى GitHub (Linux، دقيقة واحدة) · الفحص والمطابقة · الواجهة بالثيمين · التطبيق على جهاز ثانٍ · **الاختبارات الآلية 98/98 على Windows وLinux** · نشر الإصدار وتنزيل الكتالوج والتحقق من بصمته · `wgo stats/search/audit-versions` |
| لم يُجرَّب بعد | **التثبيت والإزالة الفعليان** · `Builder pack` والتنزيل · `wgo script` · تثبيت MSIX · اختبارات 0.0.9.1 (17 حالة) · وعلى Windows: زر الثيم وأزرار Export وصفحة **Local** كاملةً وخيارات Settings الجديدة (اختبارات المصدِّرات والمكتبة نجحت في بيئة Linux تجريبية: 123 حالة، وقُرئ MSI حقيقي بُني بـ wixl، وفُتح ملف .xlsx في openpyxl وLibreOffice، لكن لم يُجرَّب بعد على CI ولا على Windows ولا في Excel) · **workflow الإصدار بحزمه الثلاث** (لم يُشغَّل قط) · قراءة مثبّتاتك الحقيقية المتنوعة (T8.4) |

## الأمان والترخيص

- التطبيق و`wgo` لا يحتويان أي كود شبكي. الجزء المتصل الوحيد هو أداة البناء (Builder). وميزات الإدارة عبر الشبكة المخططة للإصدار 0.3 ستكون في مكوّنات منفصلة واختيارية فيبقى هذا الضمان قائماً للأدوات غير المتصلة.
- لا يُنفَّذ تثبيت/إزالة إلا بتأكيد صريح؛ كل عملية تُسجَّل في `logs/`.
- الترخيص: MIT (انظر `LICENSE`).

</div>
