using CommunityToolkit.Mvvm.ComponentModel;
using ApplicationIdentity = HisabTak.Application.ApplicationIdentity;

namespace HisabTak.WPF.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(ApplicationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Version = identity.Version;
    }

    public string Title => "النظام المحاسبي";

    public string Version { get; }
}
