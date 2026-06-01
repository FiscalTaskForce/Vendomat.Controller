using CommunityToolkit.Mvvm.ComponentModel;
using Vendomat.Controller.Mobile.Models;

namespace Vendomat.Controller.Mobile.ViewModels;

public partial class ConnectionModeOptionViewModel : ObservableObject
{
    public ConnectionModeOptionViewModel(
        MachineConnectionPreference preference,
        string title,
        string description)
    {
        Preference = preference;
        Title = title;
        Description = description;
    }

    public MachineConnectionPreference Preference { get; }

    public string Title { get; }

    public string Description { get; }

    [ObservableProperty]
    private bool isSelected;
}
