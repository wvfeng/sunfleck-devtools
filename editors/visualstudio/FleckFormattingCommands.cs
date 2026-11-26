using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;
using Sunfleck.Development;

namespace Sunfleck.VisualStudio;

[Guid("3c2d8210-bfca-4205-88e3-70c41576c679")]
public sealed class FleckFormattingOptions : DialogPage
{
    [Category("Models")]
    [System.ComponentModel.DisplayName("Model files")]
    [Description("Semicolon-separated metadata paths, relative to the solution directory. Leave empty to discover each project's active configuration.")]
    [DefaultValue("")]
    public string ModelFiles { get; set; } = "";

    [Category("Formatting")]
    [System.ComponentModel.DisplayName("Format on save")]
    [Description("Format Fleck documents when saving.")]
    public bool FormatOnSave { get; set; }
}

[PackageRegistration(UseManagedResourcesOnly = true)]
[ProvideOptionPage(typeof(FleckFormattingOptions), "Sunfleck", "Fleck", 0, 0, true)]
[Guid("8914f51d-af71-4a4e-8ddf-aa354e771c98")]
public sealed class FleckFormattingPackage : Package
{
    internal bool FormatOnSave => ((FleckFormattingOptions)GetDialogPage(typeof(FleckFormattingOptions))).FormatOnSave;
    internal string ModelFiles => ((FleckFormattingOptions)GetDialogPage(typeof(FleckFormattingOptions))).ModelFiles;
}

[Export(typeof(ICommandHandler))]
[Name("Sunfleck Fleck formatting")]
[ContentType("fleck")]
internal sealed class FleckFormattingCommands : ICommandHandler<FormatDocumentCommandArgs>, IChainedCommandHandler<SaveCommandArgs>
{
    public string DisplayName => "Sunfleck Fleck formatting";

    public CommandState GetCommandState(FormatDocumentCommandArgs args) =>
        args.SubjectBuffer.CheckEditAccess() ? CommandState.Available : CommandState.Unavailable;

    public bool ExecuteCommand(FormatDocumentCommandArgs args, CommandExecutionContext context)
    {
        Format(args.TextView, args.SubjectBuffer);
        return true;
    }

    public CommandState GetCommandState(SaveCommandArgs args, Func<CommandState> next) => next();

    public void ExecuteCommand(SaveCommandArgs args, Action next, CommandExecutionContext context)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var shell = Package.GetGlobalService(typeof(SVsShell)) as IVsShell;
        var packageId = typeof(FleckFormattingPackage).GUID;
        if (shell is not null && shell.LoadPackage(ref packageId, out var package) >= 0 &&
            package is FleckFormattingPackage formatting && formatting.FormatOnSave)
        {
            Format(args.TextView, args.SubjectBuffer);
        }
        next();
    }

    private static void Format(ITextView view, ITextBuffer buffer)
    {
        var snapshot = buffer.CurrentSnapshot;
        var document = new FleckDocument(snapshot.GetText());
        using var edit = buffer.CreateEdit();
        foreach (var change in FleckFormatter.GetEdits(document,
            view.Options.GetOptionValue(DefaultOptions.TabSizeOptionId),
            view.Options.GetOptionValue(DefaultOptions.ConvertTabsToSpacesOptionId)))
        {
            if (!edit.Replace(change.Start, change.Length, change.Text))
            {
                return;
            }
        }
        if (edit.HasEffectiveChanges)
        {
            edit.Apply();
        }
    }
}
