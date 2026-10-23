using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Windows.Forms;

namespace Cheesecloth.Tray;

internal static class Ui
{
    public static readonly Color Good = Color.ForestGreen;
    public static readonly Color Attention = Color.DarkOrange;
    public static readonly Color Broken = Color.Firebrick;
    public static readonly Color Off = Color.DimGray;
    public static readonly Color Info = Color.SlateGray;

    // The light colors of Windows 11 apps. With high contrast on, the system
    // colors instead, so that the user's theme applies.
    private static bool HighContrast => SystemInformation.HighContrast;
    public static Color WindowBack => HighContrast ? SystemColors.Control : Color.FromArgb(243, 243, 243);
    public static Color CardBack => HighContrast ? SystemColors.Window : Color.White;
    public static Color Line => HighContrast ? SystemColors.WindowText : Color.FromArgb(229, 229, 229);
    public static Color Border => HighContrast ? SystemColors.WindowText : Color.FromArgb(209, 209, 209);
    public static Color SecondaryText => HighContrast ? SystemColors.WindowText : Color.FromArgb(96, 96, 96);
    public static Color Accent => HighContrast ? SystemColors.Highlight : Color.FromArgb(0, 95, 184);
    public static Color SelectedBack => HighContrast ? SystemColors.Highlight : Color.FromArgb(230, 238, 247);

    // The fonts of Windows 11 (Segoe UI Variable), or of Windows 10. They
    // follow the Windows text size, as the message box font does.
    private static readonly HashSet<string> Families = FontFamily.Families.Select(family => family.Name).ToHashSet();
    private static readonly float BodySize = SystemFonts.MessageBoxFont!.SizeInPoints * 7 / 6;
    public static readonly Font Body = NewFont(BodySize, "Segoe UI Variable Text", "Segoe UI");
    public static readonly Font Strong = NewFont(BodySize, "Segoe UI Variable Text Semibold", "Segoe UI Semibold");
    public static readonly Font Title = NewFont(BodySize * 1.5f, "Segoe UI Variable Display Semibold", "Segoe UI Semibold");
    // Segoe Fluent Icons (Windows 11) keeps the code points of Segoe MDL2
    // Assets (Windows 10).
    public static readonly string IconFamily = Families.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    public static readonly Font Icon = new(IconFamily, BodySize);

    private static Font NewFont(float size, params string[] names) =>
        new(names.FirstOrDefault(Families.Contains) ?? SystemFonts.MessageBoxFont!.FontFamily.Name, size);

    // Brings an open window to the front, restoring it if it is minimized.
    public static void Raise(Form form)
    {
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    public static Label Label(string text, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        Font = bold ? Strong : Body,
        Margin = new Padding(3, 6, 3, 6),
    };

    // The large heading at the top of a page.
    public static Label Heading(string text)
    {
        var label = Label(text);
        label.Font = Title;
        label.Margin = new Padding(3, 0, 3, 4);
        return label;
    }

    // Text that explains, in gray.
    public static Label Note(string text)
    {
        var label = Label(text);
        label.ForeColor = SecondaryText;
        return label;
    }

    // A light line across the page.
    public static Panel Separator() => new() { Height = 1, BackColor = Line, Margin = new Padding(3, 14, 3, 14) };

    public static Button Button(string text, EventHandler click, bool primary = false)
    {
        var button = new FlatButton { Text = text, AutoSize = true, MinimumSize = new Size(100, 32), Primary = primary };
        button.Click += click;
        return button;
    }

    // A row of buttons at the bottom of a dialog, on the window color.
    internal static FlowLayoutPanel Footer() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
        BackColor = WindowBack, Padding = new Padding(16, 12, 16, 12), Margin = Padding.Empty,
    };

    internal static (Panel Area, Label Text) MessageArea(string text, string name)
    {
        var area = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var message = Label(text);
        message.AccessibleName = name;
        message.Location = new Point(21, 18);
        area.Controls.Add(message);
        area.SizeChanged += (_, _) => message.MaximumSize = new Size(Math.Max(200, area.ClientSize.Width - 48), 0);
        return (area, message);
    }

    public static bool Confirm(string title, string body, string action, string cancelText = "Cancel")
    {
        using var dialog = CreateConfirmation(title, body, action, cancelText);
        return dialog.ShowDialog() == DialogResult.OK;
    }

    internal static Form CreateConfirmation(string title, string body, string action, string cancelText = "Cancel")
    {
        var dialog = new Form
        {
            Text = title, ClientSize = new Size(480, 200), MinimumSize = new Size(480, 200),
            StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, Font = Body, BackColor = CardBack,
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var (messageArea, _) = MessageArea(body, "Confirmation details");
        layout.Controls.Add(messageArea, 0, 0);
        var actions = Footer();
        var cancel = new FlatButton { Text = cancelText, DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(100, 32) };
        var confirm = new FlatButton { Text = action, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(110, 32) };
        actions.Controls.Add(confirm);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 1);
        dialog.Controls.Add(layout);
        dialog.CancelButton = cancel;
        dialog.AcceptButton = cancel;
        return dialog;
    }

    public static void Message(string text) => MessageBox.Show(text, "Cheesecloth", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void OpenLogs() => Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true });

    public static string PortBadge(JsonElement status, string port, out string hint)
    {
        hint = "Port mapping is off.";
        var maps = status.Get("port_mapping");
        if (maps.ValueKind != JsonValueKind.Array) return "Off";
        foreach (var map in maps.Items())
        {
            if (map.Text("port") != port) continue;
            var grant = map.Get("mapped");
            var error = map.Text("error");
            if (grant.ValueKind == JsonValueKind.Object)
            {
                hint = $"{grant.Text("external")} via {grant.Text("method")}" +
                    (error.Length > 0 ? $"; renewal failed: {error}" : "");
                return "Mapped";
            }
            if (error.Length > 0) { hint = error; return "Not mapped"; }
            hint = "Asking the router for a mapping.";
            return "Requesting";
        }
        return "Off";
    }
}

// A Windows 11 button: rounded, with a light border, or filled with the
// accent color for the main action. With high contrast on, it is the
// standard button.
internal sealed class FlatButton : Button
{
    private bool hover;
    private bool pressed;

    public bool Primary { get; init; }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        var graphics = e.Graphics;
        graphics.Clear(Parent?.BackColor ?? Ui.CardBack);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        var corner = new Size(4, 4);
        Color back, border, text;
        if (Primary)
        {
            back = !Enabled ? Color.FromArgb(191, 191, 191) : pressed ? Color.FromArgb(0, 84, 166) :
                hover ? Color.FromArgb(25, 110, 191) : Ui.Accent;
            border = back;
            text = Color.White;
        }
        else
        {
            back = !Enabled ? Color.FromArgb(245, 245, 245) : pressed ? Color.FromArgb(240, 240, 240) :
                hover ? Color.FromArgb(246, 246, 246) : Color.FromArgb(251, 251, 251);
            border = Ui.Border;
            text = Enabled ? ForeColor : SystemColors.GrayText;
        }
        using (var brush = new SolidBrush(back)) graphics.FillRoundedRectangle(brush, box, corner);
        using (var pen = new Pen(border)) graphics.DrawRoundedRectangle(pen, box, corner);
        TextRenderer.DrawText(graphics, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(SystemColors.ControlText, 2);
            graphics.DrawRoundedRectangle(focus, Rectangle.Inflate(box, -2, -2), corner);
        }
    }
}

// A white panel with rounded corners and a light border. Its controls go in
// `Body`, which stays inside the corners.
internal sealed class Card : Panel
{
    private const int Radius = 8;

    public Panel Body { get; } = new() { Dock = DockStyle.Fill, BackColor = Ui.CardBack };

    public Card()
    {
        Dock = DockStyle.Fill;
        BackColor = Ui.WindowBack;
        Padding = new Padding(Radius);
        DoubleBuffered = true;
        ResizeRedraw = true;
        Controls.Add(Body);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        using var brush = new SolidBrush(Ui.CardBack);
        using var pen = new Pen(Ui.Line);
        e.Graphics.FillRoundedRectangle(brush, box, new Size(Radius, Radius));
        e.Graphics.DrawRoundedRectangle(pen, box, new Size(Radius, Radius));
    }
}

// A page of controls, one below the other, that scrolls when it is too tall.
// A stretched control takes the page's width, and labels wrap to it.
internal sealed class PageLayout : Panel
{
    private readonly TableLayoutPanel column = new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
    };

    public PageLayout()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Padding = new Padding(24, 18, 24, 18);
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        column.SizeChanged += (_, _) => { foreach (Control control in column.Controls) Wrap(control); };
        Controls.Add(column);
    }

    public T Add<T>(T control, bool stretch = false) where T : Control
    {
        if (stretch) control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        column.Controls.Add(control, 0, column.RowCount++);
        Wrap(control);
        return control;
    }

    // Removes and disposes every control but `keep`.
    public void Clear(Control? keep = null)
    {
        foreach (var control in column.Controls.Cast<Control>().Where(c => c != keep).ToArray()) control.Dispose();
        column.Controls.Clear();
        column.RowStyles.Clear();
        column.RowCount = 0;
    }

    private void Wrap(Control control)
    {
        if (control is Label { AutoSize: true } label)
            label.MaximumSize = new Size(Math.Max(100, column.ClientSize.Width - label.Margin.Horizontal), 0);
    }
}

internal abstract class PageForm : Form
{
    protected readonly DaemonClient Client;
    protected readonly Label MessageLabel = Ui.Label("");
    private readonly Card card = new();
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 5000 };
    private readonly List<Button> actionButtons = [];
    private bool busy;
    private bool refreshing;

    protected PageForm(string title, DaemonClient client, int width = 550, int height = 430)
    {
        Client = client;
        Text = $"Cheesecloth — {title}";
        ClientSize = new Size(width, height);
        MinimumSize = new Size(width, height);
        Font = Ui.Body;
        BackColor = Ui.WindowBack;
        Padding = new Padding(12);
        StartPosition = FormStartPosition.CenterScreen;
        MessageLabel.AccessibleName = "Result";
        Controls.Add(card);
        poll.Tick += async (_, _) => await RefreshSafeAsync();
        Shown += async (_, _) => { poll.Start(); await RefreshSafeAsync(); };
        FormClosed += (_, _) => poll.Dispose();
    }

    // The inside of the window's card, where its controls go.
    protected Panel Body => card.Body;

    protected Button Action(string text, Func<Task> action, bool primary = false)
    {
        var button = Ui.Button(text, async (_, _) => await RunAsync(action), primary);
        actionButtons.Add(button);
        return button;
    }

    protected async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        foreach (var button in actionButtons) button.Enabled = false;
        MessageLabel.Text = "Working…";
        try { await action(); }
        catch (Exception ex) { if (!IsDisposed) MessageLabel.Text = ex.Message; }
        finally
        {
            busy = false;
            if (!IsDisposed)
                foreach (var button in actionButtons) button.Enabled = true;
        }
    }

    protected void ClearDynamicActions() => actionButtons.Clear();

    private async Task RefreshSafeAsync()
    {
        if (refreshing || busy || IsDisposed) return;
        refreshing = true;
        try { await RefreshAsync(); }
        catch (Exception ex) { if (!IsDisposed) MessageLabel.Text = ex.Message; }
        finally { refreshing = false; }
    }

    protected virtual Task RefreshAsync() => Task.CompletedTask;
}

internal sealed class OperationForm : Form
{
    public OperationForm(string title, Func<Task<string>> operation)
    {
        Text = $"Cheesecloth — {title}";
        ClientSize = new Size(460, 180);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Body;
        BackColor = Ui.CardBack;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var (messageArea, message) = Ui.MessageArea("Working…", "Result");
        var progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee, Dock = DockStyle.Fill, AccessibleName = "Progress",
            Margin = new Padding(24, 0, 24, 12),
        };
        var close = Ui.Button("Close", (_, _) => Close());
        close.Enabled = false;
        var footer = Ui.Footer();
        footer.Controls.Add(close);
        layout.Controls.Add(messageArea, 0, 0);
        layout.Controls.Add(progress, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        Shown += async (_, _) =>
        {
            try
            {
                var result = await operation();
                if (!IsDisposed) message.Text = result;
            }
            catch (Exception ex) { if (!IsDisposed) message.Text = ex.Message; }
            finally
            {
                if (!IsDisposed) { progress.Visible = false; close.Enabled = true; }
            }
        };
    }
}

internal sealed class CreateForm : PageForm
{
    private readonly TextBox range = new() { Width = 300, BorderStyle = BorderStyle.FixedSingle };
    private readonly CheckBox strict = new() { Text = "Strict security", AutoSize = true };

    public CreateForm(DaemonClient client) : base("Create Network", client, 560, 420)
    {
        var page = new PageLayout();
        page.Add(Ui.Heading("Create a network"));
        page.Add(Ui.Note("This computer becomes the network's first member."));
        page.Add(Ui.Separator(), stretch: true);
        page.Add(Ui.Label("IPv4 range (empty: a random /24 in 100.64.0.0/10)"));
        page.Add(range);
        page.Add(strict);
        page.Add(Ui.Note("Keep Byzantine protection once there are enough members, and pause changes instead of giving it up."));
        page.Add(Action("Create", async () =>
        {
            var args = new List<string> { "init" };
            if (strict.Checked) args.Add("--strict-security");
            if (range.Text.Trim().Length > 0) { args.Add("--ipv4-range"); args.Add(range.Text.Trim()); }
            var result = await Client.CallAsync([.. args]);
            if (IsDisposed) return;
            MessageLabel.Text = $"Created network {Json.ShortId(result.Text("cluster_id"))}. IPv4 range: {result.Text("ipv4_range")}";
        }, primary: true));
        page.Add(MessageLabel);
        Body.Controls.Add(page);
    }
}

internal sealed class JoinForm : PageForm
{
    private readonly TextBox token = new()
    {
        Multiline = true, Height = 100, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle,
    };

    public JoinForm(DaemonClient client) : base("Join Network", client, 560, 440)
    {
        var page = new PageLayout();
        page.Add(Ui.Heading("Join a network"));
        page.Add(Ui.Note("Paste the token from Peers > Invite on a member."));
        page.Add(Ui.Separator(), stretch: true);
        page.Add(token, stretch: true);
        var join = Action("Join", async () =>
        {
            var result = await Client.CallAsync("join", token.Text.Trim());
            if (IsDisposed) return;
            MessageLabel.Text = result.Flag("joined") ? $"Joined. This node's address is {result.Text("ipv4")}." :
                $"Waiting for approval. Ask members to check this node ID: {result.Text("node_id")}";
        }, primary: true);
        join.Enabled = false;
        token.TextChanged += (_, _) => join.Enabled = token.Text.Trim().Length > 0;
        page.Add(join);
        page.Add(MessageLabel);
        Body.Controls.Add(page);
    }
}

internal sealed class StatusForm : PageForm
{
    // Glyphs of Segoe Fluent Icons for each section, and for the info buttons.
    private static readonly Dictionary<string, string> SectionGlyphs = new()
    {
        ["Overview"] = "",     // Home
        ["Network"] = "",      // Globe
        ["Consensus"] = "",    // People
        ["Security"] = "",     // Shield
        ["Connectivity"] = "", // Link
    };
    private const string InfoGlyph = "";

    private readonly ListBox sections = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false,
        DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 56, AccessibleName = "Status sections",
    };
    private readonly PageLayout page = new();
    /// The rows of the shown section, in one grid so that their columns line up.
    private TableLayoutPanel grid = new();
    private readonly List<Label> values = [];
    private JsonElement status;
    private readonly ToolTip tips = new();
    private Font? iconFont;

    private static readonly string[] MemberSections = ["Overview", "Network", "Consensus", "Security", "Connectivity"];

    // `Health` names the section's state, and `Description` says what it means.
    private sealed record SectionChoice(string Name, string Health, Color Color, string Description)
    {
        public override string ToString() => $"{Name}, {Health}";
    }

    public StatusForm(DaemonClient client) : base("Status", client, 860, 560)
    {
        var nav = new Panel { Dock = DockStyle.Left, Width = 240, Padding = new Padding(0, 0, 12, 0) };
        sections.BackColor = Ui.WindowBack;
        nav.Controls.Add(sections);
        Body.Controls.Add(page);
        Controls.Add(nav);
        UpdateMetrics();
        sections.DrawItem += DrawSection;
        sections.SelectedIndexChanged += (_, _) => Render();
        sections.FontChanged += (_, _) => UpdateMetrics();
        FormClosed += (_, _) => { tips.Dispose(); iconFont?.Dispose(); };
    }

    // Sizes the section icons and items to the list's font.
    private void UpdateMetrics()
    {
        iconFont?.Dispose();
        iconFont = new Font(Ui.IconFamily, sections.Font.SizeInPoints * 1.3f);
        sections.ItemHeight = Math.Min(255, Math.Max(56, sections.Font.Height * 2 + 24));
    }

    protected override async Task RefreshAsync()
    {
        status = await Client.CallAsync("status");
        if (IsDisposed) return;
        string[] names = status.Text("phase") == "member" ? MemberSections : ["Overview"];
        var selected = (sections.SelectedItem as SectionChoice)?.Name ?? "Overview";
        sections.BeginUpdate();
        sections.Items.Clear();
        foreach (var name in names) sections.Items.Add(Section(name));
        sections.SelectedIndex = Math.Max(0, Array.IndexOf(names, selected));
        sections.EndUpdate();
    }

    private SectionChoice Section(string name)
    {
        var phase = status.Text("phase");
        var warnings = status.Get("warnings");
        var warningCount = warnings.ValueKind == JsonValueKind.Array ? warnings.GetArrayLength() : 0;
        var relay = status.Flag("relay") ? " It relays messages for other members." : "";
        var ports = PortColor();
        return name switch
        {
            "Overview" when phase == "stopping" => new(name, "Leaving", Ui.Attention,
                "This computer is leaving the network. Its local cleanup is not finished yet."),
            "Overview" when warningCount > 0 => new(name, "Warnings", Ui.Attention, warningCount == 1
                ? "This computer is a member of the network. One thing needs attention."
                : $"This computer is a member of the network. {warningCount} things need attention."),
            "Overview" when phase == "pending" => new(name, "Waiting for approval", Ui.Attention,
                "This computer asked to join the network. It joins when enough members approve."),
            "Overview" when phase == "member" => new(name, "Healthy", Ui.Good,
                "This computer is a member of the network, and nothing needs attention."),
            "Overview" => new(name, "Not in a network", Ui.Off,
                "This computer is not in a network. Use the tray menu to create or join one."),
            "Network" when status.Flag("public") => new(name, "Public", Ui.Good,
                "Other members can reach this computer directly." + relay),
            "Network" => new(name, "Behind NAT", Ui.Off,
                "This computer is behind a NAT. Other members reach it through NAT hole punching." + relay),
            "Consensus" when status.Flag("changes_paused") => new(name, "Changes paused", Ui.Attention,
                "Too few acceptors can be reached. Joins, removals and setting changes wait. Traffic between members keeps flowing."),
            "Consensus" => new(name, "Available", Ui.Good,
                "Enough acceptors can be reached to agree on changes."),
            "Security" when status.Text("security") == "protected" => new(name, "Protected", Ui.Good,
                "Changes stay safe even if one acceptor is compromised and lies."),
            "Security" => new(name, "Relaxed", Ui.Info,
                "Changes are safe while every acceptor is honest. This is normal for small networks."),
            "Connectivity" when ports == Ui.Attention => new(name, "Needs attention", Ui.Attention,
                "The router did not forward a port. Other members may not reach this computer directly."),
            "Connectivity" when ports == Ui.Good => new(name, "Mapped", Ui.Good,
                "The router forwards a port to this computer, so other members can reach it directly."),
            _ => new(name, "No mapping", Ui.Off,
                "The router forwards no port to this computer. This is fine when NAT hole punching works."),
        };
    }

    // An item: the section's icon in its health color, its name, and its
    // health in gray. The selected item has a light background and an accent
    // mark at its left.
    private void DrawSection(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || iconFont == null) return;
        var graphics = e.Graphics;
        var section = (SectionChoice)sections.Items[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0;
        var highContrast = SystemInformation.HighContrast;
        using (var back = new SolidBrush(sections.BackColor)) graphics.FillRectangle(back, e.Bounds);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var item = Rectangle.Inflate(e.Bounds, -2, -3);
        var text = selected && highContrast ? SystemColors.HighlightText : SystemColors.ControlText;
        if (selected)
        {
            using var fill = new SolidBrush(Ui.SelectedBack);
            graphics.FillRoundedRectangle(fill, item, new Size(6, 6));
            using var mark = new SolidBrush(highContrast ? text : Ui.Accent);
            var markHeight = item.Height * 2 / 5;
            graphics.FillRoundedRectangle(mark, new Rectangle(item.Left, item.Top + (item.Height - markHeight) / 2, 3, markHeight), new Size(1, 1));
        }
        // A neutral health shows in the text color, or the accent when selected.
        var neutral = section.Color == Ui.Off || section.Color == Ui.Info;
        var iconColor = !neutral && !(selected && highContrast) ? section.Color : selected && !highContrast ? Ui.Accent : text;
        var icon = new Rectangle(item.Left + 14, item.Top, iconFont.Height, item.Height);
        TextRenderer.DrawText(graphics, SectionGlyphs[section.Name], iconFont, icon, iconColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var lineHeight = e.Font!.Height;
        var left = icon.Right + 12;
        var textBounds = new Rectangle(left, item.Top + (item.Height - lineHeight * 2) / 2, item.Right - left - 4, lineHeight);
        TextRenderer.DrawText(graphics, section.Name, e.Font, textBounds, text, TextFormatFlags.EndEllipsis);
        textBounds.Y += lineHeight;
        TextRenderer.DrawText(graphics, section.Health, e.Font, textBounds, selected && highContrast ? text : Ui.SecondaryText,
            TextFormatFlags.EndEllipsis);
        if ((e.State & DrawItemState.Focus) != 0 && (e.State & DrawItemState.NoFocusRect) == 0)
            ControlPaint.DrawFocusRectangle(graphics, item);
    }

    private Color PortColor()
    {
        var maps = status.Get("port_mapping");
        if (maps.ValueKind != JsonValueKind.Array) return Ui.Off;
        var entries = maps.Items().ToArray();
        if (entries.Any(m => m.Get("mapped").ValueKind != JsonValueKind.Object && m.Text("error").Length > 0))
            return Ui.Attention;
        return entries.Any(m => m.Get("mapped").ValueKind == JsonValueKind.Object) ? Ui.Good : Ui.Off;
    }

    // The page: the section's health as a heading, what it means, a line,
    // then the section's rows.
    private void Render()
    {
        if (status.ValueKind != JsonValueKind.Object || sections.SelectedItem is not SectionChoice section) return;
        page.SuspendLayout();
        ClearDynamicActions();
        page.Clear(keep: MessageLabel);
        values.Clear();
        page.Add(Ui.Heading(section.Health));
        page.Add(Ui.Note(section.Description));
        page.Add(Ui.Separator(), stretch: true);
        grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.SizeChanged += (_, _) => FitValues();
        page.Add(grid, stretch: true);
        switch (section.Name)
        {
            case "Overview": Overview(); break;
            case "Network": Network(); break;
            case "Consensus": Consensus(); break;
            case "Security": Security(); break;
            case "Connectivity": Connectivity(); break;
        }
        page.Add(MessageLabel);
        page.ResumeLayout();
        FitValues();
    }

    // Wraps the values to the width that the names and buttons leave.
    private void FitValues()
    {
        if (grid.IsDisposed) return;
        var widths = grid.GetColumnWidths();
        if (widths.Length < 3) return;
        var width = Math.Max(150, grid.Width - widths[0] - widths[2] - 12);
        foreach (var value in values) value.MaximumSize = new Size(width, 0);
    }

    // A warning across the grid, in the attention color.
    private void Warning(string text)
    {
        var label = Ui.Label(text);
        label.ForeColor = Ui.Attention;
        var row = grid.RowCount++;
        grid.Controls.Add(label, 0, row);
        grid.SetColumnSpan(label, 3);
    }

    /// A row of the grid: a name, its value, and a button that explains it.
    /// Each cell is centered in the row's height.
    private void Row(string label, string value, string? help = null)
    {
        var row = grid.RowCount++;
        var name = Ui.Label(label, true);
        name.Anchor = AnchorStyles.Left;
        name.Margin = new Padding(3, 9, 40, 9);
        grid.Controls.Add(name, 0, row);
        var detail = Ui.Label(value);
        detail.Anchor = AnchorStyles.Left;
        values.Add(detail);
        grid.Controls.Add(detail, 1, row);
        if (help != null)
        {
            var info = Ui.Button(InfoGlyph, (_, _) => Ui.Message(help));
            info.Font = Ui.Icon;
            info.ForeColor = Ui.Accent;
            info.MinimumSize = new Size(40, 34);
            info.Anchor = AnchorStyles.Right;
            info.AccessibleName = $"About {label}";
            info.AccessibleDescription = help;
            tips.SetToolTip(info, help);
            grid.Controls.Add(info, 2, row);
        }
    }

    private void Overview()
    {
        var warnings = status.Get("warnings");
        if (warnings.ValueKind == JsonValueKind.Array)
            foreach (var warning in warnings.Items()) Warning(warning.Text());
        Row("Node name", status.Text("name"));
        Row("Node ID", status.Text("node_id"));
        if (status.Text("cluster_id").Length > 0) Row("Network ID", status.Text("cluster_id"));
        Row("Members", status.Number("members").ToString());
    }

    private void Network()
    {
        Row("Permissions", PeersForm.PermissionNames(status.Get("permissions")),
            "What this computer may do, set by its invite. Admin: invite, remove, approve and change settings. Voter: be an acceptor, and take joins to the network. Relay: relay messages for other members. All: all three. Member: none of them. Every member can use the network and leave. Permissions never change.");
        Row("Overlay IPv4", status.Text("ipv4"));
        Row("Overlay IPv6", status.Text("ipv6"));
        Row("Conditions", $"{(status.Flag("public") ? "public" : "behind NAT")}, " +
            (status.Number("keepalive") == 0 ? "keepalive off" : $"keepalive {status.Number("keepalive")}s"));
        var leave = page.Add(Action("Leave Network…", LeaveAsync));
        leave.Margin = new Padding(3, 18, 3, 6);
    }

    private async Task LeaveAsync()
    {
        if (!Ui.Confirm("Leave the network?", "This computer leaves and removes its WireGuard interface. It needs a new invite to come back.", "Leave")) return;
        try { await Client.CallAsync("leave"); }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            if (!Ui.Confirm("Leaving failed", $"{ex.Message}\n\nLeave anyway?", "Leave Anyway")) { MessageLabel.Text = ex.Message; return; }
            await Client.CallAsync("leave", "--force");
        }
        if (IsDisposed) return;
        MessageLabel.Text = "Left the network.";
        await RefreshAsync();
    }

    private void Consensus()
    {
        var paused = status.Flag("changes_paused");
        Row("Status", paused ? "Changes paused" : "Available", "Available: enough acceptors can be reached to agree on changes. Changes paused: too few can be reached, so joins, removals and setting changes wait. Traffic between members keeps flowing.");
        Row("State version", status.Text("version"), "The version of the network's agreed state (members and settings) that this computer holds. It goes up with each change.");
        Row("Acceptors", status.Get("acceptors").GetArrayLength().ToString(), "The members that vote on changes and keep the agreed state. There are at most 7.");
        Row("Configuration", status.Text("config"), "The number of the current group of acceptors. It goes up each time the group changes.");
    }

    private void Security()
    {
        var protectedMode = status.Text("security") == "protected";
        Row("Status", protectedMode ? "Protected" : "Relaxed", "Protected: changes stay safe even if one acceptor is compromised and lies. It needs at least 4 acceptors. Relaxed: changes are safe only while every acceptor is honest. This is normal for small networks.");
        Row("Quorum", status.Text("quorum"), "The number of acceptor votes needed to agree on a change.");
        Row("Reachable", status.Text("reachable_voters"), "The acceptors this computer can reach now. When fewer than the quorum can be reached, changes pause.");
        Row("Strict mode", status.Get("strict_security").Text(), "When on, a protected network does not fall back to relaxed when acceptors go away. It pauses changes instead.");
        Row("Buffer", status.Text("buffer_nodes"), "How many acceptors may be unreachable while changes still go through. A larger buffer needs more acceptors before protection starts.");
    }

    private void Connectivity()
    {
        var wireguard = Ui.PortBadge(status, "wireguard", out var wgHint);
        Row("WireGuard", $"{status.Text("wg_interface")} • {status.Text("wg_backend")} • port {status.Text("wg_port")} • {wireguard}", wgHint);
        var control = Ui.PortBadge(status, "control", out var controlHint);
        Row("Control", $"{status.Text("control_addr")} • {control}", controlHint);
    }
}
