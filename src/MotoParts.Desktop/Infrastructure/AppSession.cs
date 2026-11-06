using MotoParts.Core;
using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Security;

namespace MotoParts.Desktop.Infrastructure;

/// <summary>
/// The running application: configuration, the database, the clock, and who
/// is signed in. One per process.
/// </summary>
public sealed class AppSession
{
    public AppSession(AppConfig config, IClock clock)
    {
        Config = config;
        Clock = clock;
        Database = new Database(config.DatabasePath);
    }

    public static AppSession Current { get; set; } = null!;

    public AppConfig Config { get; }

    public IClock Clock { get; }

    public Database Database { get; }

    public Actor? User { get; private set; }

    public Actor RequireUser => User ?? throw new InvalidOperationException("No user is signed in.");

    public event EventHandler? UserChanged;

    /// <summary>A fresh unit of work. Dispose it when the operation is done.</summary>
    public ServiceScope NewScope() => new(Database, Clock);

    public void SignIn(Actor actor)
    {
        User = actor;
        UserChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut()
    {
        User = null;
        UserChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-reads the signed-in user's roles, e.g. after an owner edits them.</summary>
    public void RefreshUser()
    {
        if (User is null)
        {
            return;
        }

        using var scope = NewScope();
        User = scope.Users.LoadActor(User.Id);
        UserChanged?.Invoke(this, EventArgs.Empty);
    }
}
