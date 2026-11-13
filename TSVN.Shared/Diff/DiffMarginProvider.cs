using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using Microsoft.VisualStudio.Utilities;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>
    /// Registers the gutter strip between the line numbers and the text.
    ///
    /// This is the extension's first MEF export: without a
    /// <c>Microsoft.VisualStudio.MefComponent</c> asset in the VSIX manifest and
    /// <see cref="Microsoft.VisualStudio.Shell.ProvideBindingPathAttribute"/> on the package,
    /// Visual Studio never composes it.
    /// </summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(DiffMargin.MarginName)]
    // Right of the line numbers. Do not add Before = Glyph here: combining both puts the margin
    // in the very first slot, left of the line numbers.
    [Order(After = PredefinedMarginNames.LineNumber)]
    [MarginContainer(PredefinedMarginNames.Left)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    public sealed class DiffMarginProvider : IWpfTextViewMarginProvider
    {
        /// <summary>
        /// Tells the margin which lines a fold hides, so a changed run inside one can be marked on
        /// the header line instead of floating in the gap the fold left behind. Optional: without
        /// it the margin infers the header from the rendered lines and still refuses to draw
        /// outside them.
        /// </summary>
        [Import(AllowDefault = true)]
        public IOutliningManagerService OutliningManagerService { get; set; }

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            var textView = wpfTextViewHost.TextView;
            IOutliningManager outlining = null;

            try
            {
                if (OutliningManagerService != null)
                {
                    outlining = OutliningManagerService.GetOutliningManager(textView);
                }
            }
            catch (Exception)
            {
                // No outlining support in this view; the margin falls back to rendered lines only.
            }

            return new DiffMargin(textView, outlining);
        }
    }
}
