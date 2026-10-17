using System.ComponentModel;
using System.Windows;
using DroidMic.Desktop.ViewModels;

namespace DroidMic.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>Set by the app when the user chose Exit, so closing really closes.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Raised when the window hides to the tray instead of closing.</summary>
    public event System.Action? HiddenToTray;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && ((App)Application.Current).Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }
        base.OnClosing(e);
    }

    private void GoToMicSetup(object sender, RoutedEventArgs e) => ViewModel.SelectedSection = 2;
    private void GoToPairing(object sender, RoutedEventArgs e) => ViewModel.SelectedSection = 1;
}
