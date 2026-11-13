using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Editor;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>
    /// How much of the editor's scale the margins have to undo.
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
    /// </summary>
    public static class ViewScale
    {
        /// <summary>Span used for the measurement; large enough to beat rounding, arbitrary otherwise.</summary>
        private const double ProbeSpan = 1000.0;

        /// <summary>Last reported pair, so the log only grows when the scale actually changes. Temporary.</summary>
        private static double _loggedScale = double.NaN;
        private static double _loggedZoom = double.NaN;

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
                    Report(textView, -1.0, -1.0);

                    return Fallback(textView);
                }

                var textSpan = Measure(text, ancestor);
                var canvasSpan = Measure(canvas, ancestor);

                if (textSpan <= 0.001 || canvasSpan <= 0.001)
                {
                    Report(textView, textSpan, canvasSpan);

                    return Fallback(textView);
                }

                var scale = textSpan / canvasSpan;

                Report(textView, textSpan, canvasSpan);

                return scale > 0.1 && scale < 10.0 ? scale : Fallback(textView);
            }
            catch (Exception)
            {
                // Not connected to a visual tree yet, or mid layout: the fallback is good enough
                // until the next layout pass asks again.
                return Fallback(textView);
            }
        }

        /// <summary>
        /// Writes one line per change of scale or zoom to %LOCALAPPDATA%\TSVN\view-scale.log.
        ///
        /// Temporary: it answers whether the editor reports a zoomed layout through the transform
        /// at all, which decides whether the scale has to be applied here or is already inside the
        /// line coordinates. Two lines per session, no hot path cost.
        /// </summary>
        private static void Report(IWpfTextView textView, double textSpan, double canvasSpan)
        {
            var zoom = textView.ZoomLevel;
            var scale = canvasSpan > 0.001 ? textSpan / canvasSpan : -1.0;

            if (!double.IsNaN(_loggedScale)
                && Math.Abs(scale - _loggedScale) < 0.0005
                && Math.Abs(zoom - _loggedZoom) < 0.5)
            {
                return;
            }

            _loggedScale = scale;
            _loggedZoom = zoom;

            try
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TSVN");

                Directory.CreateDirectory(folder);

                File.AppendAllText(
                    Path.Combine(folder, "view-scale.log"),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "zoom={0} scale={1:F4} textSpan={2:F1} canvasSpan={3:F1}{4}",
                        zoom,
                        scale,
                        textSpan,
                        canvasSpan,
                        Environment.NewLine));
            }
            catch (Exception)
            {
                // Diagnostics are never a reason to break a margin.
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
