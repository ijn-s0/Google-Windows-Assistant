using System.Windows;
using System.Windows.Controls;

namespace GeminiLauncher
{
    public class ResultTemplateSelector : DataTemplateSelector
    {
        public DataTemplate Item { get; set; }
        public DataTemplate Header { get; set; }
        public override DataTemplate SelectTemplate(object item, DependencyObject container)
            => item is ResultItem r && r.IsHeader ? Header : Item;
    }
}
