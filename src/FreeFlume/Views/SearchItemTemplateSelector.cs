using FreeFlume.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FreeFlume.Views
{
    /// <summary>Channels get a square-avatar template; everything else uses the video template.</summary>
    public sealed partial class SearchItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? VideoTemplate { get; set; }
        public DataTemplate? ChannelTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item) =>
            item is SearchResult { Kind: ResultKind.Channel } ? ChannelTemplate : VideoTemplate;

        protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
            SelectTemplateCore(item);
    }
}
