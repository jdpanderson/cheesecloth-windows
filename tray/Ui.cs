using System.Diagnostics;
using System.Drawing;
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

    public static Label Label(string text, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        Font = bold ? new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) : SystemFonts.MessageBoxFont,
        Margin = new Padding(3, 6, 3, 6),
    };

    public static Button Button(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(100, 30) };
        button.Click += click;
        return button;
    }

    internal static (Panel Area, Label Text) MessageArea(string text, string name)
    {
        var area = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var message = Label(text);
        message.AccessibleName = name;
        area.Controls.Add(message);
        area.SizeChanged += (_, _) => message.MaximumSize = new Size(Math.Max(200, area.ClientSize.Width - 12), 0);
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
            Text = title, ClientSize = new Size(480, 180), MinimumSize = new Size(480, 180),
            StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, Font = SystemFonts.MessageBoxFont,
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var (messageArea, _) = MessageArea(body, "Confirmation details");
        layout.Controls.Add(messageArea, 0, 0);
        var actions = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Dock = DockStyle.Fill,
        };
        var cancel = new Button { Text = cancelText, DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(100, 32) };
        var confirm = new Button { Text = action, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(110, 32) };
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

internal abstract class PageForm : Form
{
    protected readonly DaemonClient Client;
    protected readonly Label MessageLabel = Ui.Label("");
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
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterScreen;
        MessageLabel.AccessibleName = "Result";
        poll.Tick += async (_, _) => await RefreshSafeAsync();
        Shown += async (_, _) => { poll.Start(); await RefreshSafeAsync(); };
        FormClosed += (_, _) => poll.Dispose();
    }

    protected Button Action(string text, Func<Task> action)
    {
        var button = Ui.Button(text, async (_, _) => await RunAsync(action));
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
        ClientSize = new Size(450, 150);
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var (messageArea, message) = Ui.MessageArea("Working…", "Result");
        var progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Fill, AccessibleName = "Progress" };
        var close = Ui.Button("Close", (_, _) => Close());
        close.Anchor = AnchorStyles.Right;
        close.Enabled = false;
        layout.Controls.Add(messageArea, 0, 0);
        layout.Controls.Add(progress, 0, 1);
        layout.Controls.Add(close, 0, 2);
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
    private readonly TextBox range = new() { Width = 300 };
    private readonly CheckBox strict = new() { Text = "Strict security", AutoSize = true };

    public CreateForm(DaemonClient client) : base("Create Network", client, 490, 300)
    {
        var flow = NewFlow();
        flow.Controls.Add(Ui.Label("This computer becomes the network's first member."));
        flow.Controls.Add(Ui.Label("IPv4 range (empty: a random /24 in 100.64.0.0/10)"));
        flow.Controls.Add(range);
        flow.Controls.Add(strict);
        var hint = Ui.Label("Keep Byzantine protection once there are enough members, and pause changes instead of giving it up.");
        hint.ForeColor = Ui.Off;
        flow.Controls.Add(hint);
        flow.Controls.Add(Action("Create", async () =>
        {
            var args = new List<string> { "init" };
            if (strict.Checked) args.Add("--strict-security");
            if (range.Text.Trim().Length > 0) { args.Add("--ipv4-range"); args.Add(range.Text.Trim()); }
            var result = await Client.CallAsync([.. args]);
            if (IsDisposed) return;
            MessageLabel.Text = $"Created network {Json.ShortId(result.Text("cluster_id"))}. IPv4 range: {result.Text("ipv4_range")}";
        }));
        flow.Controls.Add(MessageLabel);
        Controls.Add(flow);
    }

    internal static FlowLayoutPanel NewFlow() => new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, Padding = new Padding(16),
    };
}

internal sealed class JoinForm : PageForm
{
    private readonly TextBox token = new() { Multiline = true, Width = 420, Height = 100, ScrollBars = ScrollBars.Vertical };

    public JoinForm(DaemonClient client) : base("Join Network", client, 490, 330)
    {
        var flow = CreateForm.NewFlow();
        flow.Controls.Add(Ui.Label("Paste the token from Peers > Invite on a member."));
        flow.Controls.Add(token);
        var join = Action("Join", async () =>
        {
            var result = await Client.CallAsync("join", token.Text.Trim());
            if (IsDisposed) return;
            MessageLabel.Text = result.Flag("joined") ? $"Joined. This node's address is {result.Text("ipv4")}." :
                $"Waiting for approval. Ask members to check this node ID: {result.Text("node_id")}";
        });
        join.Enabled = false;
        token.TextChanged += (_, _) => join.Enabled = token.Text.Trim().Length > 0;
        flow.Controls.Add(join);
        flow.Controls.Add(MessageLabel);
        Controls.Add(flow);
    }
}

internal sealed class StatusForm : PageForm
{
    private readonly ListBox sections = new()
    {
        Dock = DockStyle.Left, Width = 190, BorderStyle = BorderStyle.None,
        DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 48, AccessibleName = "Status sections",
    };
    private readonly FlowLayoutPanel content = CreateForm.NewFlow();
    private JsonElement status;
    private readonly ToolTip tips = new();

    private static readonly string[] MemberSections = ["Overview", "Network", "Consensus", "Security", "Connectivity"];

    private sealed record SectionChoice(string Name, string Health, Color Color)
    {
        public override string ToString() => $"{Name}, {Health}";
    }

    public StatusForm(DaemonClient client) : base("Status", client, 780, 510)
    {
        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(content);
        body.Controls.Add(sections);
        content.Dock = DockStyle.Fill;
        sections.DrawItem += DrawSection;
        sections.SelectedIndexChanged += (_, _) => Render();
        sections.FontChanged += (_, _) => sections.ItemHeight = Math.Max(48, sections.Font.Height * 2 + 12);
        FormClosed += (_, _) => tips.Dispose();
        Controls.Add(body);
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
        var warnings = status.Get("warnings");
        var hasWarnings = warnings.ValueKind == JsonValueKind.Array && warnings.GetArrayLength() > 0;
        return name switch
        {
            "Overview" when hasWarnings => new(name, "Warnings", Ui.Attention),
            "Overview" when status.Text("phase") == "pending" => new(name, "Waiting for approval", Ui.Attention),
            "Overview" when status.Text("phase") == "stopping" => new(name, "Leaving", Ui.Attention),
            "Overview" when status.Text("phase") == "member" => new(name, "Healthy", Ui.Good),
            "Network" when status.Flag("public") => new(name, "Public", Ui.Good),
            "Network" => new(name, "Behind NAT", Ui.Off),
            "Consensus" when status.Flag("changes_paused") => new(name, "Changes paused", Ui.Attention),
            "Consensus" => new(name, "Available", Ui.Good),
            "Security" when status.Text("security") == "protected" => new(name, "Protected", Ui.Good),
            "Security" => new(name, "Relaxed", Ui.Info),
            "Connectivity" when PortColor() == Ui.Attention => new(name, "Needs attention", Ui.Attention),
            "Connectivity" when PortColor() == Ui.Good => new(name, "Mapped", Ui.Good),
            _ => new(name, "Off", Ui.Off),
        };
    }

    private void DrawSection(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        e.DrawBackground();
        var section = (SectionChoice)sections.Items[e.Index];
        using var brush = new SolidBrush(section.Color);
        e.Graphics.FillEllipse(brush, e.Bounds.Left + 11, e.Bounds.Top + (e.Bounds.Height - 10) / 2, 10, 10);
        var lineHeight = e.Font!.Height;
        var top = e.Bounds.Top + Math.Max(2, (e.Bounds.Height - lineHeight * 2) / 2);
        var textBounds = new Rectangle(e.Bounds.Left + 30, top, e.Bounds.Width - 34, lineHeight);
        TextRenderer.DrawText(e.Graphics, section.Name, e.Font!, textBounds, e.ForeColor, TextFormatFlags.EndEllipsis);
        textBounds.Y += lineHeight;
        TextRenderer.DrawText(e.Graphics, section.Health, e.Font!, textBounds, e.ForeColor, TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
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

    private void Render()
    {
        if (status.ValueKind != JsonValueKind.Object || sections.SelectedItem == null) return;
        content.SuspendLayout();
        ClearDynamicActions();
        foreach (var control in content.Controls.Cast<Control>().Where(c => c != MessageLabel).ToArray()) control.Dispose();
        content.Controls.Clear();
        switch (((SectionChoice)sections.SelectedItem).Name)
        {
            case "Overview": Overview(); break;
            case "Network": Network(); break;
            case "Consensus": Consensus(); break;
            case "Security": Security(); break;
            case "Connectivity": Connectivity(); break;
        }
        content.Controls.Add(MessageLabel);
        content.ResumeLayout();
    }

    private void Heading(string text, Color color)
    {
        var label = Ui.Label(text, true);
        label.ForeColor = color;
        content.Controls.Add(label);
    }

    private void Row(string label, string value, string? help = null)
    {
        var row = new TableLayoutPanel
        {
            Width = Math.Max(400, content.ClientSize.Width - 36), AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly, ColumnCount = help == null ? 2 : 3,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75));
        if (help != null) row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        var name = Ui.Label(label, true);
        name.Dock = DockStyle.Fill;
        row.Controls.Add(name, 0, 0);
        var detail = Ui.Label(value);
        detail.MaximumSize = new Size(Math.Max(200, (int)(row.Width * 0.68f)), 0);
        row.Controls.Add(detail, 1, 0);
        if (help != null)
        {
            var info = Ui.Button("ⓘ", (_, _) => Ui.Message(help));
            info.Size = new Size(30, 26);
            info.MinimumSize = Size.Empty;
            info.AccessibleName = $"About {label}";
            info.AccessibleDescription = help;
            tips.SetToolTip(info, help);
            row.Controls.Add(info, 2, 0);
        }
        content.Controls.Add(row);
    }

    private void Overview()
    {
        var warnings = status.Get("warnings");
        if (warnings.ValueKind == JsonValueKind.Array)
            foreach (var warning in warnings.Items()) Heading(warning.Text(), Ui.Attention);
        var count = warnings.ValueKind == JsonValueKind.Array ? warnings.GetArrayLength() : 0;
        Heading(status.Text("phase") == "stopping" ? "Leaving" : count == 0 ? "Healthy" : $"{count} warning(s)",
            count == 0 ? Ui.Good : Ui.Attention);
        Row("Node name", status.Text("name"));
        Row("Node ID", status.Text("node_id"));
        if (status.Text("phase") == "pending") Heading("Waiting for approval", Ui.Attention);
        if (status.Text("cluster_id").Length > 0) Row("Network ID", status.Text("cluster_id"));
        Row("Members", status.Number("members").ToString());
    }

    private void Network()
    {
        Heading(status.Flag("public") ? "Public" : "Behind NAT", status.Flag("public") ? Ui.Good : Ui.Off);
        if (status.Flag("relay")) Heading("Relay", Ui.Off);
        Row("Overlay IPv4", status.Text("ipv4"));
        Row("Overlay IPv6", status.Text("ipv6"));
        Row("Conditions", $"{(status.Flag("public") ? "public" : "behind NAT")}, " +
            (status.Number("keepalive") == 0 ? "keepalive off" : $"keepalive {status.Number("keepalive")}s"));
        content.Controls.Add(Action("Leave Network…", LeaveAsync));
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
        Heading(paused ? "Changes paused" : "Available", paused ? Ui.Attention : Ui.Good);
        Row("Status", paused ? "Changes paused" : "Available", "Available: enough acceptors can be reached to agree on changes. Changes paused: too few can be reached, so joins, removals and setting changes wait. Traffic between members keeps flowing.");
        Row("State version", status.Text("version"), "The version of the network's agreed state (members and settings) that this computer holds. It goes up with each change.");
        Row("Acceptors", status.Get("acceptors").GetArrayLength().ToString(), "The members that vote on changes and keep the agreed state. There are at most 7.");
        Row("Configuration", status.Text("config"), "The number of the current group of acceptors. It goes up each time the group changes.");
    }

    private void Security()
    {
        var protectedMode = status.Text("security") == "protected";
        Heading(protectedMode ? "Protected" : "Relaxed", protectedMode ? Ui.Good : Ui.Info);
        Row("Status", protectedMode ? "Protected" : "Relaxed", "Protected: changes stay safe even if one acceptor is compromised and lies. It needs at least 4 acceptors. Relaxed: changes are safe only while every acceptor is honest. This is normal for small networks.");
        Row("Quorum", status.Text("quorum"), "The number of acceptor votes needed to agree on a change.");
        Row("Reachable", status.Text("reachable_voters"), "The acceptors this computer can reach now. When fewer than the quorum can be reached, changes pause.");
        Row("Strict mode", status.Get("strict_security").Text(), "When on, a protected network does not fall back to relaxed when acceptors go away. It pauses changes instead.");
        Row("Buffer", status.Text("buffer_nodes"), "How many acceptors may be unreachable while changes still go through. A larger buffer needs more acceptors before protection starts.");
    }

    private void Connectivity()
    {
        Heading("Ports", Ui.Info);
        var wireguard = Ui.PortBadge(status, "wireguard", out var wgHint);
        Heading($"WireGuard mapping: {wireguard}", wireguard == "Mapped" ? Ui.Good : wireguard == "Not mapped" ? Ui.Attention : Ui.Off);
        Row("WireGuard", $"{status.Text("wg_interface")} • {status.Text("wg_backend")} • port {status.Text("wg_port")} • {wireguard}", wgHint);
        var control = Ui.PortBadge(status, "control", out var controlHint);
        Heading($"Control mapping: {control}", control == "Mapped" ? Ui.Good : control == "Not mapped" ? Ui.Attention : Ui.Off);
        Row("Control", $"{status.Text("control_addr")} • {control}", controlHint);
    }
}
