using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PewDF
{
    // SEMANTIC ANCHOR: NAV_CHEVRON_GEOMETRY_SSOT.
    // Reuse the same application-scoped vector geometry/style as the visible inline search.
    // INVARIANT: Only the path rotates. Container style, footprint, hit target, and theme remain independent.
    internal static class NavigationChevron
    {
        internal static Path Create(double rotationDegrees)
        {
            var path = new Path
            {
                Style = (Style)Application.Current.FindResource("NavigationChevronPathStyle"),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(rotationDegrees),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            return path;
        }
    }
}
