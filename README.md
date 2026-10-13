# HisabTak — نظام محاسبي ومخزون متكامل | Integrated Accounting & Inventory System

> نظام محاسبة ومخزون سطح مكتب (Desktop) متكامل، مبني بـ **C# / WPF / SQL Server**، يغطي المحاسبة الكاملة، المخزون متعدد المستودعات، المبيعات والمشتريات، نقطة البيع (كاشير)، الرواتب، والتخليص الجمركي — مصمم خصيصاً للمحلات التجارية، الجملة، والمطاعم.

---

## 📝 Description (مختصر لـ GitHub)

> A full-featured **local desktop ERP** for small-to-medium businesses (retail shops, wholesalers, restaurants) built with **C# 12, WPF, .NET 8, and SQL Server**. Handles double-entry accounting, multi-warehouse inventory, sales/purchase invoicing, POS/cashier, payroll, and customs clearance — with a clean, modern UI replacing a legacy WinForms system.

---

## ✨ المميزات الرئيسية | Key Features

- 📊 **محاسبة كاملة**: شجرة حسابات هرمية (ترقيم عشري)، قيود يومية متوازنة تلقائياً، حسابات ختامية (متاجرة، أرباح وخسائر، ميزانية، ميزان مراجعة)
- 📦 **مخزون متعدد المستودعات**: تعدد الوحدات لكل صنف، تتبع الصلاحية والدُفعات، تسعير متعدد الشرائح (مستهلك/عميل/جملة/موزع)
- 🧾 **مستندات تجارية موحّدة**: فواتير بيع/شراء (نقد وآجل)، مردودات، عروض أسعار، أوامر شراء، فواتير تخليص جمركي، تحويلات مخزنية
- 💰 **سندات مالية**: قبض، صرف، إقفال صناديق يومي
- 🖥️ **نقطة بيع (POS/كاشير)**: واجهة لمس، فواتير متعددة متوازية، دعم قطاع المطاعم (طباعة مطبخ)
- 👥 **رواتب وموظفين**: رواتب، خصميات، سلف، أيام غياب
- 🔐 **صلاحيات وأدوار شجرية**: أدوار جاهزة (كاشير/محاسب/مدير) + صلاحيات فردية دقيقة
- 🕵️ **سجل تدقيق شامل (Audit Log)**: تتبّع كل عملية عرض/إضافة/تعديل/حذف
- ♻️ **سلة محذوفات مركزية**: استرجاع أي كيان محذوف دون فقدان بيانات
- 🖨️ **طباعة احترافية**: فواتير (كبيرة/صغيرة حرارية) وتقارير PDF عبر QuestPDF
- 📥 **استيراد/تصدير Excel**
- 💾 **نسخ احتياطي محلي اختياري** بجدولة حرة يحدّدها المستخدم
- 🏢 **دعم تعدد الفروع/الشركات**: كل عميل/فرع بقاعدة بيانات مستقلة تماماً

---

## 🛠️ التقنيات المستخدمة | Tech Stack

| الطبقة | التقنية |
|---|---|
| اللغة والمنصة | C# 12, .NET 8 |
| الواجهة | WPF + CommunityToolkit.Mvvm (MVVM) |
| قاعدة البيانات | SQL Server |
| الوصول للبيانات | Entity Framework Core (CRUD) + Dapper (تقارير ثقيلة) |
| التقارير المالية | Stored Procedures / SQL Views |
| الطباعة وPDF | QuestPDF |
| Excel | ClosedXML |
| الباركود | ZXing.Net |
| التحقق من الصحة | FluentValidation |
| Object Mapping | AutoMapper |
| Logging | Serilog |
| الاختبارات | xUnit |
| الأمان | BCrypt.Net (Hashing لكلمات المرور) |

---

## 🏗️ المعمارية | Architecture

مبني على **Clean Architecture** (فصل تام بين الطبقات):

```
HisabTak.sln
├── src/
│   ├── HisabTak.Domain          # الكيانات ومنطق الأعمال الأساسي (لا يعتمد على أي شيء)
│   ├── HisabTak.Application     # حالات الاستخدام والخدمات (Use Cases)
│   ├── HisabTak.Infrastructure  # EF Core, Dapper, الطباعة, Excel, النسخ الاحتياطي
│   ├── HisabTak.WPF             # الواجهة (Views/ViewModels)
│   └── HisabTak.Shared          # أدوات وثوابت مشتركة
└── tests/
    ├── HisabTak.Domain.Tests
    └── HisabTak.Application.Tests
```

**قاعدة الاعتماد**: `WPF → Application → Domain` | `Infrastructure → Application + Domain`

### قرارات معمارية بارزة
- **شاشة مستندات موحّدة واحدة** تخدم كل أنواع الفواتير (بيع/شراء/مردودات/عروض أسعار/تخليص جمركي) بدل تكرار 7 شاشات متطابقة
- **Strategy Pattern** للترحيل المحاسبي التلقائي (`IPostingStrategy`) — قيد متوازن مضمون هيكلياً لكل عملية مالية
- **Soft Delete + أرشيف JSON مركزي** لسلة محذوفات آمنة وقابلة للاسترجاع الكامل
- **لا تجميع بين العملات** بالتقارير المالية — كل عملة بتقاريرها المستقلة

---

## 🚀 البدء السريع | Getting Started

### المتطلبات
- .NET 8 SDK أو أحدث
- SQL Server (Express أو أعلى) — محلي بالكامل، لا يتطلب اتصال إنترنت للتشغيل
- Visual Studio 2022 أو JetBrains Rider

### التثبيت
```bash
git clone https://github.com/<your-username>/HisabTak.git
cd HisabTak
dotnet restore
dotnet build

# تطبيق الـ Migrations لإنشاء قاعدة البيانات
dotnet ef database update --project src/HisabTak.Infrastructure --startup-project src/HisabTak.WPF

# تشغيل التطبيق
dotnet run --project src/HisabTak.WPF
```

### إعداد سلسلة الاتصال
عدّل `appsettings.json` بمشروع `HisabTak.WPF`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=.;Database=HisabTak;Trusted_Connection=True;TrustServerCertificate=True;"
  }
}
```

---

## 🧪 تشغيل الاختبارات | Running Tests

```bash
dotnet test
```

يغطي منطق الأعمال الحرج: توازن القيود المحاسبية، حساب المخزون والتكلفة، صحة كل استراتيجيات الترحيل التلقائي.

---

## 📌 حالة المشروع | Project Status

🚧 **قيد التطوير النشط** — يُعاد بناء هذا النظام بالكامل من الصفر، كإعادة تصميم معماري وواجهة حديثة لنظام محاسبي قائم فعلياً كان يعمل بـ WinForms، مع الحفاظ على نفس النطاق الوظيفي الكامل وإصلاح نقاط ضعف هيكلية جوهرية.

### خارطة الطريق
- [x] تحليل النظام القديم وتوثيق كل الوظائف
- [x] تصميم Schema قاعدة البيانات الكامل
- [x] هيكلة المشروع البرمجي (Clean Architecture)
- [x] تصميم خدمات الترحيل التلقائي والمخزون
- [ ] بناء الشاشات الأساسية (الحسابات، الفواتير، الأصناف)
- [ ] بناء شاشة الكاشير/POS
- [ ] بناء التقارير المالية والطباعة
- [ ] اختبارات شاملة (Unit + Integration)
- [ ] هوية بصرية وواجهة نهائية

---

## 🤝 المساهمة | Contributing

هذا مشروع خاص قيد التطوير. المساهمات والاقتراحات مرحّب بها عبر Issues.

## 📄 الترخيص | License

هذا المستودع خاص حالياً؛ سيُحدَّد الترخيص لاحقاً عند النشر التجاري.

## 📬 التواصل | Contact

لأي استفسار أو تعاون، يُرجى فتح Issue بالمستودع.
