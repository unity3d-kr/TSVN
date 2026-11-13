using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Editor;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>
    /// How much of the editor's scale the gutter has to undo.
    ///
    /// <see cref="ITextViewLine.Top"/> and <see cref="ITextView.ViewportHeight"/> are expressed in
    /// the text view's own units, but a margin is a sibling visual, arranged outside whatever
    /// transform the editor applies to its text. At the default 100 % the two spaces coincide and
    /// nothing has to be done, which is why the markers only drifted once the zoom was changed:
    /// every offset then grows in proportion to the distance from the top of the view.
    ///
    /// The factor is measured from the visual tree rather than taken from
    /// <see cref="IWpfTextView.ZoomLevel"/>. Dividing the height of a thousand units inside the
    /// text view by the same height inside the canvas cancels any translation - the scroll
    /// position is handled by the line origin instead - and covers every other reason the two
    /// spaces can differ, not just zoom.
    ///
    /// Only the gutter uses this. The overview bar places its marks at fractions of its own height,
    /// so there is no second coordinate space for it to convert.
    /// </summary>
    public static class ViewScale
    {
        /// <summary>Span used for the measurement; large enough to beat rounding, arbitrary otherwise.</summary>
        private const double ProbeSpan = 1000.0;

        /// <summary>Canvas pixels per text view unit. 1.0 when the two spaces already agree.</summary>
        public static double Resolve(IWpfTextView textView, Visual canvas)
        {
            if (textView == null || canvas == null)
            {
                return Fallback(textView);
            }

            try
            {
                var text = textView.VisualElement;

                var ancestor = text == null ? null : FindCommonAncestor(text, canvas);

                if (ancestor == null)
                {
                    return Fallback(textView);
                }

                var textSpan = Measure(text, ancestor);
                var canvasSpan = Measure(canvas, ancestor);

                if (textSpan <= 0.001 || canvasSpan <= 0.001)
                {
                    return Fallback(textView);
                }

                var scale = textSpan / canvasSpan;

                return scale > 0.1 && scale < 10.0 ? scale : Fallback(textView);
            }
            catch (Exception)
            {
                // Not connected to a visual tree yet, or mid layout: the fallback is good enough
                // until the next layout pass asks again.
                return Fallback(textView);
            }
        }

        /// <summary>Rendered height of a vertical span measured in text view units.</summary>
        public static double ToCanvas(double textUnits, double scale)
        {
            return textUnits * scale;
        }

        private static double Measure(Visual element, Visual ancestor)
        {
            var transform = element.TransformToAncestor(ancestor);
            var top = transform.Transform(new Point(0.0, 0.0)).Y;
            var bottom = transform.Transform(new Point(0.0, ProbeSpan)).Y;

            return bottom - top;
        }

        private static double Fallback(IWpfTextView textView)
        {
            if (textView == null)
            {
                return 1.0;
            }

            var zoom = textView.ZoomLevel;

            return zoom > 0.0 ? zoom / 100.0 : 1.0;
        }

        private static Visual FindCommonAncestor(DependencyObject first, DependencyObject second)
        {
            var ancestors = new HashSet<DependencyObject>();

            for (var current = first; current != null; current = VisualTreeHelper.GetParent(current))
            {
                ancestors.Add(current);
            }

            for (var current = second; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (ancestors.Contains(current))
                {
                    return current as Visual;
                }
            }

            return null;
        }
    }
}
