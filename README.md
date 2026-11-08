# WingetOffline

**مدير برامج لويندوز 10/11 يعمل على جهاز بلا إنترنت، مبني على مستودع `winget-pkgs`.**
يبحث في كتالوج محلي شامل (نحو 16,000 حزمة)، ويكتشف برامج جهازك (المثبّتة والمحمولة) ويقارنها بأحدث الإصدارات، ويثبّت ويزيل ويحدّث من مثبّتات نقلتَها بالفلاش.

الإصدار: انظر `VERSION` (0.0.9.1) · [سجل التغييرات](CHANGELOG.md) · [المقارنة التفصيلية](docs/COMPARISON.md)

## كيف يعمل
```
جهاز متصل                                   الجهاز المعزول (بلا إنترنت)
─────────────                               ───────────────────────────
Builder: يبني catalog.wglib من winget-pkgs ─►  الواجهة / wgo:
مثبّتات (تنزّلها بنفسك الآن)               ─►   • تفحص البرامج وتقارنها بالكتالوج
                                                • تتعرّف على المثبّتات بالبصمة SHA256
                                                • تثبّت / تزيل / تحدّث (مع تأكيدك)
```

## البنية
| المشروع | الدور |
|---|---|
| `src/WingetOffline.Core` | المحلل والكتالوج (SQLite + FTS5) والفاحص والمطابقة ومحرك التثبيت |
| `src/WingetOffline.Builder` | **الجهاز المتصل فقط**: يبني `catalog.wglib`، ويجمع المثبّتات (`pack`) مع التحقق من SHA256 |
| `src/WingetOffline.Cli` | الأداة `wgo` لسطر الأوامر |
| `src/WingetOffline.App` | واجهة WPF (Discover · Installed · Updates · Installers · Settings) |
| `tests/WingetOffline.Tests` | اختبارات xUnit (63 دالة، 115 حالة) |

## البدء السريع
```powershell
# 1) على الجهاز المتصل: ابنِ الكتالوج
git clone --depth 1 https://github.com/microsoft/winget-pkgs
dotnet run -c Release --project src/WingetOffline.Builder -- .\winget-pkgs catalog.wglib

# 2) انقل catalog.wglib (+ مجلد مثبّتات) إلى الجهاز المعزول ثم شغّل الواجهة
dotnet run -c Release --project src/WingetOffline.App
#    Settings ← اختر catalog.wglib ← Load ← اختر مجلد المثبّتات ← Index now
```

## دورة تحديث جهاز معزول (الجديد في 0.0.9)
```
الجهاز المعزول                          الجهاز المتصل                               الجهاز المعزول
──────────────                          ─────────────                               ──────────────
Updates ← Export list  ──updates.json──► Builder pack --from-scan updates.json ──►  Settings ← Installers ← Index now
                                         (ينزّل + يتحقق SHA256 + التبعيات           Updates ← Update all
                                          + install-offline.ps1)                     (أو تشغيل install-offline.ps1)
```
```powershell
WingetOffline.Builder.exe pack --catalog catalog.wglib --from-scan updates.json --out D:\Pack
WingetOffline.Builder.exe pack --catalog catalog.wglib --ids Git.Git,VideoLAN.VLC --out D:\Pack --dry-run
```
يمكن أيضاً `--list ids.txt` (سطر لكل حزمة، `Id` أو `Id@Version`)، و`--arch`، و`--scope`، و`--no-deps`. الملف الذي لا يطابق SHA256 الكتالوج يُحذف ولا يدخل الحزمة.

## الكتالوج الجاهز (بلا بناء)
تبني GitHub Actions الكتالوج يومياً وتنشره في رابط ثابت (`.github/workflows/catalog.yml`):
`https://github.com/<USER>/WingetOffline/releases/download/catalog/catalog.wglib`
خطوات تجهيز جهاز جديد أو معزول: [docs/OFFLINE-SETUP.md](docs/OFFLINE-SETUP.md).

## المزايا
| الميزة | التفاصيل |
|---|---|
| كتالوج محلي | بحث فوري + فلاتر: الناشر، الرخصة، الوسم، نوع المثبّت، المعمارية، النطاق، تثبيت صامت، محمول، ProductCode |
| فحص الجهاز | السجل (32/64 بت، آلة/مستخدم) + محمولات winget + مجلداتك |
| مطابقة بدرجات ثقة | High/Medium/Low مع سبب كل مطابقة (ProductCode ← اسم ARP ← أمر ← اسم+ناشر ← تقريبي) |
| التعرّف على المثبّتات | بصمة SHA256 مقابل الكتالوج، بلا ملف بيانات إضافي |
| تثبيت وإزالة | MSI/WiX/Burn/Inno/NSIS/EXE/MSIX/ZIP/محمول؛ مفاتيح صامتة؛ UAC عند الحاجة؛ سجل لكل عملية |
| أمان التشغيل | تأكيد يعرض الأمر الكامل · تجربة جافة في `wgo` · إعادة المحاولة بواجهة البرنامج عند فشل الصامت |
| إدارة التحديثات | تحديث/تحديث الكل من مجلدك · تجاهل تحديثات حزمة · **تصدير قائمة التحديثات المطلوبة** · فلتر "لا يوجد أمر إزالة" |
| جمع المثبّتات | `Builder pack`: تنزيل مثبّت واحد لكل حزمة مع **تبعياتها**، تحقق SHA256، استئناف، `pack-manifest.json` + `install-offline.ps1` |
| دقة الإصدارات | يفضّل إصدار الاسم عند اعوجاج `DisplayVersion` · حالة "Newer than catalog" · `wgo audit-versions` يقارن ترتيب الإصدارات بتواريخ الإصدار الحقيقية |
| الأتمتة | `wgo` + مخرجات `--json` + `--export-updates` + توليد سكربت PowerShell مستقل (`wgo script`) |
| الوضع المحمول | ضع ملف `portable.txt` بجانب البرنامج فتُحفظ البيانات في مجلد `data` |

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
**⛔ لن يؤخذ:** مديرو حزم أخرى · نسخ Gist السحابي · NativeAOT/Avalonia · أيقونة النظام · PWA (الأسباب في الوثيقة).

## خارطة الطريق
| الإصدار | المحتوى |
|---|---|
| ✅ 0.0.8 | مقارنة موثّقة + تحسينات منها + اختبارات |
| ✅ 0.0.9 | أداة جمع المثبّتات (`pack`) + التبعيات + تصدير قائمة التحديثات + أداة تدقيق الإصدارات |
| 🕓 0.1.0 | Bundles (تصدير/استيراد) · صفحة سجل العمليات |
| 🕓 0.1.x | ترجمة (العربية أولاً) · جدولة · توقيع رقمي · نقطة استعادة · MSIX |

## النشر والتحديث عبر GitHub
- **إصدار:** ارفع `VERSION` ثم أنشئ إصداراً (Releases ← Create a new release) بوسم `v` + رقم `VERSION` تماماً (مثل `v0.0.9.1`)، فيبني الـ workflow ثلاثة ملفات exe.
- **تحديث كامل:** ضع **ملف zip واحداً** في `incoming/` وادفعه؛ يستبدل الـ Action ملفات المشروع (عدا `.git` و`.github` و`incoming`) بعد بناء تحقق ([التفاصيل](incoming/README.md)).
- في إعدادات المستودع: Actions ← Workflow permissions ← **Read and write**.

## حالة التحقق (بصراحة)
| | |
|---|---|
| جُرّب فعلاً | بناء الكتالوج محلياً وعلى GitHub (Linux، دقيقة واحدة) · الفحص والمطابقة · الواجهة بالثيمين · التطبيق على جهاز ثانٍ · **الاختبارات الآلية 98/98 على Windows وLinux** · نشر الإصدار وتنزيل الكتالوج والتحقق من بصمته · `wgo stats/search/audit-versions` |
| لم يُجرَّب بعد | **التثبيت والإزالة الفعليان** · `Builder pack` والتنزيل · `wgo script` · تثبيت MSIX · الاختبارات الجديدة (17 حالة أُضيفت في 0.0.9.1) |

## الأمان والترخيص
- التطبيق لا يحتوي أي كود شبكي. الجزء المتصل الوحيد هو أداة البناء (Builder).
- لا يُنفَّذ تثبيت/إزالة إلا بتأكيد صريح؛ كل عملية تُسجَّل في `logs/`.
- الترخيص: MIT (انظر `LICENSE`).
