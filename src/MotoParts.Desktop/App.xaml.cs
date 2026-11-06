using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Desktop.Views.Shell;
using MotoParts.Documents;

namespace MotoParts.Desktop;

/// <summary>
/// Start-up and shut-down.
///
///   one instance -> logging -> database migrated (backed up first) ->
///   reference data -> daily backup -> first-run setup if needed -> sign in
///
/// Signing out returns to the sign-in window rather than closing, because one
/// computer is shared by several staff.
/// </summary>
public partial class App : Application
{
    private const double BackupDueAfterHours = 20;

    private Mutex? _singleInstance;
    private bool _anyoneSignedIn;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Numbers and dates are always formatted the same way regardless of the
        // Windows region: Western digits, Gregorian calendar, "." for decimals.
        // (Saudi regional settings would otherwise switch dates to Hijri.)
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.ShortDatePattern = "yyyy-MM-dd";
        culture.DateTimeFormat.LongDatePattern = "yyyy-MM-dd";
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        // The date picker's built-in placeholder is English ("Select a date").
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.DatePicker), FrameworkElement.LoadedEvent, new RoutedEventHandler(LocaliseDatePicker));

        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            SqlTypeHandlers.Register();
            DocumentTheme.Configure();
            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await SnapshotRunner.RunAsync(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : null);
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(e.Args[1], "snapshot-error.txt"), ex.ToString());
                    Shutdown(1);
                }
            });
            return;
        }

        _singleInstance = new Mutex(true, @"Local\MotoParts.Desktop.SingleInstance", out var isFirst);

        if (!isFirst)
        {
            Dialogs.Info("البرنامج مفتوح بالفعل على هذا الجهاز.", "تنبيه");
            Shutdown();
            return;
        }

        var config = AppConfig.Load();
        Log.Initialise(config.LogDirectory);
        Log.Info("Application starting.");

        DispatcherUnhandledException += OnUnhandledUiException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        SqlTypeHandlers.Register();
        DocumentTheme.Configure();

        AppSession.Current = new AppSession(config, new SystemClock());

        if (!PrepareDatabase(config))
        {
            Shutdown(1);
            return;
        }

        BackupIfDue();
        ShowFirstRunOrSignIn();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_anyoneSignedIn)
        {
            TakeBackup("exit");
        }

        Log.Info("Application exiting.");
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Shown after sign-out and after first-run setup.</summary>
    public void ShowSignIn()
    {
        var login = new LoginWindow();

        login.SignedIn += (_, _) =>
        {
            _anyoneSignedIn = true;
            var main = new MainWindow();
            MainWindow = main;
            main.SignedOut += (_, _) => ShowSignIn();
            main.Show();
            login.Close();
        };

        login.Closed += (_, _) =>
        {
            if (AppSession.Current.User is null)
            {
                Shutdown();
            }
        };

        MainWindow = login;
        login.Show();
    }

    private void ShowFirstRunOrSignIn()
    {
        bool needsSetup;

        using (var scope = AppSession.Current.NewScope())
        {
            needsSetup = !scope.Users.AnyUserExists();
        }

        if (!needsSetup)
        {
            ShowSignIn();
            return;
        }

        var setup = new SetupWindow();
        MainWindow = setup;

        setup.Closed += (_, _) =>
        {
            if (setup.Completed)
            {
                ShowSignIn();
            }
            else
            {
                Shutdown();
            }
        };

        setup.Show();
    }

    private static bool PrepareDatabase(AppConfig config)
    {
        try
        {
            var upgradeBackup = Path.Combine(config.BackupDirectory, $"motoparts-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-beforeupgrade.db");
            var applied = new MigrationRunner(AppSession.Current.Database).Migrate(upgradeBackup);

            if (applied.Count > 0)
            {
                Log.Info($"Applied migrations: {string.Join(", ", applied)}");
            }

            using var scope = AppSession.Current.NewScope();
            scope.Seeder.Seed();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Database preparation failed", ex);
            Dialogs.Error(
                $"تعذر تجهيز قاعدة البيانات:\n{ex.Message}\n\nلم يُغيَّر شيء في بياناتك. إن كان هذا بعد تحديث للبرنامج فتوجد نسخة احتياطية في:\n{config.BackupDirectory}",
                "خطأ في قاعدة البيانات");
            return false;
        }
    }

    private static void BackupIfDue()
    {
        try
        {
            var folder = BackupFolder();
            using var scope = AppSession.Current.NewScope();
            var last = scope.Backups.LastBackupAt(folder);

            if (last is null || last < DateTime.Now.AddHours(-BackupDueAfterHours))
            {
                TakeBackup("daily");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Scheduled backup failed", ex);
        }
    }

    public static void TakeBackup(string label)
    {
        try
        {
            using var scope = AppSession.Current.NewScope();
            var folder = BackupFolder();
            var path = scope.Backups.Create(folder, label);

            var keep = int.TryParse(scope.Settings.Get(SettingsService.BackupKeepCount), out var k) ? k : 30;
            scope.Backups.Prune(folder, keep);

            Log.Info($"Backup written: {path}");
        }
        catch (Exception ex)
        {
            // A backup failure must be visible somewhere, but never block work.
            Log.Error($"Backup ({label}) failed", ex);
        }
    }

    /// <summary>Starts a fresh copy of the program and closes this one, e.g. after a restore.</summary>
    public static void Restart()
    {
        var app = (App)Current;

        // The restored database must not be immediately backed up over the
        // newest backup by the exit hook, and the new process must be able to
        // take the single-instance lock.
        app._anyoneSignedIn = false;
        app._singleInstance?.Dispose();
        app._singleInstance = null;

        System.Diagnostics.Process.Start(Environment.ProcessPath!);
        Current.Shutdown();
    }

    /// <summary>The configured backup folder, overridable from the settings screen.</summary>
    public static string BackupFolder()
    {
        using var scope = AppSession.Current.NewScope();
        var configured = scope.Settings.Get(SettingsService.BackupFolder);
        return string.IsNullOrWhiteSpace(configured) ? AppSession.Current.Config.BackupDirectory : configured;
    }

    private static void LocaliseDatePicker(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.DatePicker picker
            && picker.Template.FindName("PART_TextBox", picker) is System.Windows.Controls.Primitives.DatePickerTextBox box)
        {
            box.ApplyTemplate();

            if (box.Template.FindName("PART_Watermark", box) is System.Windows.Controls.ContentControl watermark)
            {
                watermark.Content = "اختر التاريخ";
            }
        }
    }

    private void OnUnhandledUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is BusinessRuleException rule)
        {
            Dialogs.Error(rule.Message);
        }
        else
        {
            Log.Error("Unhandled UI exception", e.Exception);
            Dialogs.Error("حدث خطأ غير متوقع. سُجّلت التفاصيل في ملف السجل، ويمكنك متابعة العمل.", "خطأ");
        }

        e.Handled = true;
    }
}
