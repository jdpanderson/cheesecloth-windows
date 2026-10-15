using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace Cheesecloth.Tray;

internal sealed class PeersForm : PageForm
{
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        AccessibleName = "Network members",
    };
    private readonly Label count = Ui.Label("");
    private readonly bool admin;
    private JsonElement peers;

    // `admin` is whether this node has the admin permission. Only an admin
    // may invite or remove, so this window otherwise offers neither.
    public PeersForm(DaemonClient client, bool admin) : base("Peers", client, 1180, 500)
    {
        this.admin = admin;
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(0, 48), Padding = new Padding(8),
        };
        top.Controls.Add(count);
        if (admin)
            top.Controls.Add(Ui.Button("Invite…", (_, _) => new InviteForm(client).Show(this)));
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(0, 68), Padding = new Padding(8),
        };
        bottom.Controls.Add(Ui.Label("* = acceptor  •  Control relay = reached through other members"));
        bottom.Controls.Add(MessageLabel);
        Controls.Add(grid);
        Controls.Add(bottom);
        Controls.Add(top);
        foreach (var heading in new[] { "Name", "IPv4", "Role", "Permissions", "Control", "Path", "State", "Endpoint", "Handshake" })
            grid.Columns.Add(heading, heading);
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "Actions", HeaderText = "Actions", Text = "⋯", UseColumnTextForButtonValue = true });
        grid.CellMouseClick += OnCellClick;
    }

    protected override async Task RefreshAsync()
    {
        peers = await Client.CallAsync("peers");
        if (IsDisposed) return;
        var oldId = grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as string : null;
        grid.Rows.Clear();
        var members = 0;
        foreach (var peer in peers.Items())
        {
            members++;
            var role = (peer.Flag("relay") ? "relay" : "member") + (peer.Flag("acceptor") ? "*" : "");
            var control = peer.Flag("this_node") ? "—" : peer.Flag("connected") ? "yes" : peer.Flag("present") ? "relay" : "no";
            var age = peer.Get("handshake_age_secs");
            var row = grid.Rows.Add(peer.Text("name") + (peer.Flag("this_node") ? " (this computer)" : ""),
                peer.Text("ipv4"), role, PermissionNames(peer.Get("permissions")), control, peer.Text("path"), peer.Text("path_detail"),
                peer.Text("wg_endpoint"), age.ValueKind == JsonValueKind.Number ? Json.Age(age.GetInt32()) : "—", "⋯");
            grid.Rows[row].Tag = peer.Text("node_id");
            var state = peer.Text("path_state");
            grid.Rows[row].Cells[6].Style.ForeColor = PathColor(state, peer.Flag("this_node"));
            if (grid.Rows[row].Tag as string == oldId) grid.Rows[row].Selected = true;
        }
        count.Text = members == 1 ? "No other members yet." : $"{members} members";
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
        "stale" or "no_handshake" or "waiting_to_punch" or "offer_sent" or "punching" or "waiting_for_removal" => Ui.Attention,
        _ => Ui.Off,
    };

    private void OnCellClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 || (e.Button != MouseButtons.Right && e.ColumnIndex != grid.Columns.Count - 1)) return;
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
