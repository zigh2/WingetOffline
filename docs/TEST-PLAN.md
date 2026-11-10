# خطة الاختبار على جهاز حقيقي

الأولوية: 🔴 لا بد منه · 🟡 مهم · 🟢 اختياري. أرقام الاختبارات (T1.1 …) للإشارة إليها عند الإبلاغ.
عند أي نتيجة مخالفة للمتوقع أرسل: رقم الاختبار + نص الرسالة أو لقطة الشاشة (+ ملف السجل إن وُجد في `%LOCALAPPDATA%\WingetOffline\logs`).

## قبل البدء
- **جهاز الاختبار:** ويندوز 10 مع حساب مدير. للمرحلتين 6 و7 يفضّل **نقطة استعادة أو آلة افتراضية**.
- **لا تجرّب** `--run` أو أزرار Install/Uninstall/Update على برنامج مهم. البرنامجان التجريبيان المقترحان: **WinSCP** (مثبّت Inno، صغير، سهل الإزالة) و**Rufus** (ملف محمول).
- **الملفات:** `WingetOffline.exe` و`wgo.exe` و`WingetOffline.Builder.exe` (من ملف الإصدار)، و`catalog.wglib` مبني بنسخة 0.0.9.
- المراحل 1 و4 تحتاج إنترنت؛ المراحل 2 و3 و5 و6 تعمل دون إنترنت.

## ما أُنجز حتى الآن (حُدِّث 2026-11-08)
| الاختبار | النتيجة |
|---|---|
| T1.1 رفع `.github` · T1.2 تطبيق الحزمة عبر `incoming` | ✅ |
| T1.3 الاختبارات على Linux | ✅ 98/98 |
| T1.4 CI على Windows | ✅ بناء الحل كاملاً بصفر أخطاء + **98/98** (نُفِّذ اختباراً Windows فعلياً) |
| T1.5 بناء الكتالوج (force) | ✅ 16,031 حزمة، 191,863 إصداراً، **دقيقة واحدة و4 ثوانٍ**، 56 MB؛ T1.5ب: البصمة تطابق (`True`) |
| T1.7 إصدار `v0.0.9` وتنزيل الملفات الثلاثة | ✅ |
| T3.1 `wgo stats` · T3.2 `search powertoys` · T3.3 `audit-versions` | ✅ نُفّذت وحُلّلت (انظر CHANGELOG 0.0.9.1) |
| ما تبقى | المرحلة 2 (الواجهة) · T3.4 · المراحل 4 و5 و6 و7 · T1.6 · الاختبارات الجديدة (135 حالة: 17 من 0.0.9.1 و20 من 0.0.9.2) على CI بعد رفع 0.0.9.2 |

**جديد في 0.0.9.1:** T3.3ب `wgo audit-versions catalog.wglib --csv audit.csv` ثم ارفع الملف لي (اختياري، لتحليل كل الصفوف الـ412).

## المرحلة 1 — خط GitHub (لا يحتاج جهاز تطوير) · نحو 30 دقيقة
| # | الأولوية | الخطوات | المتوقع |
|---|---|---|---|
| T1.1 | 🔴 | ارفع مجلد `.github` من `WingetOffline-github-folder.zip` (Add file ← Upload files) | يُرفع الملف؛ قد يفشل CI (أحمر) لأن الكود لم يُحدَّث بعد، وهذا متوقع |
| T1.2 | 🔴 | ارفع `WingetOffline-0.0.9.zip` إلى `incoming/` | في Actions ينجح "Apply incoming package"؛ commit من البوت `chore: apply incoming package (v0.0.9)`؛ يختفي الـ zip؛ يظهر `docs/` و`tests/` |
| T1.3 | 🟡 | في سجل التشغيل السابق افتح خطوة "Unit tests" | `Passed: 203` تقريباً (كانت 98 في 0.0.9 و115 في 0.0.9.1 و135 في 0.0.9.2 و169 في 0.1.0 و194 في 0.1.1) بعد 0.0.9.2 (كانت 98 في 0.0.9، و115 في 0.0.9.1). على Linux بعض الاختبارات تتخطى نفسها فتُحسب ناجحة. إن فشل اختبار فأرسل اسمه ورسالته |
| T1.4 | 🔴 | Actions ← CI ← **Run workflow** | أخضر: بناء الحل كاملاً + **98 حالة اختبار على Windows**. هذا أهم اختبار للكود |
| T1.5 | 🔴 | Actions ← Build catalog ← Run workflow ← فعّل **force** | أخضر؛ Release باسم `catalog` فيه `catalog.wglib` و`catalog-meta.json` وفيه `"builder_version":"0.0.9"`. سجّل المدة |
| T1.6 | 🟡 | شغّل Build catalog مرة ثانية **بدون** force | في سجل خطوة الفحص: `changed=false` وتُتخطى بقية الخطوات |
| T1.7 | 🔴 | Releases ← Draft a new release ← وسم جديد `v0.0.9` ← Publish | ينجح workflow "Release"؛ الملف المرفق يحوي `WingetOffline.exe` و`wgo.exe` و`WingetOffline.Builder.exe` |

## المرحلة 2 — التطبيق: قراءة فقط (آمن) · نحو 30 دقيقة
| # | الأولوية | الخطوات | المتوقع |
|---|---|---|---|
| T2.1 | 🔴 | شغّل `WingetOffline.exe` ثم Settings ← Browse ← الكتالوج ← Load | العنوان يعرض رقم `VERSION` الحالي مع `-r1` (للإصدار 0.1.2: `0.1.2-r1`)؛ أول تحميل ~دقيقة (فك + تحقق SHA256)، الثاني أسرع؛ الشريط الجانبي يعرض عدد الحزم وتاريخ البناء الجديد |
| T2.2 | 🔴 | Discover: ابحث `vlc` ثم `7 zip` ثم `c++` ثم كلمة غير موجودة؛ جرّب كل فلتر (Installer type وArchitecture وScope وSilent وPortable وProductCode) والترتيب وClear filters | نتائج فورية (أقل من ثانية)؛ العداد منطقي؛ "Tag" يُطابق كاملاً |
| T2.3 | 🔴 | اختر 5 حزم وراقب تفاصيلها | الإصدارات الأحدث أولاً؛ المثبّتات مع الرابط؛ خانة **Depends on**. أخبرني بأسماء الحزم التي ظهرت لها تبعيات |
| T2.4 | 🔴 | Installed بعد الفحص: Python وEverything وPowerToys | Python: Installed 3.14.6 مع Update available إن كان أقدم من الكتالوج · Everything: "Newer than catalog" · PowerToys: أخبرني مطابَق أم لا |
| T2.5 | 🟡 | اختر برنامجاً له تحديث ← **Ignore updates** | يختفي من Updates؛ في Installed يظهر بحالة "Update ignored" وبفلتر "Ignored updates"؛ يبقى بعد إعادة التشغيل؛ "Stop ignoring" يعيده |
| T2.6 | 🟡 | Installed ← Status = "No uninstall command" | أخبرني بما يظهر (يجب أن تكون برامج بلا أمر إزالة) |
| T2.7 | 🔴 | Updates ← **Export list** ← احفظ `updates-needed.json` وافتحه | `"format": 1`، و`items` فيها packageId وversion وinstalled وname؛ لا مطابقات Low ولا حزم متجاهَلة |
| T2.8 | 🟡 | Settings ← Theme: Light/Dark/System؛ وجرّب تكبير 125% و150% | لا نص مقصوص، شريط العنوان يتبع الثيم، أشرطة التمرير رفيعة |
| T2.9 | 🟡 | وضع محمول: ملف فارغ `portable.txt` بجانب exe ← أعد التشغيل | Settings ← About: `Data folder: ...\data (portable mode)`؛ يُنشأ `settings.json` و`cache` هناك. احذف الملف لتعود البيانات إلى LocalAppData |
| T2.10 | 🔴 | افصل الشبكة وكرّر T2.1–T2.3؛ ثم `Get-NetTCPConnection -OwningProcess (Get-Process WingetOffline).Id` | كل شيء يعمل دون إنترنت؛ والأمر لا يعيد أي اتصال |
| T2.11 | 🟢 | حمّل كتالوجاً أحدث | في `%LOCALAPPDATA%\WingetOffline\cache` يبقى مجلد واحد فقط |
| T2.12 | 🔴 | زر الثيم أسفل القائمة الجانبية: اضغط مرة واحدة ثم مرة ثانية | يتبدّل الثيم فوراً مع كل ضغطة (بلا إعادة تشغيل)، وتتغير الأيقونة والاسم (Dark/Light). مع إعداد System يظهر `(auto)`. غيّر الثيم من Settings ثم اضغط الزر: يبقى الاثنان متزامنين. أعد التشغيل: يبقى آخر ثيم |
| T2.13 | 🔴 | Installed ← Export ← Excel، ثم افتح الملف في Excel | تظهر 13 عموداً بعناوين عريضة وصف مثبّت وفلتر؛ الأسماء العربية/الصينية/الروسية (إن وجدت) سليمة؛ عدد الصفوف = ما تراه بعد الفلاتر. جرّب فلتراً ثم صدّر ثانية |
| T2.14 | 🔴 | Installed ← Export ← CSV، افتحه في Excel بالنقر المزدوج ثم عبر Data ← From Text/CSV | العربية سليمة؛ وإن ظهر كل شيء في عمود واحد فهذا فاصل القوائم في Excel عندك (`;`)، أخبرني بذلك |
| T2.15 | 🔴 | Installed ← Export ← JSON ثم على الجهاز المتصل: `Builder pack --from-scan installed.json --catalog catalog.wglib --out D:\Pack --dry-run` | تُعرض خطة بالحزم المطابقة (Medium+) فقط، دون الحزم غير المطابقة أو الضعيفة |
| T2.16 | 🟡 | Updates ← Export list بالصيغ الثلاث | JSON كما كان؛ وExcel وCSV يحويان 4 أعمدة (Package ID وName وInstalled وLatest). أبقِ الملف مفتوحاً في Excel ثم صدّر فوقه: يجب أن تظهر رسالة خطأ واضحة لا انهيار |

## المرحلة 3 — `wgo` للقراءة · 10 دقائق
| # | الأولوية | الأمر | المتوقع |
|---|---|---|---|
| T3.1 | 🔴 | `wgo stats catalog.wglib` | أعداد الحزم والإصدارات وأنواع المثبّتات |
| T3.2 | 🔴 | `wgo search catalog.wglib powertoys --limit 5` | **أرسل لي الناتج** (لحسم مطابقة PowerToys) |
| T3.3 | 🔴 | `wgo audit-versions catalog.wglib --limit 40` | **أرسل لي الناتج كاملاً** (نسبة تعارض ترتيب الإصدارات مع التواريخ) |
| T3.4 | 🟡 | `wgo scan catalog.wglib --updates --json > scan.json` ثم `Get-Content scan.json \| ConvertFrom-Json` | JSON صالح؛ لا نص يسبقه |
| T3.5 | 🟡 | `wgo scan catalog.wglib --export-updates u.json` | رسالة "Update list written"؛ الملف بنفس بنية T2.7 |

## المرحلة 4 — جمع المثبّتات `Builder pack` (جهاز متصل) · 20–40 دقيقة
| # | الأولوية | الخطوات | المتوقع |
|---|---|---|---|
| T4.1 | 🔴 | `WingetOffline.Builder.exe pack --catalog catalog.wglib --ids WinSCP.WinSCP,Rufus.Rufus --out D:\PackTest --dry-run` | جدول الخطة (الحزمة والإصدار والنوع والمعمارية والسبب) دون تنزيل |
| T4.2 | 🔴 | الأمر نفسه بدون `--dry-run` | `OK` لكل ملف؛ ملفات باسم `WinSCP.WinSCP_<إصدار>_x64.exe`؛ و`pack-manifest.json` و`install-offline.ps1` |
| T4.3 | 🔴 | أعد تشغيل الأمر نفسه | تظهر "already downloaded and verified" (استئناف دون إعادة تنزيل) |
| T4.4 | 🔴 | أضف بايتاً إلى أحد الملفات المنزَّلة ثم أعد الأمر | يعيد تنزيله ويتحقق؛ لا يقبل ملفاً تالفاً |
| T4.5 | 🟡 | `--from-scan updates-needed.json` (من T2.7) مع `--dry-run` ثم فعلياً لحزمتين | الخطة تطابق قائمة التحديثات |
| T4.6 | 🟡 | `--list ids.txt` فيه `Git.Git@<إصدار قديم موجود>` وسطر باسم خاطئ | تحذيران واضحان؛ لا انهيار |
| T4.7 | 🟡 | حزمة لها تبعيات (من T2.3) | الخطة تعرض "dependency of X"؛ وفي `install-offline.ps1` تأتي التبعية **قبل** الحزمة |
| T4.8 | 🟡 | `--arch x86` ثم `--scope user` | يتغير المثبّت المختار أو يظهر تحذير |
| T4.9 | 🟡 | افصل الإنترنت أثناء التنزيل | `download failed` بعد 3 محاولات؛ رمز الخروج 4؛ لا يبقى ملف `.part` |

## المرحلة 5 — التعرّف على المثبّتات (بلا إنترنت) · 10 دقائق
| # | الأولوية | الخطوات | المتوقع |
|---|---|---|---|
| T5.1 | 🔴 | انسخ `PackTest` إلى جهاز الاختبار، Settings ← Installers ← Browse ← **Index now** | صفحة Installers: كل الملفات **Recognised** مع الحزمة والإصدار والنوع والمعمارية صحيحة |
| T5.2 | 🔴 | ضع في المجلد ملف exe عشوائياً ثم Index now | **Unknown**؛ والمعاينة تطلب اختيار النوع يدوياً |
| T5.3 | 🟡 | `wgo index catalog.wglib D:\PackTest --json` | JSON صالح |
| T5.4 | 🟡 | أعد الفهرسة | أسرع بكثير (كاش البصمات) |
| T5.5 | 🟢 | مسار فيه مسافات أو أحرف عربية | يعمل |

## المرحلة 6 — التثبيت والإزالة (الأخطر) · 60–90 دقيقة · **خذ نقطة استعادة أولاً**
**أ) CLI بتجربة جافة (لا يغيّر شيئاً)**
| # | الأولوية | الأمر | المتوقع |
|---|---|---|---|
| T6.1 | 🔴 | `wgo install catalog.wglib <ملف WinSCP>` | يعرض `... /SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` وElevation: needed وعبارة "DRY RUN" |
| T6.2 | 🔴 | الأمر نفسه لملف MSI (مثل 7-Zip MSI) | `msiexec.exe /i "..." /quiet /norestart /L*v ...` |
| T6.3 | 🔴 | `wgo script catalog.wglib D:\PackTest` ثم افتح `install-offline.ps1` | مسارات نسبية (`$PSScriptRoot`)؛ `#Requires -RunAsAdministrator`؛ التبعيات أولاً |

**ب) CLI فعلي**
| # | الأولوية | الأمر | المتوقع |
|---|---|---|---|
| T6.4 | 🔴 | `wgo install ... <WinSCP> --run` ← `y` | UAC ← Exit code 0 ← البرنامج مثبّت (قائمة Start وApps) ← سجل في `logs` |
| T6.5 | 🔴 | `wgo uninstall WinSCP` (جاف) ثم `--run` | يعرض `unins000.exe ... /VERYSILENT ...`؛ بعد التنفيذ يُزال وExit 0 |
| T6.6 | 🟡 | `wgo install ... <Rufus> --run` | مجلد `...\WingetOffline\Portable\Rufus.Rufus` فيه الملف، وتنبيه أنه لا PATH |
| T6.7 | 🟡 | اضغط No على نافذة UAC | رسالة "Elevation (UAC) was cancelled" ورمز 1223 |
| T6.8 | 🟡 | `--interactive` | يظهر مثبّت WinSCP بواجهته |
| T6.9 | 🟡 | `--no-elevate` على مثبّت يحتاج مدير | فشل برسالة مفهومة (740 أو 5)، دون تعليق |

**ج) من الواجهة**
| # | الأولوية | الخطوات | المتوقع |
|---|---|---|---|
| T6.10 | 🔴 | Installers ← اختر WinSCP ← راجع "Command that will run" ← **Install** | نافذة تأكيد بالأمر والصلاحيات ← Yes ← UAC ← نافذة نتيجة بـ Exit code ومسار السجل ← إعادة فحص تلقائية ← يظهر في Installed |
| T6.11 | 🔴 | Discover ← WinSCP قبل التثبيت وبعده | قبل: Install مفعّل وUninstall معطّل. بعد: العكس |
| T6.12 | 🔴 | Installed ← WinSCP ← **Uninstall** | تأكيد ← إزالة ← يختفي بعد إعادة الفحص |
| T6.13 | 🔴 | **دورة التحديث:** ثبّت إصداراً أقدم (حمّله بـ `winget download --version <قديم>`)، افحص، ثم ضع الأحدث في مجلد المثبّتات وIndex | Updates يعرضه؛ تظهر "Offline installer ready"؛ **Update** ← تأكيد ← بعده الحالة Up to date بالإصدار الجديد |
| T6.14 | 🟡 | **Update all** مع تطبيقين قديمين | تأكيد واحد؛ تنفيذ متتابع؛ ملخص بالنجاح/الفشل؛ ثم إعادة فحص |
| T6.15 | 🟡 | تجاهل حزمة قديمة ثم Update all | لا تُحدَّث المتجاهَلة |
| T6.16 | 🟡 | **إعادة المحاولة:** Installers ← ملف NSIS أو Inno ← Manual override بنوع خاطئ أو مفاتيح خاطئة لإجبار فشل الصامت | عند الفشل: "Retry showing the program's own window?" ← Yes يفتح المثبّت بواجهته |
| T6.17 | 🟡 | اضغط Install مرتين بسرعة | الثانية: "Another operation is still running" |
| T6.18 | 🟡 | Uninstall لبرنامج بلا أمر صامت معروف (مثل Intel Graphics) | تحذير Unknown في نافذة التأكيد؛ **اضغط No** إن كان البرنامج مهماً |
| T6.19 | 🟢 | مثبّت MSIX/Appx إن وُجد | يُثبَّت عبر PowerShell |
| T6.20 | 🟢 | مثبّت يطلب إعادة التشغيل (3010) | رسالة "a reboot is required" |

## المرحلة 7 — المتانة
| # | الأولوية | الاختبار | المتوقع |
|---|---|---|---|
| T7.1 | 🟡 | مجلد مثبّتات كبير (أكثر من 200 ملف) | الواجهة لا تتجمد؛ سجّل الزمن |
| T7.2 | 🟡 | حساب مستخدم عادي | البحث والفحص يعملان؛ التثبيت يطلب UAC |
| T7.3 | 🟡 | Windows Defender وSmartScreen | أخبرني برسائل التحذير إن ظهرت |
| T7.4 | 🟢 | Windows 11 أو DPI 100% / 175% | لا خلل في الشكل |
| T7.5 | 🟢 | أغلق التطبيق أثناء عملية | لا يعلق ولا يتلف الإعدادات |

## المرحلة 8 — المكتبة المحلية (صفحة Local) · 30–45 دقيقة
> اختبرها على مكتبتك الحقيقية. اقرأ [docs/LIBRARY.md](LIBRARY.md) أولاً (دقيقتان).

| # | الأولوية | الاختبار | المتوقع |
|---|---|---|---|
| T8.1 | 🔴 | Local ← Add folder ← اختر مجلداً فيه عشرات الملفات من أنواع مختلفة | يبدأ الفحص تلقائياً؛ الواجهة لا تتجمد؛ شريط الحالة يعرض التقدم؛ تظهر الملفات. **سجّل: عدد الملفات والزمن** |
| T8.2 | 🔴 | أعد Scan new files فوراً | ينتهي في ثوانٍ: `0 new, 0 changed, N unchanged`. **سجّل الزمن** |
| T8.3 | 🔴 | انسخ ملفاً جديداً إلى المجلد وعدّل ملفاً آخر ثم Scan new files | `1 new, 1 changed` والبقية unchanged |
| T8.4 | 🔴 | اختر 10 ملفات متنوعة (msi وexe بمثبّتات مختلفة وmsix وzip وiso و7z) وقارن الاسم والإصدار والناشر بالحقيقة | MSI/MSIX دقيقة؛ exe غالباً صحيحة. **أرسل لي كل ملف أخطأ فيه (اسم الملف + ما ظهر + الصحيح)**: هذا أهم اختبار هنا |
| T8.5 | 🔴 | جرّب كل الفلاتر: النوع، Identification، Installed on this PC، المعمارية، الحجم، المجلد، والنص | كل فلتر يغيّر القائمة فوراً وعدد الصفوف في السطر الأعلى يتطابق |
| T8.6 | 🔴 | Export ← Excel، CSV، JSON (مع فلتر مفعّل) | يحوي الصفوف الظاهرة فقط؛ العربية سليمة؛ الحجم رقم قابل للترتيب في Excel والتاريخ تاريخ |
| T8.7 | 🔴 | بعد تحميل كتالوج وفحص الجهاز: Installed ← عمود Library وفلتر "My library" | تظهر `Newer x` / `Same` / `Older x` لبرامج لها ملفات عندك؛ اختر برنامجاً: تفاصيل المكتبة وزر Show file in folder يعملان |
| T8.8 | 🔴 | في Local: فلتر Installed on this PC ← Newer than installed | ملفات أحدث من المثبّت فعلاً. تحقق من 3 منها يدوياً |
| T8.9 | 🟡 | افصل قرصاً خارجياً فيه مجلد من المكتبة ثم Scan new files | رسالة "Not reachable (kept as is)" وتبقى سجلاته؛ أعد توصيله وScan: لا إعادة قراءة |
| T8.10 | 🟡 | Remove على أحد المجلدات | تختفي ملفاته من الصفحة؛ الملفات على القرص كما هي |
| T8.11 | 🟡 | Cancel أثناء فحص مجلد كبير | يتوقف بلا انهيار؛ ما أُنجز يُحفظ؛ المرة التالية تكمل |
| T8.12 | 🟡 | علّم "Also compute SHA256" وScan على مجلد فيه مثبّتات تعرفها من winget (مع كتالوج محمَّل) | بعض الملفات تصير "winget match" مع Winget ID؛ **سجّل زمن البصمات لأكبر ملف** |
| T8.13 | 🟡 | `wgo library scan <مجلد> --export lib.xlsx` ثم `wgo library list --type msi` ثم `wgo library match catalog.wglib --newer` | الأوامر الثلاثة تعمل؛ المكتبة هي نفسها التي في الواجهة؛ الفحص من سطر الأوامر لا يمحو مجلدات أخرى |
| T8.14 | 🟢 | مجلد فيه ملفات تالفة أو محمية بكلمة سر أو مقفلة | لا انهيار؛ تظهر بثقة منخفضة أو فلتر "Had read errors" |
| T8.15 | 🟢 | ملف ISO ضخم (أكثر من 4GB) | الفحص سريع (لا يقرأ إلا الرأس)؛ الاسم من اسم الملف واسم القرص في الملاحظات |
| T8.16 | 🔴 | Settings ← Local library: اترك الاستثناءات الافتراضية، فعّل "Search the whole computer"، ثم Scan library now | نص "The scan covers: C:\, D:\ ..." يطابق أقراصك؛ الفحص يعمل بلا تجمّد؛ شريط الحالة يعرض عدد المجلدات المستثناة. **سجّل: المدة الكلية وعدد الملفات التي وجدها وعدد المجلدات المستثناة** (هذا أول اختبار فحص قرص كامل) |
| T8.17 | 🔴 | أعد Scan library now بعد انتهاء الأول | يسرع كثيراً (الملفات غير المتغيّرة لا تُقرأ)؛ لكن التعداد نفسه يستغرق وقتاً. **سجّل الزمن** |
| T8.18 | 🔴 | في "Try a path against the rules" جرّب: `C:\Windows\System32` و`c:\PROGRAM FILES\Git\x.exe` و`D:\Software\vlc.exe` | الأولان "Excluded by line ..." والثالث "Not excluded." |
| T8.19 | 🔴 | أضف سطر `.iso` ثم `re:\\(temp\|cache)\\` وجرّب مسارات مثل `D:\x\win.ISO` و`C:\Users\me\Temp\a.exe` و`C:\Users\me\Templates\a.exe` | الأول والثاني مستثنيان، الثالث لا. ثم Scan: ملفات iso تختفي من Local |
| T8.20 | 🟡 | اكتب `re:(` في سطر | سطر الحالة يذكر "Line N: invalid regular expression" والبقية تعمل |
| T8.21 | 🟡 | أضف مجلداً في Local يقع داخل مسار مستثنى (مثل `C:\Program Files\MyInstallers`) ثم Scan | شريط الحالة يقول إن المجلد مستثنى بقاعدة كذا |
| T8.22 | 🟡 | فعّل "Also search removable drives" مع USB فيه مثبّتات ثم Scan، ثم افصل USB وScan ثانية | ملفاته تبقى في المكتبة بعد الفصل ولا تُحذف |
| T8.23 | 🟡 | ملفات في OneDrive (Files On-Demand غير محمَّلة) ضمن نطاق الفحص | لا يبدأ OneDrive تنزيلها؛ شريط الحالة يذكر "online-only cloud file(s) left unread" |
| T8.24 | 🟡 | أغلق التطبيق وافتحه | تبقى كل الخيارات والاستثناءات كما كتبتها |
| T8.25 | 🟢 | `wgo library scan --all-drives --exclude ".tmp"` ثم `wgo library scan D:\Software --no-default-exclusions` | الأول يفحص كل الأقراص بالاستثناءات الافتراضية؛ الثاني لا يمحو سجلات الأقراص الأخرى |
| T8.26 | 🔴 | Settings ← File hashes ← Quick (30 / 10 MB) ثم Scan library now على مجلد فيه ملفات صغيرة وكبيرة | الصغيرة تحصل على SHA256 كامل، والكبيرة على "Partial hash" مع عمود "SHA256 of the first 10 MB" في Export. **سجّل: الزمن مقارنة بـ Full على المجلد نفسه** |
| T8.27 | 🔴 | **انقل** ملفاً (أو مجلداً) إلى مجلد آخر داخل المكتبة وأعد تسمية ملف آخر ثم Scan new files | شريط الحالة: "N recognised without re-reading" و"hash(es) reused"؛ لا إعادة حساب؛ الاسم والإصدار كما كانا (من المحتوى) |
| T8.28 | 🟡 | غيّر الخوارزمية إلى MD5 ثم Scan | يحسب MD5 فقط للناقص؛ لا يلمس SHA256 المحسوب؛ Export يعرض العمودين |
| T8.29 | 🟡 | عدّل ملفاً (غيّر حجمه) ثم Scan | يُعاد فحصه وبصمته (لا يؤخذ من الذاكرة) |
| T8.30 | 🟡 | حذف `library-cache.json` ثم Scan new files | يعمل؛ يعيد بناء الذاكرة بلا أخطاء |

## المرحلة 9 — حزم الإصدار الثلاث · 20 دقيقة
> بعد إنشاء إصدار `v0.1.2` (وبعد استبدال `.github/workflows/release.yml` يدوياً، فهو لا يتحدّث عبر `incoming/`) (Releases ← Create a new release). افتح تشغيل Release في Actions وانظر "Package sizes" في الملخص.

| # | الأولوية | الاختبار | المتوقع |
|---|---|---|---|
| T9.1 | 🔴 | نجاح workflow الإصدار وظهور ست أصول: ثلاثة zip (`-lite` و`-standalone` و`-full`) وثلاثة exe مباشرة | كلها موجودة. إن بقيت أحجام قديمة (68/68/160MB) فمعناه أن `release.yml` القديم لم يُستبدل. **سجّل أحجامها وحجم كل exe من ملخص التشغيل** |
| T9.2 | 🔴 | شغّل `WingetOffline.exe` من `standalone` على جهاز بلا .NET (أو افترض ذلك) | يعمل مباشرة؛ **سجّل زمن أول تشغيل** (الضغط يبطئه ثوانيَ) |
| T9.3 | 🔴 | شغّل `lite` على جهاز فيه .NET 8 Desktop Runtime | يعمل ويفتح بسرعة |
| T9.4 | 🟡 | شغّل `lite` على جهاز **بلا** .NET 8 | تظهر رسالة ويندوز تدل على تنزيل .NET (هذا متوقع؛ أخبرني بنصها) |
| T9.5 | 🟡 | في `full`: Settings ← اختر `catalog.wglib` من المجلد نفسه ← Load | يُحمَّل؛ يظهر عدد الحزم وتاريخ البناء |
| T9.6 | 🟢 | `wgo.exe stats catalog.wglib` من `lite` ثم من `standalone` | النتيجة نفسها |
| T9.7 | 🔴 | بعد الإصدار: افتح `https://github.com/zigh2/WingetOffline/releases/latest/download/WingetOffline-lite.exe` في المتصفح | ينزّل الملف مباشرة. **سجّل حجمه**، وكذلك `wgo-lite.exe` و`WingetOffline.Builder-lite.exe` |
| T9.8 | 🔴 | شغّل `WingetOffline-lite.exe` على جهاز فيه **.NET 10 فقط** | يعمل ولا يطلب .NET 8 (بفضل RollForward=Major)؛ إن طلبه أخبرني بنص الرسالة |
| T9.9 | 🟡 | الإصدار ليس "Pre-release" وصفحة Releases تعرض الأصول: 3 exe + 3 zip | نعم |

## الحد الأدنى إن ضاق الوقت (≈ ساعة)
T1.1 → T1.2 → T1.4 → T1.5 → T1.7 ثم T3.2 + T3.3 ثم T2.1 → T2.4 → T2.7 ثم T4.1 → T4.3 ثم T6.1 → T6.4 → T6.5 → T6.10.

## أماكن أتوقع فيها مشاكل (للانتباه)
1. workflow الإصدار: نشر WPF كملف واحد لم يُجرَّب.
2. بناء الكتالوج على Linux: لم يُجرَّب (قد تختلف المدة والمسارات).
3. طلب UAC عبر `runas`: عدم ظهور مخرجات المثبّت في السجل (مقصود) وقد تظهر حالات حافة.
4. `Update all` المتتابع وتزامن إعادة الفحص.
5. بعض مثبّتات EXE بلا مفتاح صامت في المانيفست: ستعمل بواجهتها.
6. تعارض ترتيب الإصدارات (يكشفه T3.3).
7. قراءة MSI وexe من مثبّتات حقيقية متنوعة (T8.4): جُرّبت على ملفات تجريبية فقط.
8. ضغط الملف الواحد (`EnableCompressionInSingleFile`) قد يبطئ أول تشغيل (T9.2).
9. جلب الكتالوج للحزمة `full` داخل workflow الإصدار (يتطلب وجود إصدار باسم `catalog`).
