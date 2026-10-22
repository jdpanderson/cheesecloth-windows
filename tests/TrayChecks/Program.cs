using System.Drawing;
using System.Windows.Forms;
using Cheesecloth.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        CheckCliErrors();
        CheckPeerPaths();
        var client = new DaemonClient();
        CheckPermissions(client);
        var pages = new (string Name, Func<Form> Create)[]
        {
            ("confirmation", () => Ui.CreateConfirmation("Leave the network?",
                "This computer leaves and removes its WireGuard interface. It needs a new invite to come back.",
                "Leave")),
            ("operation", () => new OperationForm("Retry Leave", () => Task.FromResult("Left the network."))),
            ("create", () => new CreateForm(client)),
            ("join", () => new JoinForm(client)),
            ("status", () => new StatusForm(client)),
            ("peers", () => new PeersForm(client, true)),
            ("peers without admin", () => new PeersForm(client, false)),
            ("invite", () => new InviteForm(client)),
            ("approvals", () => new ApprovalsForm(client)),
        };

        foreach (var (name, create) in pages)
        {
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                using var page = create();
                SetFontScale(page, scale);
                Layout(page);
                Check(page, name, scale);
            }
        }
        Console.WriteLine("Tray accessibility and scaling checks passed.");
    }

    private static void CheckCliErrors()
    {
        var refused = DaemonException.FromCliError("""{"error":{"code":"permission_denied","message":"API version"}}""");
        if (refused.Code != "permission_denied" || refused.Message != "API version" ||
            TrayContext.ErrorTitle(refused, TimeSpan.FromMinutes(1)) != "Administrators only")
            throw new Exception("The tray did not use the permission_denied code.");

        var version = DaemonException.FromCliError("""{"error":{"code":"api_version_mismatch","message":"access denied"}}""");
        if (TrayContext.ErrorTitle(version, TimeSpan.FromMinutes(1)) != "Needs reinstall")
            throw new Exception("The tray did not use the api_version_mismatch code.");

        var unknown = DaemonException.FromCliError("""{"error":{"code":"new_code","message":"only administrators"}}""");
        if (unknown.Code != "new_code" || unknown.Message != "only administrators" ||
            TrayContext.ErrorTitle(unknown, TimeSpan.FromMinutes(1)) != "Not responding")
            throw new Exception("The tray did not preserve an unknown code.");

        try
        {
            DaemonException.FromCliError("""{"error":{"message":"missing code"}}""");
            throw new Exception("The tray accepted an error without a code.");
        }
        catch (InvalidDataException) { }
    }

    private static void CheckPeerPaths()
    {
        if (PeersForm.PathColor("up", false) != Ui.Good ||
            PeersForm.PathColor("no_path", false) != Ui.Broken ||
            PeersForm.PathColor("no_handshake", false) != Ui.Attention ||
            PeersForm.PathColor("waiting_for_removal", false) != Ui.Attention ||
            PeersForm.PathColor("future_state", false) != Ui.Off ||
            PeersForm.PathColor("up", true) != Ui.Off)
            throw new Exception("The tray did not use the documented peer path codes.");
        if (!PeersForm.PathTooltip("no_path", "no path", false).StartsWith("No path\nNAT hole punching failed") ||
            PeersForm.PathTooltip("future_state", "future state", false) != "Future state" ||
            PeersForm.PathTooltip("up", "up", true) != "This computer")
            throw new Exception("The tray did not explain the peer path states.");
    }

    // Only an admin may invite, so other members' Peers windows have no
    // Invite button. The permissions are named for people, and the Invite
    // window takes away each permission whose box is cleared.
    private static void CheckPermissions(DaemonClient client)
    {
        bool HasInvite(bool admin)
        {
            using var page = new PeersForm(client, admin);
            return Descendants(page).OfType<Button>().Any(button => button.Text == "Invite…");
        }
        if (!HasInvite(true) || HasInvite(false))
            throw new Exception("The Peers window did not follow this node's admin permission.");

        using var permissions = System.Text.Json.JsonDocument.Parse("""
            [{"admin":true,"vote":true,"relay":true},{"admin":false,"vote":true,"relay":true},
             {"admin":true,"vote":false,"relay":false},{"admin":false,"vote":false,"relay":false}]
            """);
        var names = permissions.RootElement.Items().Select(PeersForm.PermissionNames).ToArray();
        if (names is not ["All", "Voter, Relay", "Admin", "Member"])
            throw new Exception($"The tray named the permissions {string.Join("; ", names)}.");

        if (InviteForm.InviteArguments(true, true, true) is not ["invite"] ||
            InviteForm.InviteArguments(false, true, false) is not ["invite", "--no-admin", "--no-relay"] ||
            InviteForm.InviteArguments(false, false, false) is not ["invite", "--no-admin", "--no-vote", "--no-relay"])
            throw new Exception("The Invite window did not pass the cleared permissions to the CLI.");
    }

    private static void SetFontScale(Control control, float scale)
    {
        foreach (Control child in control.Controls) SetFontScale(child, scale);
        control.Font = new Font(control.Font.FontFamily, control.Font.SizeInPoints * scale, control.Font.Style);
    }

    private static void Layout(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) Layout(child);
    }

    private static void Check(Form page, string name, float scale)
    {
        foreach (var control in Descendants(page))
        {
            if (control is Button button)
            {
                if (string.IsNullOrWhiteSpace(button.AccessibilityObject.Name))
                    throw new Exception($"{name} at {scale:P0}: a button has no accessible name");
                var preferred = button.GetPreferredSize(Size.Empty);
                if (button.Width + 2 < preferred.Width || button.Height + 2 < preferred.Height)
                    throw new Exception($"{name} at {scale:P0}: {button.Text} is clipped");
                if (name is "confirmation" or "operation" && !page.ClientRectangle.Contains(BoundsInPage(button, page)))
                    throw new Exception($"{name} at {scale:P0}: {button.Text} is outside the dialog");
            }
            if (control is DataGridView grid && string.IsNullOrWhiteSpace(grid.AccessibilityObject.Name))
                throw new Exception($"{name} at {scale:P0}: the member table has no accessible name");
            if (control is ListBox list)
            {
                if (string.IsNullOrWhiteSpace(list.AccessibilityObject.Name))
                    throw new Exception($"{name} at {scale:P0}: the section list has no accessible name");
                if (list.ItemHeight < list.Font.Height * 2 + 12)
                    throw new Exception($"{name} at {scale:P0}: a section cannot show its name and health");
            }
        }
        if (name is "confirmation" or "operation" &&
            !Descendants(page).OfType<Panel>().Any(panel => panel.AutoScroll &&
                panel.Controls.OfType<Label>().Any(label => !string.IsNullOrWhiteSpace(label.AccessibleName))))
            throw new Exception($"{name} at {scale:P0}: the message cannot scroll or lacks an accessible name");
    }

    private static Rectangle BoundsInPage(Control control, Form page)
    {
        var bounds = control.Bounds;
        for (var parent = control.Parent; parent != null && parent != page; parent = parent.Parent)
            bounds.Offset(parent.Left, parent.Top);
        return bounds;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
