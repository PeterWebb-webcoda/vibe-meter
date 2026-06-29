using System.Windows;
using System.Windows.Controls;
using VibeMeter.Models;
using VibeMeter.ViewModels;

namespace VibeMeter.Views;

/// <summary>
/// Picks the meter DataTemplate based on <see cref="MainViewModel.MeterStyle"/>.
/// </summary>
public class MeterStyleTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CircularTemplate { get; set; }
    public DataTemplate? HorizontalTemplate { get; set; }
    public DataTemplate? BatteryTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is UsageGaugeData)
        {
            if (container is FrameworkElement element)
            {
                var viewModel = element.DataContext as MainViewModel;
                if (viewModel == null)
                {
                    var window = Window.GetWindow(element);
                    viewModel = window?.DataContext as MainViewModel;
                }

                if (viewModel != null)
                {
                    return viewModel.MeterStyle switch
                    {
                        MeterStyle.Circular => CircularTemplate,
                        MeterStyle.Horizontal => HorizontalTemplate,
                        MeterStyle.Battery => BatteryTemplate,
                        _ => CircularTemplate
                    };
                }
            }
            return CircularTemplate;
        }
        return base.SelectTemplate(item, container);
    }
}
