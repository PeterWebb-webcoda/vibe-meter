using System.Windows;
using System.Windows.Input;
using VibeMeter.ViewModels;

namespace VibeMeter.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ResetPosition();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    public void ResetPosition()
    {
        var workArea = SystemParameters.WorkArea;
        // Bottom-right with 18px margin.
        Left = workArea.Right - Width - 18;
        Top = workArea.Bottom - Height - 18;
    }

    private void ResetPositionButton_Click(object sender, RoutedEventArgs e) => ResetPosition();

    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
        => (System.Windows.Application.Current as App)?.ShowSettings();
}
