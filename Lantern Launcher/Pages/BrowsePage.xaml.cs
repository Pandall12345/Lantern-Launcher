using System.Windows.Controls;
using System.Windows.Input;

namespace LanternLauncher.Pages;

public partial class BrowsePage : UserControl
{
    public BrowsePage() => InitializeComponent();

    private void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is BrowseViewModel vm && vm.SearchCommand.CanExecute(null))
        {
            vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Prev_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is BrowseViewModel vm && vm.PrevPageCommand.CanExecute(null))
            vm.PrevPageCommand.Execute(null);
    }

    private void Next_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is BrowseViewModel vm && vm.NextPageCommand.CanExecute(null))
            vm.NextPageCommand.Execute(null);
    }
}
