# سجل الاعتماديات والتراخيص — P02

تاريخ المراجعة: 2026-10-29. المصدر هو nuspec للحزم المستعادة من NuGet الرسمي، مع قراءة نص ترخيص الحزم ذات ترخيص الملف عند تقييمها. هذه مراجعة تقنية أولية، ولا تختار ترخيص المنتج أو تمنح موافقة تجارية على حزم مشروطة.

## ما ثبت في التأسيس

الإصدارات المباشرة في Directory.Packages.props، والشجرة والبصمات في ملفات packages.lock.json السبعة. روجعت بيانات الترخيص لـ45 هوية حزمة مستعادة: 33 تعلن MIT و11 تعلن Apache-2.0؛ xunit.abstractions/2.0.3 يعرض رابط ترخيص فقط، وجرى فتح [رابط xUnit الذي تشير إليه الحزمة](https://raw.githubusercontent.com/xunit/xunit/master/license.txt)، وفيه Apache-2.0 مع استثناءات MIT موثقة. يجب الاحتفاظ بإشعارات الأطراف الثالثة المناسبة عند تجهيز توزيع P12؛ هذا الجرد لا يستبدل النصوص القانونية.

فحص NuGet للحزم المباشرة والمتعدية لم يرجع حزم ثغرات معروفة وقت التشغيل. التدقيق وTLS لم يعطلا. المرجع النهائي للإصدارات المثبتة هو القفل، لا رقم إصدار مكتوب في وثيقة.

| هوية الحزمة المستعادة | بيان الترخيص |
|---|---|
| CommunityToolkit.Mvvm/8.4.2 | MIT |
| Microsoft.CodeCoverage/18.11.0 | MIT |
| Microsoft.Extensions.Configuration.Abstractions/10.0.14 | MIT |
| Microsoft.Extensions.Configuration.Binder/10.0.14 | MIT |
| Microsoft.Extensions.Configuration.CommandLine/10.0.14 | MIT |
| Microsoft.Extensions.Configuration.EnvironmentVariables/10.0.14 | MIT |
| Microsoft.Extensions.Configuration.FileExtensions/10.0.14 | MIT |
| Microsoft.Extensions.Configuration.Json/10.0.14 | MIT |
| Microsoft.Extensions.Configuration.UserSecrets/10.0.14 | MIT |
| Microsoft.Extensions.Configuration/10.0.14 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions/10.0.14 | MIT |
| Microsoft.Extensions.DependencyInjection/10.0.14 | MIT |
| Microsoft.Extensions.Diagnostics.Abstractions/10.0.14 | MIT |
| Microsoft.Extensions.Diagnostics/10.0.14 | MIT |
| Microsoft.Extensions.FileProviders.Abstractions/10.0.14 | MIT |
| Microsoft.Extensions.FileProviders.Physical/10.0.14 | MIT |
| Microsoft.Extensions.FileSystemGlobbing/10.0.14 | MIT |
| Microsoft.Extensions.Hosting.Abstractions/10.0.14 | MIT |
| Microsoft.Extensions.Hosting/10.0.14 | MIT |
| Microsoft.Extensions.Logging.Abstractions/10.0.14 | MIT |
| Microsoft.Extensions.Logging.Configuration/10.0.14 | MIT |
| Microsoft.Extensions.Logging.Console/10.0.14 | MIT |
| Microsoft.Extensions.Logging.Debug/10.0.14 | MIT |
| Microsoft.Extensions.Logging.EventLog/10.0.14 | MIT |
| Microsoft.Extensions.Logging.EventSource/10.0.14 | MIT |
| Microsoft.Extensions.Logging/10.0.14 | MIT |
| Microsoft.Extensions.Options.ConfigurationExtensions/10.0.14 | MIT |
| Microsoft.Extensions.Options/10.0.14 | MIT |
| Microsoft.Extensions.Primitives/10.0.14 | MIT |
| Microsoft.NET.Test.Sdk/18.11.0 | MIT |
| Microsoft.TestPlatform.ObjectModel/18.11.0 | MIT |
| Microsoft.TestPlatform.TestHost/18.11.0 | MIT |
| Serilog.Extensions.Hosting/10.0.0 | Apache-2.0 |
| Serilog.Extensions.Logging/10.0.0 | Apache-2.0 |
| Serilog.Sinks.File/7.0.0 | Apache-2.0 |
| Serilog/4.3.0 | Apache-2.0 |
| System.Diagnostics.EventLog/10.0.14 | MIT |
| xunit.abstractions/2.0.3 | Apache-2.0 — تحقق من رابط ترخيص xUnit |
| xunit.analyzers/1.18.0 | Apache-2.0 |
| xunit.assert/2.9.3 | Apache-2.0 |
| xunit.core/2.9.3 | Apache-2.0 |
| xunit.extensibility.core/2.9.3 | Apache-2.0 |
| xunit.extensibility.execution/2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio/3.1.5 | Apache-2.0 |
| xunit/2.9.3 | Apache-2.0 |

حزم SDK ومرجع Windows Desktop لها إشعارات Microsoft و.NET الخاصة بها؛ ليست كلها PackageReference في جرد NuGet أعلاه.

## الحزم الموثقة للمراحل التالية

| الحزمة | النسخة التي فُحصت بياناتها | الترخيص المعلن | القرار الحالي |
|---|---|---|---|
| EF Core.SqlServer وEF Core.Design | 10.0.14 | MIT | مرشحان متوافقان مع .NET10 لـP03؛ غير مثبتين وغير مختبرين مع SQL Server بعد |
| Dapper | 2.1.89 | Apache-2.0 | إضافة عند تنفيذ التقارير/استعلامات SQL |
| FluentValidation | 12.2.0 | Apache-2.0 | إضافة مع أول Validators حقيقية |
| BCrypt.Net-Next | 4.2.1 | MIT | إضافة مع خدمة المستخدمين |
| ClosedXML | 0.105.1 | MIT | إضافة عند Excel |
| ZXing.Net | 0.16.11 | Apache-2.0 | إضافة مع الباركود |
| AutoMapper | 16.2.0 | ملف LICENSE.md: RPL-1.5 أو اتفاق ترخيص بديل من Lucky Penny | غير مثبت؛ لا يُفترض أنه MIT أو مناسب تلقائياً لمنتج مغلق. يحدد المسار الترخيصي قبل دمج التحويل، دون استبدال الحزمة المقررة بصمت |
| QuestPDF | 2026.9.1 | ترخيص Community أو Professional/Enterprise وفق الأهلية | غير مثبت؛ النص الفعلي راجعناه من الحزمة، لا عبارة «مجاني لكل استخدام تجاري» القديمة. يحدد الاستحقاق قبل دمج PDF؛ لا تفترض أهلية المطور أو العميل |

المصادر للحزم: https://api.nuget.org/v3-flatcontainer/{package-id}/{version}/{package-id}.nuspec، ونص LICENSE.md داخل nupkg لـAutoMapper وQuestPDF. روابط التوضيح الرسمية: [AutoMapper](https://luckypennysoftware.com/license)، [QuestPDF](https://www.questpdf.com/pricing).

خطوط العربية والطابعات وبيئة Windows تختبر عند الطباعة. لا تنزيل لخطوط Windows مملوكة أو إعادة توزيع لها ضمن P02. إبقاء هذه الحزم خارج الحل الآن يؤجل دمجها فقط، ولا يلغي متطلبات REQ-005 وREQ-050 وREQ-052.
