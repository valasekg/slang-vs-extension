using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace SlangClient
{
    /// <summary>
    /// Shows the editor's line number margin in Slang documents.
    /// </summary>
    /// <remarks>
    /// The built-in line number margin is contributed for the "text" content type, which "slang"
    /// derives from, so it is already composed for these buffers; it is only created once the
    /// TextViewHost/LineNumberMargin option is turned on. Visual Studio normally turns that option
    /// on from the per-language Tools > Options > Text Editor page, but "slang" is a MEF content
    /// type with no language service registered behind it, so nothing ever sets it and Slang files
    /// open without a gutter. Setting it while the view is being created fills that gap - the
    /// option is read when the view host builds its margins, which happens after this runs.
    /// </remarks>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType(SlangContentDefinition.SlangContentType)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    internal sealed class SlangLineNumberMargin : IWpfTextViewCreationListener
    {
        public void TextViewCreated(IWpfTextView textView)
        {
            textView.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, true);
        }
    }
}
