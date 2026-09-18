using Terminal.Gui.Input;

namespace TexBowser.Ui;

public enum BrowserShortcut
{
    NewTab,
    CloseTab,
    NextTab,
    PrevTab,
    FocusUrl,
    Reload,
    MoveTabLeft,
    MoveTabRight,
    Find,
}

public static class HotkeyManager
{
    public static BrowserShortcut? Map(Key key)
    {
        if (key is null || !key.IsCtrl || key.IsAlt) return null;
        var plain = key.NoCtrl.NoShift;
        if (key.IsShift)
        {
            if (plain == Key.Tab) return BrowserShortcut.PrevTab;
            if (plain == Key.CursorLeft) return BrowserShortcut.MoveTabLeft;
            if (plain == Key.CursorRight) return BrowserShortcut.MoveTabRight;
            return null;
        }
        if (plain == Key.T) return BrowserShortcut.NewTab;
        if (plain == Key.W) return BrowserShortcut.CloseTab;
        if (plain == Key.Tab) return BrowserShortcut.NextTab;
        if (plain == Key.L) return BrowserShortcut.FocusUrl;
        if (plain == Key.R) return BrowserShortcut.Reload;
        if (plain == Key.F) return BrowserShortcut.Find;
        return null;
    }

    public static string HintLine =>
        "Ctrl+T new tab  Ctrl+W close  Ctrl+Tab next  Ctrl+Shift+Tab previous  Ctrl+L url  Ctrl+R reload  Ctrl+F find  Ctrl+Shift+Left/Right move tab";
}
