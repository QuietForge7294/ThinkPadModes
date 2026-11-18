using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
internal static class WindowsPower
{
    internal static readonly Guid Eco = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
    internal static readonly Guid Turbo = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
    [DllImport("powrprof.dll")]
    private static extern int PowerGetUserConfiguredACPowerMode(out Guid mode);
    [DllImport("powrprof.dll")]
    private static extern int PowerGetUserConfiguredDCPowerMode(out Guid mode);
    [DllImport("powrprof.dll")]
    private static extern int PowerSetUserConfiguredACPowerMode(ref Guid mode);
    [DllImport("powrprof.dll")]
    private static extern int PowerSetUserConfiguredDCPowerMode(ref Guid mode);
    [DllImport("powrprof.dll")]
    private static extern int PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")]
    private static extern int PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("powrprof.dll")]
    private static extern int PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    private static extern int PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    private static extern int PowerReadACDefaultIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    private static extern int PowerReadDCDefaultIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    private static extern int PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, uint value);
    [DllImport("powrprof.dll")]
    private static extern int PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, uint value);
    private static Guid ActiveScheme()
    {
        IntPtr pointer;
        Check(PowerGetActiveScheme(IntPtr.Zero, out pointer));
        try { return (Guid)Marshal.PtrToStructure(pointer, typeof(Guid)); }
        finally { LocalFree(pointer); }
    }
    private static readonly Guid Processor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid Cooling = new Guid("94d3a615-a899-4ac5-ae2b-e4d8f634367f");
    internal static uint CoolingPolicy(bool mains, bool factory = false)
    {
        return ReadCooling(ActiveScheme(), mains, factory);
    }
    private static uint ReadCooling(Guid scheme, bool mains, bool factory)
    {
        Guid group = Processor;
        Guid setting = Cooling;
        uint value;
        int result;
        if (factory)
            result = mains ? PowerReadACDefaultIndex(IntPtr.Zero, ref scheme, ref group, ref setting, out value)
                : PowerReadDCDefaultIndex(IntPtr.Zero, ref scheme, ref group, ref setting, out value);
        else
            result = mains ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, out value)
                : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, out value);
        Check(result);
        return value;
    }
    private static void WriteCooling(Guid scheme, bool mains, uint value)
    {
        Guid group = Processor;
        Guid setting = Cooling;
        Check(mains ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, value)
            : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, value));
    }
    internal static void SetCooling(bool mains, uint? requested)
    {
        Guid scheme = ActiveScheme();
        uint previous = ReadCooling(scheme, mains, false);
        uint value = requested ?? ReadCooling(scheme, mains, true);
        if (value > 1)
            throw new InvalidOperationException("Politique de refroidissement inconnue.");
        if (previous == value) return;
        Guid acMode = Read(true);
        Guid dcMode = Read(false);
        WriteCooling(scheme, mains, value);
        try
        {
            if (ActiveScheme() != scheme)
                throw new InvalidOperationException("Le plan Windows a changé pendant l’opération.");
            Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
            Set(true, acMode);
            Set(false, dcMode);
            if (ReadCooling(scheme, mains, false) != value)
                throw new InvalidOperationException("Windows n’a pas confirmé la politique de refroidissement.");
        }
        catch (Exception error)
        {
            try
            {
                WriteCooling(scheme, mains, previous);
                if (ActiveScheme() == scheme)
                {
                    Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
                    Set(true, acMode);
                    Set(false, dcMode);
                }
            }
            catch (Exception recovery)
            {
                throw new InvalidOperationException("Le retour au réglage précédent a échoué. " + recovery.Message, error);
            }
            throw;
        }
    }
    internal static bool OnMains()
    {
        var source = SystemInformation.PowerStatus.PowerLineStatus;
        if (source == PowerLineStatus.Unknown)
            throw new InvalidOperationException("Windows ne peut pas identifier la source d’alimentation.");
        return source == PowerLineStatus.Online;
    }
    internal static Guid Read(bool mains)
    {
        Guid mode;
        Check(mains ? PowerGetUserConfiguredACPowerMode(out mode) : PowerGetUserConfiguredDCPowerMode(out mode));
        return mode;
    }
    internal static void Set(bool mains, Guid mode)
    {
        if (Read(mains) == mode)
            return;
        Check(mains ? PowerSetUserConfiguredACPowerMode(ref mode) : PowerSetUserConfiguredDCPowerMode(ref mode));
        if (Read(mains) != mode)
            throw new InvalidOperationException("Windows n’a pas confirmé le changement de mode.");
    }
    private static void Check(int result)
    {
        if (result != 0)
            throw new Win32Exception(result);
    }
    internal static string Name(Guid mode)
    {
        if (mode == Eco) return "Éco";
        if (mode == Turbo) return "Turbo";
        if (mode == Guid.Empty) return "Normal";
        return "Personnalisé";
    }
}
internal sealed class ModesWindow : Form
{
    private static readonly Color Surface = Color.FromArgb(24, 35, 51);
    private static readonly Color Border = Color.FromArgb(50, 65, 85);
    private static readonly Color TextColor = Color.FromArgb(237, 242, 252);
    private static readonly Color Muted = Color.FromArgb(177, 192, 212);
    private static readonly Color Accent = Color.FromArgb(113, 189, 255);
    private static readonly Color Pink = Color.FromArgb(230, 159, 231);
    private readonly Label power;
    private readonly Label cooling;
    private readonly Label feedback;
    private readonly Label rpmMain;
    private readonly Label rpmSecondary;
    private readonly Label rpmSource;
    private readonly Timer timer = new Timer { Interval = 2000 };
    private readonly ModeButton[] modes = new ModeButton[3];
    private readonly Guid[] ids = { WindowsPower.Eco, Guid.Empty, WindowsPower.Turbo };
    private FanReading manualReading;
    private System.Threading.Tasks.Task<FanReading> pendingReading;
    private DateTime readingStarted;
    internal ModesWindow()
    {
        Text = "ThinkPad Modes";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(758, 658);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(13, 21, 33);
        ForeColor = TextColor;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        LabelAt(this, "ThinkPad Modes", 25, 18, 704, 48, 26, TextColor, true);
        LabelAt(this, "ThinkPad P14s Gen 6 Intel", 27, 69, 704, 25, 11, Muted);
        var sensors = Card(24, 104, 710, 187);
        LabelAt(sensors, "Ventilateurs", 19, 13, 340, 30, 15, TextColor, true);
        ButtonAt(sensors, "Ouvrir Lenovo Diagnostics", 389, 13, 300, 32, OpenLenovoDiagnostics);
        LabelAt(sensors, "Principal", 20, 55, 306, 23, 11, Muted);
        LabelAt(sensors, "Secondaire", 369, 55, 305, 23, 11, Muted);
        rpmMain = LabelAt(sensors, "— tr/min", 20, 80, 306, 43, 23, Accent, true);
        rpmSecondary = LabelAt(sensors, "— tr/min", 369, 80, 305, 43, 23, Pink, true);
        rpmSource = LabelAt(sensors, "En attente du moniteur thermique Lenovo.", 20, 135, 450, 36, 10, Muted);
        ButtonAt(sensors, "Saisir les RPM", 485, 134, 204, 36, EnterReading);
        var performance = Card(24, 306, 710, 129);
        LabelAt(performance, "Performances", 19, 12, 345, 30, 15, TextColor, true);
        power = LabelAt(performance, "", 355, 18, 335, 26, 11, Accent, true);
        power.TextAlign = ContentAlignment.MiddleRight;
        string[] names = { "Éco", "Normal", "Turbo" };
        for (int i = 0; i < names.Length; i++)
        {
            int mode = i;
            modes[i] = ButtonAt(performance, names[i], 20 + i * 225, 52, 218, 40, delegate {
                WindowsPower.Set(WindowsPower.OnMains(), ids[mode]);
                feedback.Text = names[mode] + " confirmé par Windows.";
            });
        }
        LabelAt(performance, "Le profil de performances agit aussi sur le refroidissement Lenovo.", 20, 98, 669, 25, 10, Muted);
        var coolingCard = Card(24, 450, 710, 111);
        LabelAt(coolingCard, "Contrôle de la ventilation", 19, 12, 550, 30, 15, TextColor, true);
        ButtonAt(coolingCard, "Bas", 20, 47, 160, 31, null);
        ModeButton automatic = ButtonAt(coolingCard, "Auto Lenovo", 190, 47, 160, 31, null);
        automatic.Selected = true;
        ButtonAt(coolingCard, "Renforcé", 360, 47, 160, 31, null);
        ButtonAt(coolingCard, "0 dB", 530, 47, 158, 31, null);
        LabelAt(coolingCard, "Bas, Renforcé et 0 dB nécessitent une commande matérielle validée.", 20, 81, 670, 24, 10, Muted);
        ButtonAt(this, "Rétablir la stratégie Windows", 24, 576, 255, 35, delegate {
            bool mains = WindowsPower.OnMains();
            WindowsPower.SetCooling(mains, null);
            feedback.Text = "Défaut de refroidissement Windows confirmé.";
        });
        ButtonAt(this, "Diagnostic Windows", 289, 576, 205, 35, OpenWindowsDiagnostics);
        ButtonAt(this, "Paramètres d’alimentation", 504, 576, 230, 35, delegate {
            using (Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true })) { }
        });
        cooling = LabelAt(this, "", 24, 616, 708, 20, 9, Muted);
        feedback = LabelAt(this, "", 24, 636, 708, 20, 9, Accent);
        timer.Tick += delegate { RefreshMode(); RefreshFans(); };
        Shown += delegate { RefreshFans(); timer.Start(); };
        FormClosed += delegate { timer.Dispose(); };
        RefreshMode();
    }
    private Panel Card(int x, int y, int width, int height)
    {
        var panel = new Panel { BackColor = Surface };
        panel.SetBounds(x, y, width, height);
        panel.Paint += delegate(object sender, PaintEventArgs args) {
            using (var pen = new Pen(Border))
                args.Graphics.DrawRectangle(pen, 0, 0, panel.ClientSize.Width - 1, panel.ClientSize.Height - 1);
        };
        Controls.Add(panel);
        return panel;
    }
    private static Label LabelAt(Control parent, string text, int x, int y, int width, int height, int size, Color color, bool bold = false)
    {
        var label = new Label {
            Text = text, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = color, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft
        };
        label.SetBounds(x, y, width, height);
        parent.Controls.Add(label);
        return label;
    }
    private ModeButton ButtonAt(Control parent, string text, int x, int y, int width, int height, Action action)
    {
        var button = new ModeButton { Text = text, Enabled = action != null };
        button.SetBounds(x, y, width, height);
        if (action != null)
        {
            button.Click += delegate {
                try { action(); }
                catch (Exception ex) { feedback.Text = "Action non confirmée · " + ex.Message; }
                RefreshMode();
            };
        }
        parent.Controls.Add(button);
        return button;
    }
    private sealed class ModeButton : Button
    {
        private bool selected;
        private bool hovered;
        internal bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value;
                AccessibleDescription = value ? "Sélectionné" : null;
                Invalidate();
            }
        }
        internal ModeButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent.BackColor);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle box = Rectangle.Inflate(ClientRectangle, -1, -1);
            int corner = Math.Min(12, box.Height);
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddArc(box.Left, box.Top, corner, corner, 180, 90);
                path.AddArc(box.Right - corner, box.Top, corner, corner, 270, 90);
                path.AddArc(box.Right - corner, box.Bottom - corner, corner, corner, 0, 90);
                path.AddArc(box.Left, box.Bottom - corner, corner, corner, 90, 90);
                path.CloseFigure();
                Color fill = Selected ? Color.FromArgb(31, 70, 105) :
                    Enabled && hovered ? Color.FromArgb(54, 73, 99) : Color.FromArgb(34, 47, 65);
                using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                using (var outline = new Pen(Selected || Focused ? Accent : Border))
                    e.Graphics.DrawPath(outline, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, box, Selected ? Accent : Enabled ? TextColor : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
    private void OpenLenovoDiagnostics()
    {
        Process[] existing = Process.GetProcessesByName("NextGenLenovoDiagnostics");
        foreach (Process process in existing)
            process.Dispose();
        if (existing.Length == 0)
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Lenovo", "Lenovo Diagnostics Evolution", "NextGenLenovoDiagnostics.exe");
            if (!File.Exists(file))
                throw new FileNotFoundException("Lenovo Diagnostics Evolution est introuvable.");
            using (Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file) })) { }
        }
        feedback.Text = "Dans Lenovo Diagnostics, ouvre Outils → Moniteur thermique → Processeur.";
    }
    private void OpenWindowsDiagnostics()
    {
        string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DiagnosticVentilation.exe");
        if (!File.Exists(file))
            throw new FileNotFoundException("Diagnostic Windows introuvable dans le dossier de l’application.");
        using (Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })) { }
        feedback.Text = "Diagnostic Windows lancé.";
    }
    private void RefreshFans()
    {
        if (pendingReading != null)
        {
            if (!pendingReading.IsCompleted)
            {
                if (DateTime.UtcNow - readingStarted > TimeSpan.FromSeconds(6))
                    DisplayReading(null);
                return;
            }
            FanReading live = pendingReading.Status == System.Threading.Tasks.TaskStatus.RanToCompletion
                ? pendingReading.Result : null;
            if (pendingReading.IsFaulted)
                Debug.WriteLine(pendingReading.Exception);
            DisplayReading(live);
        }
        readingStarted = DateTime.UtcNow;
        pendingReading = System.Threading.Tasks.Task.Run((Func<FanReading>)FanReading.ReadLenovo);
    }
    private void DisplayReading(FanReading live)
    {
        bool isLive = live != null && live.Any;
        FanReading reading = isLive ? live : manualReading;
        rpmMain.Text = FormatRpm(reading == null ? null : reading.Main);
        rpmSecondary.Text = FormatRpm(reading == null ? null : reading.Secondary);
        rpmSource.Text = reading == null ? "Ouvre le moniteur thermique Lenovo ou saisis un relevé." :
            (isLive ? "Lenovo Diagnostics · lecture à " : "Relevé manuel · saisi à ") + reading.Timestamp.ToString("HH:mm:ss");
    }
    private static string FormatRpm(int? rpm)
    {
        return rpm.HasValue ? rpm.Value.ToString("N0") + " tr/min" : "— tr/min";
    }
    private void EnterReading()
    {
        using (var dialog = new Form {
            Text = "Saisir un relevé Lenovo", ClientSize = new Size(410, 205), Font = Font,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, BackColor = Surface, ForeColor = TextColor
        })
        {
            LabelAt(dialog, "Principal (tr/min)", 20, 20, 165, 30, 11, TextColor);
            LabelAt(dialog, "Secondaire (tr/min)", 20, 66, 165, 30, 11, TextColor);
            var main = new TextBox { MaxLength = 5, Text = manualReading != null ? manualReading.Main.ToString() : "" };
            var secondary = new TextBox { MaxLength = 5, Text = manualReading != null ? manualReading.Secondary.ToString() : "" };
            main.SetBounds(195, 22, 185, 30);
            secondary.SetBounds(195, 68, 185, 30);
            dialog.Controls.AddRange(new Control[] { main, secondary });
            var error = LabelAt(dialog, "", 20, 108, 365, 36, 10, Pink);
            var save = new Button { Text = "Afficher le relevé", BackColor = Surface, FlatStyle = FlatStyle.Flat };
            save.SetBounds(195, 151, 185, 36);
            dialog.Controls.Add(save);
            dialog.AcceptButton = save;
            save.Click += delegate {
                int primary;
                int second;
                if (!int.TryParse(main.Text, out primary) || !int.TryParse(secondary.Text, out second) ||
                    primary < 0 || second < 0 || primary > 99999 || second > 99999)
                {
                    error.Text = "Entre les deux vitesses en chiffres.";
                    return;
                }
                manualReading = new FanReading(primary, second);
                dialog.DialogResult = DialogResult.OK;
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                DisplayReading(null);
        }
    }
    private void RefreshMode()
    {
        try
        {
            bool ac = WindowsPower.OnMains();
            Guid mode = WindowsPower.Read(ac);
            power.Text = WindowsPower.Name(mode) + (ac ? "  /  Secteur" : "  /  Batterie");
            for (int i = 0; i < modes.Length; i++)
            {
                modes[i].Enabled = true;
                modes[i].Selected = mode == ids[i];
            }
            try
            {
                uint current = WindowsPower.CoolingPolicy(ac);
                uint standard = WindowsPower.CoolingPolicy(ac, true);
                cooling.Text = "Stratégie Windows · " + CoolingName(current) + "  /  Défaut · " + CoolingName(standard);
            }
            catch (Exception ex) { cooling.Text = "Stratégie Windows illisible · " + ex.Message; }
        }
        catch (Exception ex)
        {
            power.Text = "Profil Windows inaccessible";
            cooling.Text = ex.Message;
            foreach (ModeButton button in modes)
            {
                button.Enabled = false;
                button.Selected = false;
            }
        }
    }
    private static string CoolingName(uint policy)
    {
        return policy == 0 ? "passive" : policy == 1 ? "active" : policy.ToString();
    }
    [STAThread]
    private static int Main(string[] args) {
        if (args.Length == 2 && args[0] == "--rpm-status")
        {
            try
            {
                FanReading reading = FanReading.ReadLenovo();
                File.WriteAllText(args[1], "principal=" + reading.Main + Environment.NewLine + "secondaire=" + reading.Secondary);
                return reading.Any ? 0 : 1;
            }
            catch (Exception ex)
            {
                File.WriteAllText(args[1], ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }
        if (args.Length > 0 && args[0] == "--status") {
            var lines = new System.Collections.Generic.List<string>();
            int code = 0;
            foreach (bool ac in new bool[] {true,false}) {
                string source = ac ? "AC" : "DC";
                try { lines.Add(source + " mode=" + WindowsPower.Read(ac)); }
                catch(Exception ex) { lines.Add(source + " mode ERROR=" + ex.Message); code=1; }
                try { lines.Add(source + " cooling_policy=" + WindowsPower.CoolingPolicy(ac) + " default=" + WindowsPower.CoolingPolicy(ac,true)); }
                catch(Exception ex) { lines.Add(source + " policy ERROR=" + ex.Message); code=1; }
            }
            lines.Add("Fan control=Lenovo automatic; manual and zero RPM not validated; RPM source=Lenovo Diagnostics Evolution");
            string text = String.Join(Environment.NewLine, lines.ToArray());
            if(args.Length == 2) System.IO.File.WriteAllText(args[1], text);
            else Console.WriteLine(text);
            return code;
        }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using(var window = new ModesWindow()) Application.Run(window);
        return 0;
    }
}
