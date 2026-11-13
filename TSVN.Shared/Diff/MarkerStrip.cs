using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>One bar to paint, in the coordinates of the strip's canvas.</summary>
    public struct MarkerRect
    {
        public MarkerRect(double top, double height, Brush fill, string tooltip)
        {
            Top = top;
            Height = height;
            Fill = fill;
            Tooltip = tooltip;
        }

        public double Top { get; private set; }

        public double Height { get; private set; }

        public Brush Fill { get; private set; }

        public string Tooltip { get; private set; }
    }

    /// <summary>
    /// The rectangle pool both diff margins paint through.
    ///
    /// Bars are reused instead of rebuilt on every layout pass. Clearing the children of a canvas
    /// and adding new ones during a scroll leaves the strip empty for one frame, which is what
    /// makes the marks next to the scroll bar blink; reusing a rectangle only touches the
    /// dependency properties whose value actually changed, so a mark that has not moved costs
    /// nothing and never disappears.
    ///
    /// The canvas has no intrinsic size, so a height is always set explicitly: without one the
    /// margin container arranges it at zero and <see cref="UIElement.ClipToBounds"/> throws every
    /// bar away, which looks exactly like nothing was ever painted.
    /// </summary>
    public sealed class MarkerStrip
    {
        private readonly List<Rectangle> _pool = new List<Rectangle>();
        private readonly Canvas _canvas;
        private readonly double _markLeft;
        private readonly double _markWidth;
        private readonly double _cornerRadius;

        public MarkerStrip(
            double stripWidth,
            double markLeft,
            double markWidth,
            double cornerRadius,
            bool hitTest,
            VerticalAlignment verticalAlignment)
        {
            _markLeft = markLeft;
            _markWidth = markWidth;
            _cornerRadius = cornerRadius;

            _canvas = new Canvas
            {
                Width = stripWidth,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = verticalAlignment,
                IsHitTestVisible = hitTest
            };
        }

        public Canvas Element
        {
            get { return _canvas; }
        }

        public void SetHeight(double height)
        {
            if (height > 0.0 && _canvas.Height != height)
            {
                _canvas.Height = height;
            }
        }

        /// <summary>Moves the pooled bars onto <paramref name="markers"/>, adding or hiding as needed.</summary>
        public void Apply(List<MarkerRect> markers)
        {
            var wanted = markers == null ? 0 : markers.Count;

            while (_pool.Count < wanted)
            {
                var created = new Rectangle
                {
                    Width = _markWidth,
                    RadiusX = _cornerRadius,
                    RadiusY = _cornerRadius
                };

                Canvas.SetLeft(created, _markLeft);
                _pool.Add(created);
                _canvas.Children.Add(created);
            }

            for (var i = 0; i < _pool.Count; i++)
            {
                var rectangle = _pool[i];

                if (i >= wanted)
                {
                    if (rectangle.Visibility != Visibility.Collapsed)
                    {
                        rectangle.Visibility = Visibility.Collapsed;
                    }

                    continue;
                }

                var marker = markers[i];

                if (rectangle.Visibility != Visibility.Visible)
                {
                    rectangle.Visibility = Visibility.Visible;
                }

                if (!ReferenceEquals(rectangle.Fill, marker.Fill))
                {
                    rectangle.Fill = marker.Fill;
                }

                if (rectangle.Height != marker.Height)
                {
                    rectangle.Height = marker.Height;
                }

                if (Canvas.GetTop(rectangle) != marker.Top)
                {
                    Canvas.SetTop(rectangle, marker.Top);
                }

                if (!string.Equals(rectangle.ToolTip as string, marker.Tooltip, StringComparison.Ordinal))
                {
                    rectangle.ToolTip = marker.Tooltip;
                }
            }
        }

        public void Clear()
        {
            _canvas.Children.Clear();
            _pool.Clear();
        }
    }
}
