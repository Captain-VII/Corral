using System.Runtime.InteropServices;

namespace Corral.UI;

/// <summary>Raccourcis clavier globaux (actifs même quand Corral est caché), via RegisterHotKey.</summary>
public sealed class GlobalHotkeys : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    readonly Dictionary<int, Action> actions = new();
    int nextId = 1;

    public GlobalHotkeys() => CreateHandle(new CreateParams());

    /// <summary>Renvoie false si la combinaison est déjà prise par une autre application.</summary>
    public bool Register(Keys keys, Action action)
    {
        uint mods = MOD_NOREPEAT;
        if (keys.HasFlag(Keys.Control)) mods |= MOD_CONTROL;
        if (keys.HasFlag(Keys.Alt)) mods |= MOD_ALT;
        if (keys.HasFlag(Keys.Shift)) mods |= MOD_SHIFT;
        int id = nextId++;
        if (!RegisterHotKey(Handle, id, mods, (uint)(keys & Keys.KeyCode)))
            return false;
        actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in actions.Keys)
            UnregisterHotKey(Handle, id);
        actions.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && actions.TryGetValue((int)m.WParam, out var action))
            action();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }

    public static string Format(Keys? keys)
    {
        if (keys is not { } k || (k & Keys.KeyCode) == Keys.None)
            return Tr("Aucun", "None");
        var parts = new List<string>();
        if (k.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (k.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (k.HasFlag(Keys.Shift)) parts.Add(Tr("Maj", "Shift"));
        var code = k & Keys.KeyCode;
        parts.Add(code switch
        {
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (code - Keys.D0))).ToString(),
            >= Keys.NumPad0 and <= Keys.NumPad9 => Tr("Pavé ", "Num ") + (code - Keys.NumPad0),
            _ => code.ToString(),
        });
        return string.Join(" + ", parts);
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

/// <summary>
/// Champ de saisie d'un raccourci : on appuie sur la combinaison voulue (Ctrl ou Alt obligatoire),
/// Retour arrière ou Suppr pour n'en mettre aucun.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    Keys? value;

    public HotkeyBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Width = 170;
        TextAlign = HorizontalAlignment.Center;
        Cursor = Cursors.Hand;
        Text = GlobalHotkeys.Format(null);
    }

    public event EventHandler? ValueChanged;

    public Keys? Value
    {
        get => value;
        set
        {
            this.value = value;
            Text = GlobalHotkeys.Format(value);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
        var code = e.KeyCode;
        if (code is Keys.Back or Keys.Delete && e.Modifiers == Keys.None)
        {
            Value = null;
            ValueChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (code is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin)
        {
            var mods = new List<string>();
            if (e.Control) mods.Add("Ctrl");
            if (e.Alt) mods.Add("Alt");
            if (e.Shift) mods.Add(Tr("Maj", "Shift"));
            mods.Add("…");
            Text = string.Join(" + ", mods);
            return;
        }
        if (!e.Control && !e.Alt)
        {
            Text = Tr("Ctrl ou Alt + une touche", "Ctrl or Alt + a key");
            return;
        }
        Value = e.KeyData;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLeave(EventArgs e)
    {
        Text = GlobalHotkeys.Format(value); // efface une saisie incomplète
        base.OnLeave(e);
    }

    // Capter les flèches, Entrée… mais laisser Tab / Maj+Tab passer au champ suivant
    protected override bool IsInputKey(Keys keyData) => keyData is not (Keys.Tab or (Keys.Tab | Keys.Shift));
}
