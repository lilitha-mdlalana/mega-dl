using CommunityToolkit.Mvvm.ComponentModel;

namespace MegaDl.Desktop.ViewModels;

/// <summary>Base for every view model; ObservableObject implements INotifyPropertyChanged for us.</summary>
public abstract class ViewModelBase : ObservableObject;
