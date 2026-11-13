using System;
using System.Windows.Media;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>Immutable set of brushes for one theme.</summary>
    public sealed class DiffPalette
    {
        public DiffPalette(
            Brush added,
            Brush modified,
            Brush deleted,
            Color addedLine,
            Color modifiedLine,
            Color deletedLine,
            Brush addedLineBrush,
            Brush modifiedLineBrush,
            Brush deletedLineBrush)
        {
            Added = added;
            Modified = modified;
            Deleted = deleted;
            AddedLine = addedLine;
            ModifiedLine = modifiedLine;
            DeletedLine = deletedLine;
            AddedLineBrush = addedLineBrush;
            ModifiedLineBrush = modifiedLineBrush;
            DeletedLineBrush = deletedLineBrush;
        }

        /// <summary>Gutter bar for lines that do not exist in BASE.</summary>
        public Brush Added { get; private set; }

        /// <summary>Gutter bar for lines whose content changed.</summary>
        public Brush Modified { get; private set; }

        /// <summary>Gutter bar for removed BASE lines.</summary>
        public Brush Deleted { get; private set; }

        public Color AddedLine { get; private set; }

        public Color ModifiedLine { get; private set; }

        public Color DeletedLine { get; private set; }

        public Brush AddedLineBrush { get; private set; }

        public Brush ModifiedLineBrush { get; private set; }

        public Brush DeletedLineBrush { get; private set; }

        public Brush ForKind(DiffKind kind)
        {
            switch (kind)
            {
                case DiffKind.Added: return Added;
                case DiffKind.Deleted: return Deleted;
                default: return Modified;
            }
        }

        public Brush LineForKind(DiffKind kind)
        {
            switch (kind)
            {
                case DiffKind.Added: return AddedLineBrush;
                case DiffKind.Deleted: return DeletedLineBrush;
                default: return ModifiedLineBrush;
            }
        }
    }

    /// <summary>
    /// Colour mapping derived from WinMerge's Options &gt; Colors dialog.
    ///
    /// WinMerge paints a changed line with the "Difference" background and a line that only
    /// exists on one side with that row's "Deleted" swatch, so only two base colours are
    /// needed: #EFCB05 and #C0C0C0. Added and modified lines share the amber and are told
    /// apart by opacity - a solid bar is a new line, a washed out bar is an edited one.
    /// </summary>
    public static class DiffColorProvider
    {
        // WinMerge | Difference | Background
        private static readonly Color Difference = Color.FromRgb(0xEF, 0xCB, 0x05);

        // WinMerge | Difference | Deleted, with a darker variant for dark themes
        private static readonly Color DeletedLight = Color.FromRgb(0xC0, 0xC0, 0xC0);
        private static readonly Color DeletedDark = Color.FromRgb(0x8A, 0x8A, 0x8A);

        // Gutter bar opacity
        private const byte AddedAlpha = 0xFF;
        private const byte ModifiedAlpha = 0x73; // ~45 %
        private const byte DeletedAlpha = 0xE6;

        // Line background opacity, kept far lower so text stays readable
        private const byte AddedLineAlpha = 0x2E;
        private const byte ModifiedLineAlpha = 0x1C;
        private const byte DeletedLineAlpha = 0x22;

        private static readonly DiffPalette Light = Build(false);
        private static readonly DiffPalette Dark = Build(true);

        public static DiffPalette For(Brush textViewBackground)
        {
            return IsDark(textViewBackground) ? Dark : Light;
        }

        public static bool IsDark(Brush background)
        {
            var solid = background as SolidColorBrush;

            if (solid == null)
            {
                return false;
            }

            var c = solid.Color;
            var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

            return luminance < 0.5;
        }

        private static DiffPalette Build(bool dark)
        {
            var deletedBase = dark ? DeletedDark : DeletedLight;

            // Amber is bright enough for both themes; lift it slightly on dark backgrounds
            // so the bar does not disappear next to the light text.
            var amber = dark
                ? Color.FromRgb(0xF2, 0xD2, 0x2A)
                : Difference;

            return new DiffPalette(
                Frozen(WithAlpha(amber, AddedAlpha)),
                Frozen(WithAlpha(amber, ModifiedAlpha)),
                Frozen(WithAlpha(deletedBase, DeletedAlpha)),
                WithAlpha(amber, AddedLineAlpha),
                WithAlpha(amber, ModifiedLineAlpha),
                WithAlpha(deletedBase, DeletedLineAlpha),
                Frozen(WithAlpha(amber, AddedLineAlpha)),
                Frozen(WithAlpha(amber, ModifiedLineAlpha)),
                Frozen(WithAlpha(deletedBase, DeletedLineAlpha)));
        }

        private static Color WithAlpha(Color color, byte alpha)
        {
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
