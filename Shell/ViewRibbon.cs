// SEMANTIC ANCHOR: The Ansicht ribbon delegates to existing footer view mode and zoom controls.
// Do not introduce independent view state, zoom state, theme values, or change Startseite.
using System.Windows;

namespace PewDF
{
    public partial class MainWindow
    {
        private void RibbonViewContinuous_Click(object sender, RoutedEventArgs e) => ViewContinuousRadio.IsChecked = true;
        private void RibbonViewSingle_Click(object sender, RoutedEventArgs e) => ViewSingleRadio.IsChecked = true;
        private void RibbonViewTwoPage_Click(object sender, RoutedEventArgs e) => ViewTwoPageRadio.IsChecked = true;
        private void RibbonViewGrid_Click(object sender, RoutedEventArgs e) => ViewGridRadio.IsChecked = true;
        private void RibbonFitPage_Click(object sender, RoutedEventArgs e) => ZoomBox.SelectedIndex = 0;
        private void RibbonFitWidth_Click(object sender, RoutedEventArgs e) => ZoomBox.SelectedIndex = 1;
    }
}
