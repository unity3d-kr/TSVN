using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
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
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            return new DiffMargin(wpfTextViewHost.TextView);
        }
    }
}
