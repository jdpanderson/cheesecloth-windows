using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Windows.Forms;

namespace Cheesecloth.Tray;

internal sealed class PeersForm : PageForm
{
    // An icon in the members table. Its tooltip names it and says what it means.
    // `Dim` is for "Member", which has none of the others.
    internal sealed record Badge(string Glyph, string Name, string Meaning, bool Dim = false)
    {
        public string Tooltip => $"{Name}: {Meaning}";
    }

    // Glyphs of Segoe Fluent Icons (Windows 11), which keeps the code points of
    // Segoe MDL2 Assets (Windows 10).
    private const string AdminGlyph = "\uEA18";  // Shield
    private const string VoteGlyph = "\uE73E";   // CheckMark
    private const string RelayGlyph = "\uE8AB";  // Switch
    private const string MemberGlyph = "\uE77B"; // Contact
    private const string MoreGlyph = "\uE712";   // More
    private static readonly string IconFamily =
        FontFamily.Families.Any(family => family.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    // The light gray lines, the selected row and the hovered button of Windows 11 lists.
    private static readonly Color Line = Color.FromArgb(229, 229, 229);
    private static readonly Color SelectionBack = Color.FromArgb(229, 241, 251);
    private static readonly Color Hover = Color.FromArgb(234, 234, 234);

    // What each WireGuard path state means, as the GNOME app says it.
    private static readonly Dictionary<string, string> PathMeanings = new()
    {
        ["up"] = "WireGuard had a recent handshake with this member, so traffic flows.",
        ["not_configured"] = "WireGuard has no entry for this member yet.",
        ["no_handshake"] = "WireGuard has not had a handshake with this member yet.",
        ["waiting_to_punch"] = "A NAT hole punch is due, and waits for its turn.",
        ["offer_sent"] = "This computer offered a NAT hole punch, and waits for the answer.",
        ["punching"] = "Both sides are opening their NATs to reach each other.",
        ["waiting_for_removal"] = "The old path is being removed from WireGuard before the next try.",
        ["stale"] = "The last WireGuard handshake was more than 200 seconds ago. The path may be down.",
        ["no_path"] = "NAT hole punching failed, so there is no path to this member. It is tried again later.",
    };

    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        BorderStyle = BorderStyle.None, BackgroundColor = SystemColors.Window, GridColor = Line,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        EnableHeadersVisualStyles = false, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        AccessibleName = "Network members",
    };
    private readonly Label count = Ui.Label("");
    private readonly bool admin;
    private JsonElement peers;
    private Font? iconFont;
    private int gap;
    private int hoverRow = -1;
    private InviteForm? inviteForm;

    // `admin` is whether this node has the admin permission. Only an admin
    // may invite or remove, so this window otherwise offers neither.
    public PeersForm(DaemonClient client, bool admin) : base("Peers", client, 1180, 500)
    {
        this.admin = admin;
        // The count at the left, and the Invite button at the right.
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(0, 48), Padding = new Padding(8), ColumnCount = 2,
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        count.Anchor = AnchorStyles.Left;
        top.Controls.Add(count, 0, 0);
        if (admin)
        {
            var invite = Ui.Button("Invite…", (_, _) => ShowInvite());
            invite.Anchor = AnchorStyles.Right;
            top.Controls.Add(invite, 1, 0);
        }
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(0, 48), Padding = new Padding(8),
        };
        bottom.Controls.Add(MessageLabel);
        Controls.Add(grid);
        Controls.Add(bottom);
        Controls.Add(top);

        grid.DefaultCellStyle.BackColor = SystemColors.Window;
        grid.DefaultCellStyle.SelectionBackColor = SelectionBack;
        grid.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        grid.ColumnHeadersDefaultCellStyle.BackColor = SystemColors.Window;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = SystemColors.Window;
        // The same columns as the GNOME app. The name takes the width that the others leave.
        foreach (var heading in new[] { "Name", "Present", "IPv4", "Role", "Permissions", "Control", "Path", "State", "Endpoint", "Handshake", "Actions" })
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = heading, HeaderText = heading == "Actions" ? "" : heading,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
            });
        grid.Columns["Name"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        foreach (var drawn in new[] { "Role", "Permissions", "State", "Actions" })
            grid.Columns[drawn]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        grid.Columns["Actions"]!.SortMode = DataGridViewColumnSortMode.NotSortable;
        UpdateMetrics();
        grid.FontChanged += (_, _) => UpdateMetrics();
        grid.CellPainting += PaintCell;
        grid.CellMouseEnter += (_, e) => SetHover(e.ColumnIndex == grid.Columns["Actions"]!.Index ? e.RowIndex : -1);
        grid.CellMouseLeave += (_, _) => SetHover(-1);
        grid.CellMouseClick += OnCellClick;
        FormClosed += (_, _) => iconFont?.Dispose();
    }

    // Shows the one Invite window, opening it if it is not open.
    private void ShowInvite()
    {
        if (inviteForm != null)
        {
            Ui.Raise(inviteForm);
            return;
        }
        inviteForm = new InviteForm(Client);
        inviteForm.FormClosed += (_, _) => inviteForm = null;
        inviteForm.Show(this);
    }

    protected override async Task RefreshAsync()
    {
        peers = await Client.CallAsync("peers");
        if (IsDisposed) return;
        var list = peers.Items().ToArray();
        var rows = grid.Rows.Cast<DataGridViewRow>().ToDictionary(row => (string)row.Tag!);
        // While the members stay the same, the rows are updated in place, so the
        // sort order, the selection and an open tooltip stay.
        if (!rows.Keys.ToHashSet().SetEquals(list.Select(peer => peer.Text("node_id"))))
        {
            var oldId = grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as string : null;
            grid.Rows.Clear();
            rows.Clear();
            foreach (var peer in list)
            {
                var row = grid.Rows[grid.Rows.Add()];
                row.Tag = peer.Text("node_id");
                rows[peer.Text("node_id")] = row;
            }
            grid.ClearSelection();
            if (oldId != null && rows.TryGetValue(oldId, out var selected)) selected.Selected = true;
        }
        foreach (var peer in list) ShowPeer(rows[peer.Text("node_id")], peer);
        if (grid.SortedColumn != null)
            grid.Sort(grid.SortedColumn, grid.SortOrder == SortOrder.Descending
                ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending);
        count.Text = list.Length == 1 ? "No other members yet." : $"{list.Length} members";
    }

    // Each cell holds text, which screen readers read and the columns sort by.
    // The Role, Permissions and State cells also hold what PaintCell draws.
    private static void ShowPeer(DataGridViewRow row, JsonElement peer)
    {
        var thisNode = peer.Flag("this_node");
        var present = peer.Flag("present");
        var connected = peer.Flag("connected");
        var age = peer.Get("handshake_age_secs");
        var roles = RoleBadges(peer);
        var permissions = PermissionBadges(peer.Get("permissions"));
        var state = peer.Text("path_state");
        var (control, controlTip) = thisNode ? ("—", "") : connected ? ("Yes", "Connected directly.") :
            present ? ("Relay", "Reached through other members.") : ("No", "Not reached now.");
        Set(row.Cells["Name"], peer.Text("name") + (thisNode ? " (this computer)" : ""));
        Set(row.Cells["Present"], present ? "Yes" : "No", color: present ? Ui.Good : Ui.Off);
        Set(row.Cells["IPv4"], Or(peer.Text("ipv4")));
        Set(row.Cells["Role"], string.Join(", ", roles.Select(badge => badge.Name)), Tooltips(roles), tag: roles);
        Set(row.Cells["Permissions"], PermissionNames(peer.Get("permissions")), Tooltips(permissions), tag: permissions);
        Set(row.Cells["Control"], control, controlTip, connected ? Ui.Good : Ui.Off);
        Set(row.Cells["Path"], peer.Text("path"));
        var pathTip = PathTooltip(state, peer.Text("path_detail"), thisNode);
        Set(row.Cells["State"], pathTip.Split('\n')[0], pathTip, tag: PathColor(state, thisNode));
        Set(row.Cells["Endpoint"], Or(peer.Text("wg_endpoint")));
        Set(row.Cells["Handshake"], age.ValueKind == JsonValueKind.Number ? Json.Age(age.GetInt32()) : "—");
        Set(row.Cells["Actions"], "Actions", $"Actions for {peer.Text("name")}");
    }

    // An empty `tooltip` lets the grid show the text when the cell cuts it off.
    private static void Set(DataGridViewCell cell, string text, string tooltip = "", Color? color = null, object? tag = null)
    {
        cell.Value = text;
        cell.ToolTipText = tooltip;
        cell.Tag = tag;
        cell.Style.ForeColor = cell.Style.SelectionForeColor = color ?? Color.Empty;
    }

    private static string Or(string text) => text.Length > 0 ? text : "—";

    private static string Tooltips(Badge[] badges) => string.Join("\n", badges.Select(badge => badge.Tooltip));

    // What the member does now. Its permissions say what it may do.
    internal static Badge[] RoleBadges(JsonElement peer)
    {
        var badges = new List<Badge>();
        if (peer.Flag("acceptor")) badges.Add(new(VoteGlyph, "Acceptor", "votes on changes now"));
        if (peer.Flag("relay")) badges.Add(new(RelayGlyph, "Relay", "relays messages for other members now"));
        if (badges.Count == 0) badges.Add(new(MemberGlyph, "Member", "no other role now", true));
        return [.. badges];
    }

    internal static Badge[] PermissionBadges(JsonElement permissions)
    {
        var badges = new List<Badge>();
        if (permissions.Flag("admin")) badges.Add(new(AdminGlyph, "Admin", "may invite, remove, approve and change settings"));
        if (permissions.Flag("vote")) badges.Add(new(VoteGlyph, "Voter", "may be an acceptor, and take joins to the network"));
        if (permissions.Flag("relay")) badges.Add(new(RelayGlyph, "Relay", "may relay messages for other members"));
        if (badges.Count == 0) badges.Add(new(MemberGlyph, "Member", "may use the network and leave", true));
        return [.. badges];
    }

    // A member's permissions for people: "All", a list such as "Voter, Relay",
    // or "Member" with none.
    internal static string PermissionNames(JsonElement permissions)
    {
        var names = new[] { ("admin", "Admin"), ("vote", "Voter"), ("relay", "Relay") }
            .Where(p => permissions.Flag(p.Item1)).Select(p => p.Item2).ToArray();
        return names.Length == 3 ? "All" : names.Length == 0 ? "Member" : string.Join(", ", names);
    }

    internal static Color PathColor(string state, bool thisNode) => thisNode ? Ui.Off : state switch
    {
        "up" => Ui.Good,
        "no_path" => Ui.Broken,
        "stale" or "not_configured" or "no_handshake" or "waiting_to_punch" or "offer_sent" or "punching" or "waiting_for_removal" => Ui.Attention,
        _ => Ui.Off,
    };

    // The daemon's detail on the first line, then what the state means.
    internal static string PathTooltip(string state, string detail, bool thisNode)
    {
        if (thisNode) return "This computer";
        if (detail.Length > 0) detail = char.ToUpperInvariant(detail[0]) + detail[1..];
        // A state from a newer daemon has no meaning here, but its detail still names it.
        return PathMeanings.TryGetValue(state, out var meaning) ? $"{detail}\n{meaning}" : detail;
    }

    // Sizes the cell padding, rows, icons and drawn columns to the grid font,
    // so the table scales with the display.
    private void UpdateMetrics()
    {
        iconFont?.Dispose();
        var icon = iconFont = new Font(IconFamily, grid.Font.SizeInPoints * 1.15f);
        gap = grid.Font.Height / 3;
        grid.DefaultCellStyle.Padding = new Padding(gap, 0, gap, 0);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(gap, gap, gap, gap);
        grid.RowTemplate.Height = grid.Font.Height * 2;
        foreach (DataGridViewRow row in grid.Rows) row.Height = grid.RowTemplate.Height;
        // The header text with room for the sort arrow.
        int Header(string name) => TextRenderer.MeasureText(grid.Columns[name]!.HeaderText, grid.Font).Width + 2 * gap + grid.Font.Height;
        int Icons(int n) => n * icon.Height + (n - 1) * gap + 2 * gap + 1;
        grid.Columns["Role"]!.Width = Math.Max(Header("Role"), Icons(2));
        grid.Columns["Permissions"]!.Width = Math.Max(Header("Permissions"), Icons(3));
        grid.Columns["State"]!.Width = Header("State");
        grid.Columns["Actions"]!.Width = Icons(1) + 2 * gap;
        grid.Columns["Name"]!.MinimumWidth = grid.Font.Height * 8;
    }

    private void PaintCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.ColumnIndex < 0 || iconFont == null) return;
        var graphics = e.Graphics!;
        var bounds = e.CellBounds;
        if (e.RowIndex < 0)
        {
            // A header with one light line below it.
            e.Paint(e.ClipBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.Border);
            using var pen = new Pen(Line);
            graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
            e.Handled = true;
            return;
        }
        var column = grid.Columns[e.ColumnIndex].Name;
        if (column is not ("Role" or "Permissions" or "State" or "Actions")) return;
        e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var tag = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Tag;
        var foreground = e.CellStyle!.ForeColor;
        var box = new Rectangle(bounds.Left + gap, bounds.Top, iconFont.Height, bounds.Height);
        if (column == "State" && tag is Color color)
        {
            var size = grid.Font.Height / 2;
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, bounds.Left + (bounds.Width - size) / 2, bounds.Top + (bounds.Height - size) / 2, size, size);
        }
        else if (column == "Actions")
        {
            box.X = bounds.Left + (bounds.Width - box.Width) / 2;
            if (e.RowIndex == hoverRow)
            {
                using var brush = new SolidBrush(Hover);
                var back = new Rectangle(box.X - gap, bounds.Top + (bounds.Height - box.Width) / 2 - gap / 2, box.Width + 2 * gap, box.Width + gap);
                graphics.FillRoundedRectangle(brush, back, new Size(gap, gap));
            }
            DrawGlyph(graphics, MoreGlyph, box, foreground);
        }
        else if (tag is Badge[] badges)
        {
            foreach (var badge in badges)
            {
                DrawGlyph(graphics, badge.Glyph, box, badge.Dim ? SystemColors.GrayText : foreground);
                box.X += box.Width + gap;
            }
        }
        e.Handled = true;
    }

    private void DrawGlyph(Graphics graphics, string glyph, Rectangle box, Color color) =>
        TextRenderer.DrawText(graphics, glyph, iconFont, box, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

    private void SetHover(int row)
    {
        if (row == hoverRow) return;
        var actions = grid.Columns["Actions"]!.Index;
        if (hoverRow >= 0 && hoverRow < grid.RowCount) grid.InvalidateCell(actions, hoverRow);
        hoverRow = row;
        if (row >= 0) grid.InvalidateCell(actions, row);
    }

    private void OnCellClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 || (e.Button != MouseButtons.Right && e.ColumnIndex != grid.Columns["Actions"]!.Index)) return;
        var id = grid.Rows[e.RowIndex].Tag as string;
        var peer = peers.Items().FirstOrDefault(p => p.Text("node_id") == id);
        if (peer.ValueKind != JsonValueKind.Object) return;
        var context = new ContextMenuStrip();
        context.Items.Add("Copy IP", null, (_, _) => Clipboard.SetText(peer.Text("ipv4")));
        context.Items.Add("Copy Name", null, (_, _) => Clipboard.SetText(peer.Text("name")));
        if (admin && !peer.Flag("this_node"))
        {
            context.Items.Add("Remove…", null, async (_, _) =>
            {
                if (!Ui.Confirm($"Remove {peer.Text("name")} from the network?", $"Node ID: {peer.Text("node_id")}", "Remove")) return;
                await RunAsync(async () =>
                {
                    var result = await Client.CallAsync("remove", peer.Text("node_id"));
                    if (IsDisposed) return;
                    MessageLabel.Text = result.Text("outcome") == "pending" ? "Removal waits for approvals." : "Member removed.";
                    await RefreshAsync();
                });
            });
        }
        context.Closed += (_, _) => context.Dispose();
        context.Show(Cursor.Position);
    }
}

internal sealed class InviteForm : PageForm
{
    private readonly TextBox token = new()
    {
        Multiline = true, ReadOnly = true, Width = 430, Height = 90,
        ScrollBars = ScrollBars.Vertical,
    };
    private readonly Label expires = Ui.Label("");
    private readonly System.Windows.Forms.Timer countdown = new() { Interval = 1000 };
    private DateTimeOffset expiry;
    private readonly Button create;
    // All on by default, as `cheesecloth invite` does.
    private readonly CheckBox admin = Permission("Admin: invite, remove, approve and change settings");
    private readonly CheckBox vote = Permission("Voter: be an acceptor, and take joins to the network");
    private readonly CheckBox relay = Permission("Relay: relay messages for other members");

    public InviteForm(DaemonClient client) : base("Invite", client, 500, 470)
    {
        var flow = CreateForm.NewFlow();
        flow.Controls.Add(Ui.Label("An invite lets one new node join. It works once and expires after 30 minutes."));
        flow.Controls.Add(Ui.Label("Permissions", true));
        flow.Controls.Add(admin);
        flow.Controls.Add(vote);
        flow.Controls.Add(relay);
        var hint = Ui.Label("Every member can use the network and leave. Permissions never change: to change them, the member must leave and join again.");
        hint.ForeColor = Ui.Off;
        flow.Controls.Add(hint);
        create = Action("Create Invite", CreateInviteAsync);
        flow.Controls.Add(create);
        flow.Controls.Add(token);
        var copy = Ui.Button("Copy", (_, _) =>
        {
            Clipboard.SetText(token.Text);
            MessageLabel.Text = "Copied.";
        });
        copy.Enabled = false;
        token.TextChanged += (_, _) => copy.Enabled = token.Text.Length > 0;
        flow.Controls.Add(copy);
        flow.Controls.Add(expires);
        flow.Controls.Add(MessageLabel);
        Controls.Add(flow);
        countdown.Tick += (_, _) => UpdateCountdown();
        Shown += (_, _) => countdown.Start();
        FormClosed += (_, _) => countdown.Dispose();
    }

    private static CheckBox Permission(string text) => new() { Text = text, Checked = true, AutoSize = true };

    // The CLI arguments: `invite` gives every permission, and each cleared
    // box takes one away.
    internal static string[] InviteArguments(bool admin, bool vote, bool relay)
    {
        var arguments = new List<string> { "invite" };
        if (!admin) arguments.Add("--no-admin");
        if (!vote) arguments.Add("--no-vote");
        if (!relay) arguments.Add("--no-relay");
        return arguments.ToArray();
    }

    private async Task CreateInviteAsync()
    {
        var result = await Client.CallAsync(InviteArguments(admin.Checked, vote.Checked, relay.Checked));
        if (IsDisposed) return;
        token.Text = result.Text("token");
        expiry = DateTimeOffset.FromUnixTimeMilliseconds(result.Get("expires_at_ms").GetInt64());
        create.Text = "Create Another";
        UpdateCountdown();
        MessageLabel.Text = "Invite created.";
    }

    private void UpdateCountdown()
    {
        if (expiry == default) return;
        var left = expiry - DateTimeOffset.UtcNow;
        expires.Text = left <= TimeSpan.Zero ? "Expired" : $"Expires in {(int)left.TotalMinutes:00}:{left.Seconds:00}";
    }
}

internal sealed class ApprovalsForm : PageForm
{
    private readonly FlowLayoutPanel list = CreateForm.NewFlow();

    public ApprovalsForm(DaemonClient client) : base("Approvals", client, 600, 500)
    {
        Controls.Add(list);
    }

    protected override async Task RefreshAsync()
    {
        var pending = await Client.CallAsync("pending");
        if (IsDisposed) return;
        list.SuspendLayout();
        ClearDynamicActions();
        foreach (var control in list.Controls.Cast<Control>().Where(c => c != MessageLabel).ToArray()) control.Dispose();
        list.Controls.Clear();
        if (pending.GetArrayLength() == 0) list.Controls.Add(Ui.Label("Nothing waits for approval."));
        foreach (var proposal in pending.Items())
        {
            var kind = proposal.Text("kind");
            var card = new FlowLayoutPanel
            {
                Width = 540, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(540, kind == "join" ? 190 : 155), FlowDirection = FlowDirection.TopDown,
                WrapContents = false, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(10),
            };
            var title = kind switch
            {
                "join" => $"Join of {proposal.Text("name")}",
                "remove" => $"Removal of {proposal.Text("name")}",
                _ => $"Setting {proposal.Get("setting").Text("key")} = {proposal.Get("setting").Text("value")}",
            };
            card.Controls.Add(Ui.Label(title, true));
            if (kind == "join")
            {
                card.Controls.Add(Ui.Label($"Node ID: {proposal.Text("node")}\nCheck this ID against the one the joining node shows."));
                card.Controls.Add(Ui.Label($"Permissions: {PeersForm.PermissionNames(proposal.Get("permissions"))}"));
            }
            var approvals = proposal.Get("approvals").GetArrayLength();
            card.Controls.Add(Ui.Label($"Asked by {Json.ShortId(proposal.Text("proposer"))} • {approvals} of {proposal.Number("needed")} approvals • {proposal.Number("expires_in_secs") / 60} min left"));
            var actions = new FlowLayoutPanel
            {
                Width = 500, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(500, 38),
            };
            var id = proposal.Text("id");
            actions.Controls.Add(Action("Reject", () => VoteAsync("reject", id)));
            actions.Controls.Add(Action("Approve", () => VoteAsync("approve", id)));
            card.Controls.Add(actions);
            list.Controls.Add(card);
        }
        list.Controls.Add(MessageLabel);
        list.ResumeLayout();
    }

    private async Task VoteAsync(string vote, string id)
    {
        var result = await Client.CallAsync(vote, id);
        if (IsDisposed) return;
        MessageLabel.Text = result.Text("outcome") switch
        {
            "approved" => $"Approved. {result.Number("remaining")} more approval(s) needed.",
            "joined" => "Node joined.",
            "removed" => "Member removed.",
            "rejected" => "Proposal rejected.",
            _ => "Done.",
        };
        await RefreshAsync();
    }
}
