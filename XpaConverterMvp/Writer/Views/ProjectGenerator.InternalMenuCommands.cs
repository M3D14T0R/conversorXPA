using System;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string? ResolveInternalMenuCommand(MenuEntryDef entry)
    {
        var ev = entry.Event;
        if (ev is null || !string.Equals(ev.EventType, "I", StringComparison.OrdinalIgnoreCase) || !ev.InternalEventId.HasValue)
            return null;

        var text = Escape(entry.Description ?? "Menu Item");
        var id = ev.InternalEventId.Value;
        return id switch
        {
            11 => $"new ManagedCommand(\"{text}\", Command.Cut)",
            23 => $"new ManagedCommand(\"{text}\", Command.SwitchToUpdateActivity)",
            24 => $"new ManagedCommand(\"{text}\", Command.SwitchToInsertActivity)",
            25 => $"new ManagedCommand(\"{text}\", Command.SwitchToBrowseActivity)",
            26 => $"new ManagedCommand(\"{text}\", ENV.Commands.FindRows)",
            27 => $"new RaiseCommand(\"{text}\", ENV.Commands.ShellToOS)",
            28 => $"new ManagedCommand(\"{text}\", Command.ExitApplication)",
            30 => $"new ManagedCommand(\"{text}\", Command.SwitchToUpdateActivity)",
            31 => $"new ManagedCommand(\"{text}\", Command.SwitchToInsertActivity)",
            32 => $"new ManagedCommand(\"{text}\", Command.SwitchToBrowseActivity)",
            33 => $"new ManagedCommand(\"{text}\", Command.UndoChangesInRow)",
            34 => $"new ManagedCommand(\"{text}\", Command.Expand)",
            35 => $"new ManagedCommand(\"{text}\", Command.ExpandTextBox)",
            36 => $"new ManagedCommand(\"{text}\", Command.DeleteRow)",
            37 => $"new ManagedCommand(\"{text}\", Command.InsertRow)",
            64 => $"new ManagedCommand(\"{text}\", Command.GoToPreviousRow)",
            65 => $"new ManagedCommand(\"{text}\", Command.GoToNextRow)",
            66 => $"new ManagedCommand(\"{text}\", Command.GoToPreviousPage)",
            67 => $"new ManagedCommand(\"{text}\", Command.GoToNextPage)",
            72 => $"new ManagedCommand(\"{text}\", Command.GoToFirstRow)",
            73 => $"new ManagedCommand(\"{text}\", Command.GoToLastRow)",
            77 => $"new ManagedCommand(\"{text}\", Command.SetFocusedControlValueSameAsInPreviousRow)",
            122 => $"new ManagedCommand(\"{text}\", ENV.Commands.FindRows)",
            123 => $"new ManagedCommand(\"{text}\", ENV.Commands.FilterRows)",
            124 => $"new ManagedCommand(\"{text}\", ENV.Commands.SelectOrderBy)",
            125 => $"new ManagedCommand(\"{text}\", ENV.Commands.CustomOrderBy)",
            155 => $"new ManagedCommand(\"{text}\", Command.Help)",
            156 => $"new ManagedCommand(\"{text}\", Command.Help)",
            157 => $"new RaiseCommand(\"{text}\", ENV.Commands.About)",
            160 => $"new ManagedCommand(\"{text}\", ENV.Commands.ExportData)",
            162 => $"new RaiseCommand(\"{text}\", ENV.Commands.PrinterSettingsDialog)",
            165 => $"new ManagedCommand(\"{text}\", ENV.Commands.FindNextRow)",
            180 => $"new ManagedCommand(\"{text}\", Command.UndoEditing)",
            193 => $"new ManagedCommand(\"{text}\", ENV.Commands.ExportData)",
            200 => $"new ManagedCommand(\"{text}\", Command.SetFocusedControlValueToNull)",
            209 => $"new ManagedCommand(\"{text}\", Command.SetFocusedControlValueToNull)",
            239 => $"new ManagedCommand(\"{text}\", Command.Copy)",
            240 => $"new ManagedCommand(\"{text}\", Command.Paste)",
            286 => $"new ManagedCommand(\"{text}\", Command.SelectAll)",
            370 => $"new RaiseCommand(\"{text}\", ENV.Commands.CloseAllWindows)",
            462 => $"new RaiseCommand(\"{text}\", ENV.Commands.NextWindow)",
            463 => $"new RaiseCommand(\"{text}\", ENV.Commands.PreviousWindow)",
            _ => null
        };
    }

    private static string? ResolveCommandByInternalEventId(int internalEventId)
    {
        if (internalEventId is >= 219 and <= 238)
            return $"ENV.Commands.CustomCommand_{internalEventId - 218}";

        return internalEventId switch
        {
            13 => "Command.CloseForm",
            14 => "Command.Exit",
            23 => "ENV.Commands.SelectApplicationFromList",
            28 => "Command.ExitApplication",
            30 => "Command.SwitchToUpdateActivity",
            32 => "Command.SwitchToBrowseActivity",
            33 => "Command.UndoChangesInRow",
            42 => "Command.Select",
            34 => "Command.Expand",
            35 => "Command.ExpandTextBox",
            37 => "Command.InsertRow",
            36 => "Command.DeleteRow",
            242 => "Command.BeforeControlClick",
            245 => "Command.BeforeWindowClick",
            63 => "Command.GoToNextControl",
            64 => "Command.GoToPreviousRow",
            65 => "Command.GoToNextRow",
            66 => "Command.GoToPreviousPage",
            67 => "Command.GoToNextPage",
            72 => "Command.GoToFirstRow",
            73 => "Command.GoToLastRow",
            301 => "Command.DoubleClick",
            249 => "Command.WindowResize",
            250 => "Command.WindowMove",
            251 => "Command.WindowResize",
            243 => "ENV.Commands.PageHeader",
            244 => "ENV.Commands.PageFooter",
            293 => "Command.SaveCurrentRow",
            295 => "Command.ReloadData",
            302 => "Command.Click",
            303 => "Command.MouseLeave",
            304 => "Command.MouseEnter",
            378 => "Command.ExpandTreeNode",
            379 => "Command.CollapseTreeNode",
            313 => "Command.ToggleCurrentRowMultiSelection",
            384 => "Command.GoToFirstChildNode",
            395 => "Command.GoToFirstRowWhileMultiSelecting",
            396 => "Command.GoToLastRowWhileMultiSelecting",
            409 => "Command.DragStart",
            410 => "Command.DragDrop",
            432 => "Command.RefreshSubForm",
            439 => "Command.ShowContextMenu",
            544 => "Command.ControlValueChanged",
            555 => "Command.GridColumnClick",
            370 => "ENV.Commands.CloseAllWindows",
            462 => "ENV.Commands.NextWindow",
            463 => "ENV.Commands.PreviousWindow",
            477 => "ENV.Commands.ContextLostFocus",
            478 => "ENV.Commands.ContextGotFocus",
            505 => "ENV.Commands.SingleInstanceAsyncTaskReactivated",
            155 => "Command.Help",
            156 => "Command.Help",
            _ => null
        };
    }
}

