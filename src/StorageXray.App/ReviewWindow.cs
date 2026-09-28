using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using StorageXray.Core;

namespace StorageXray.App;

public sealed class ReviewWindow : Window
{
    public ReviewWindow(IReadOnlyList<CleanupItem> items)
    {
        Title = "Review cleanup"; Width = 900; Height = 610; MinWidth = 650; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(16, 25, 39));
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top);
        heading.Children.Add(new TextBlock { Text = $"Recycle {items.Count:N0} selected files?", FontSize = 25, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = $"{Sizes.Format(items.Sum(i => i.File.Size))} of file content. Review the full paths below. Files are rechecked before recycling.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18) });
        panel.Children.Add(heading);
        var bottom = new StackPanel { Margin = new Thickness(0, 18, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom);
        var agree = new CheckBox { Content = "I reviewed these files and want to send them to the Recycle Bin.", Margin = new Thickness(0, 0, 0, 14) };
        bottom.Children.Add(agree);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Keep my files", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var confirm = new Button { Content = "Send to Recycle Bin", Style = (Style)FindResource("Primary"), IsEnabled = false };
        agree.Checked += (_, _) => confirm.IsEnabled = true; agree.Unchecked += (_, _) => confirm.IsEnabled = false;
        confirm.Click += (_, _) => DialogResult = true; cancel.Click += (_, _) => DialogResult = false;
        actions.Children.Add(cancel); actions.Children.Add(confirm); bottom.Children.Add(actions); panel.Children.Add(bottom);
        var grid = new DataGrid { ItemsSource = items };
        grid.Columns.Add(new DataGridTextColumn { Header = "File to recycle", Binding = new Binding("Path"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Size", Binding = new Binding("SizeText"), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Copy being kept (duplicates)", Binding = new Binding("KeeperPath"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        panel.Children.Add(grid); Content = panel;
    }
}
