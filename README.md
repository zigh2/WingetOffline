# WingetOffline

**مدير برامج لويندوز 10/11 يعمل على جهاز بلا إنترنت، مبني على مستودع `winget-pkgs`.**
يبحث في كتالوج محلي شامل (نحو 15,855 حزمة)، ويكتشف برامج جهازك (المثبّتة والمحمولة) ويقارنها بأحدث الإصدارات، ويثبّت ويزيل ويحدّث من مثبّتات نقلتَها بالفلاش.

الإصدار: انظر `VERSION` (0.0.8) · [سجل التغييرات](CHANGELOG.md) · [المقارنة التفصيلية](docs/COMPARISON.md)

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
| `src/WingetOffline.Builder` | يبني `catalog.wglib` (على الجهاز المتصل فقط) |
| `src/WingetOffline.Cli` | الأداة `wgo` لسطر الأوامر |
| `src/WingetOffline.App` | واجهة WPF (Discover · Installed · Updates · Installers · Settings) |
| `tests/WingetOffline.Tests` | اختبارات xUnit (44 دالة) |

## البدء السريع
```powershell
# 1) على الجهاز المتصل: ابنِ الكتالوج
git clone --depth 1 https://github.com/microsoft/winget-pkgs
dotnet run -c Release --project src/WingetOffline.Builder -- .\winget-pkgs catalog.wglib

# 2) انقل catalog.wglib (+ مجلد مثبّتات) إلى الجهاز المعزول ثم شغّل الواجهة
dotnet run -c Release --project src/WingetOffline.App
#    Settings ← اختر catalog.wglib ← Load ← اختر مجلد المثبّتات ← Index now
```

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
| إدارة التحديثات | تحديث/تحديث الكل من مجلدك · **تجاهل تحديثات حزمة** · فلتر "لا يوجد أمر إزالة" |
| الأتمتة | `wgo` + مخرجات `--json` + توليد سكربت PowerShell مستقل (`wgo script`) |
| الوضع المحمول | ضع ملف `portable.txt` بجانب البرنامج فتُحفظ البيانات في مجلد `data` |

## `wgo` (سطر الأوامر)
```
wgo stats | search | scan | index | install | uninstall | script
wgo index   catalog.wglib D:\Installers            # أي ملف يطابق أي حزمة؟   (--json)
wgo install catalog.wglib app.exe [--run]          # تجربة جافة؛ --run للتنفيذ
wgo uninstall WinSCP [--run]
wgo script  catalog.wglib D:\Installers            # install-offline.ps1 مستقل
wgo scan    catalog.wglib --updates --json
```

## المقارنة مع المشاريع المشابهة
> التفاصيل الكاملة (وصفاً وكوداً) في [docs/COMPARISON.md](docs/COMPARISON.md). لم يُنسخ أي كود؛ أفكار فقط.

| المشروع | ما هو | ما جلبناه | الحالة |
|---|---|---|---|
| [UniGetUI](https://github.com/Devolutions/UniGetUI) | واجهة موحّدة لمديري الحزم (MIT) | تجاهل تحديثات حزمة · وضع محمول · مخرجات JSON · اختبارات + CI | ✅ |
| | | Bundles · سجل العمليات · جدولة · ترجمة | 🕓 |
| [winget-cli](https://github.com/microsoft/winget-cli) | المدير الرسمي (`winget download`) | نموذج جمع المثبّتات مع التبعيات | 🕓 0.0.9 |
| [Chocolatey GUI](https://github.com/chocolatey/ChocolateyGUI) | واجهة Chocolatey | رموز الخروج الصالحة (1605/1614 عند الإزالة) | ✅ |
| [Scoop](https://github.com/ScoopInstaller/Scoop) | مثبّت محمول بسطر الأوامر | تجميد الحزم (hold) · الوضع المحمول | ✅ |
| [Winget-AutoUpdate](https://github.com/Romanitho/Winget-AutoUpdate) | تحديث تلقائي مجدول | قوائم الاستبعاد | ✅ |
| | | جدولة · مستويات إشعارات · خطّافات · تدوير سجلات | 🕓 |
| [Bulk Crap Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller) | إزالة جماعية | إعادة المحاولة بواجهة البرنامج · كود 1627 لـ NSIS · رسائل أخطاء مفهومة · dry-run · فلتر "مزيل غير صالح" | ✅ |
| | | عمود توقيع رقمي · نقطة استعادة · MSIX/Store | 🕓 |
| winstall | اختيار برامج وتوليد سكربت | توليد سكربت تثبيت (`wgo script`) | ✅ |
| WingetBatch (PowerShell) | `Export-WingetOffline` | مستودع غير متصل + سكربت + تحقق هاش | ✅ سكربت · 🕓 الجمع |
| WSUS Offline Update | تحديثات Windows دون اتصال | فصل المولّد عن المطبِّق (Builder / التطبيق) | ✅ مفهوم |

**أين نتميّز:** تصميم لجهاز بلا إنترنت (كتالوج محلي + تعرّف بالبصمة + مطابقة تشرح نفسها).
**أين ينقصنا:** أكثر من مصدر حزم، MSIX/Store، جدولة، سجل عمليات، Bundles، ترجمة، تحقق من التوقيع.
**⛔ لن يؤخذ:** مديرو حزم أخرى · نسخ Gist السحابي · NativeAOT/Avalonia · أيقونة النظام · PWA (الأسباب في الوثيقة).

## خارطة الطريق
| الإصدار | المحتوى |
|---|---|
| ✅ 0.0.8 | مقارنة موثّقة + تحسينات منها (انظر أعلاه) + اختبارات |
| 🕓 0.0.9 | أداة جمع المثبّتات على الجهاز المتصل (SHA256 + التبعيات) · مراجعة مقارِن الإصدارات |
| 🕓 0.1.0 | Bundles (تصدير/استيراد) · صفحة سجل العمليات |
| 🕓 0.1.x | ترجمة (العربية أولاً) · جدولة · توقيع رقمي · نقطة استعادة · MSIX |

## النشر والتحديث عبر GitHub
- **إصدار:** ارفع `VERSION` ثم `git tag v0.0.8 && git push origin v0.0.8` فيبني الـ workflow ملفات الإصدار.
- **تحديث كامل:** ضع **ملف zip واحداً** في `incoming/` وادفعه؛ يستبدل الـ Action ملفات المشروع (عدا `.git` و`.github` و`incoming`) بعد بناء تحقق ([التفاصيل](incoming/README.md)).
- في إعدادات المستودع: Actions ← Workflow permissions ← **Read and write**.

## الأمان والترخيص
- التطبيق لا يحتوي أي كود شبكي. الجزء المتصل الوحيد هو أداة البناء (Builder).
- لا يُنفَّذ تثبيت/إزالة إلا بتأكيد صريح؛ كل عملية تُسجَّل في `logs/`.
- الترخيص: MIT (انظر `LICENSE`).
