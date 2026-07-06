using System.Windows;
using System.Windows.Input;

namespace VibeMeter.Views;

public partial class CostDetailsWindow : Window
{
    public CostDetailsWindow(object dataContext)
    {
        InitializeComponent();
        DataContext = dataContext;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
