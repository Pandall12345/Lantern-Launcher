using System.Windows;
using System.Windows.Controls;
using LanternLauncher.Shell;

namespace LanternLauncher;

public class PageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HomeTemplate { get; set; }
    public DataTemplate? LibraryTemplate { get; set; }
    public DataTemplate? BrowseTemplate { get; set; }
    public DataTemplate? PacksTemplate { get; set; }
    public DataTemplate? DownloadsTemplate { get; set; }
    public DataTemplate? SettingsTemplate { get; set; }
    public DataTemplate? ModDetailTemplate { get; set; }
    public DataTemplate? InstanceDetailTemplate { get; set; }
    public DataTemplate? InstanceModsTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is PageKey key
            ? key switch
            {
                PageKey.Home => HomeTemplate,
                PageKey.Library => LibraryTemplate,
                PageKey.Browse => BrowseTemplate,
                PageKey.Packs => PacksTemplate,
                PageKey.Downloads => DownloadsTemplate,
                PageKey.Settings => SettingsTemplate,
                PageKey.ModDetail => ModDetailTemplate,
                PageKey.InstanceDetail => InstanceDetailTemplate,
                PageKey.InstanceMods => InstanceModsTemplate,
                _ => HomeTemplate
            }
            : null;
}
