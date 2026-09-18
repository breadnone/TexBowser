namespace TexBowser.Ui;

public static class KeyboardShortcuts
{
    public static BrowserShortcut? Map(Terminal.Gui.Input.Key key) => HotkeyManager.Map(key);
}
