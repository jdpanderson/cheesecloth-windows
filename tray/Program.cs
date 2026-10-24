using System.Drawing;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Forms;

namespace Cheesecloth.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var single = new Mutex(true, "Local\\CheeseclothTray", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

internal sealed class TrayContext : ApplicationContext
{
    // Setup and the uninstaller set this event to end the app. Matches
    // TrayExitEvent in cheesecloth.iss.
    private const string ExitEventName = @"Local\CheeseclothTrayExit";

    private readonly DaemonClient client = new();
    private readonly EventWaitHandle exitEvent;
    private readonly RegisteredWaitHandle exitWait;
    private readonly NotifyIcon icon;
    private readonly Icon trayIcon;
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private readonly DateTimeOffset openedAt = DateTimeOffset.UtcNow;
    private bool refreshing;
    private JsonElement status;
    // The open window of each kind. A kind has at most one window.
    private readonly Dictionary<Type, Form> forms = [];

    public TrayContext()
    {
        // The menu, a control, installed this context for the UI thread.
        var ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("the tray app has no UI synchronization context");
        exitEvent = CreateExitEvent();
        exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent,
            (_, _) => ui.Post(_ => ExitThread(), null), null, Timeout.Infinite, executeOnlyOnce: true);
        trayIcon = LoadIcon();
        icon = new NotifyIcon { Icon = trayIcon, Text = "Cheesecloth", Visible = true, ContextMenuStrip = menu };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowStatus(); };
        menu.Opening += async (_, e) =>
        {
            // A menu with no items cancels its opening, so the first click
            // showed nothing. This handler fills the menu, so it opens.
            e.Cancel = false;
            menu.Items.Clear();
            Add("Cheesecloth  •  Loading…", null, false);
            timer.Start();
            await RefreshAsync(opening: true);
        };
        menu.Closed += (_, _) => timer.Stop();
        timer.Tick += async (_, _) => await RefreshAsync();
    }

    // Setup runs elevated, maybe as another user than this app's, so
    // Administrators may set the event too.
    private static EventWaitHandle CreateExitEvent()
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(
            WindowsIdentity.GetCurrent().User!, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, ExitEventName, out _, security);
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("Cheesecloth.Tray.TrayIcon.ico")
            ?? throw new InvalidOperationException("TrayIcon.ico is missing from the tray app");
        return new Icon(stream, new Size(32, 32));
    }

    private void ShowStatus() => Show(() => new StatusForm(client));

    // Shows the one window of its kind, opening it if it is not open. The
    // shell lets the app take the foreground after a click on its icon.
    private void Show<T>(Func<T> create) where T : Form
    {
        if (forms.TryGetValue(typeof(T), out var open))
        {
            menu.Close();
            Ui.Raise(open);
            return;
        }
        var form = create();
        forms[typeof(T)] = form;
        form.FormClosed += (_, _) => forms.Remove(typeof(T));
        Open(form);
    }

    private ToolStripMenuItem Add(string text, Action? action, bool enabled = true)
    {
        var item = new ToolStripMenuItem(text) { Enabled = enabled };
        if (action != null) item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    private void Open(Form form)
    {
        menu.Close();
        form.StartPosition = FormStartPosition.CenterScreen;
        form.Show();
        form.Activate();
    }

    private async Task RefreshAsync(bool opening = false)
    {
        if (refreshing || (!opening && !menu.Visible)) return;
        refreshing = true;
        Exception? failure = null;
        int pending = 0;
        try
        {
            status = await client.CallAsync("status");
            // Only an admin may approve or reject.
            if (status.Text("phase") == "member" && status.Get("permissions").Flag("admin"))
            {
                try { pending = (await client.CallAsync("pending")).GetArrayLength(); }
                catch (DaemonException) { pending = 0; }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally { refreshing = false; }
        if (!menu.Visible) return;
        menu.Items.Clear();
        var phase = status.Text("phase");
        var warningCount = status.Get("warnings").Items().Count();
        string title = failure != null ?
            ErrorTitle(failure, DateTimeOffset.UtcNow - openedAt) :
            phase switch
            {
                "none" => "Not in a network",
                "pending" => "Waiting for approval",
                "stopping" => "Leaving",
                "member" => warningCount > 0 ? "Connected, with warnings" : "Connected",
                _ => "Not responding",
            };
        var header = Add($"●  Cheesecloth  •  {title}", null);
        header.Font = new Font(menu.Font, FontStyle.Bold);
        header.ForeColor = title switch
        {
            "Connected" => Ui.Good,
            "Connected, with warnings" or "Waiting for approval" or "Leaving" => Ui.Attention,
            "Not responding" or "Needs reinstall" or "Administrators only" => Ui.Broken,
            _ => Ui.Off,
        };
        var detail = failure?.Message ?? (phase == "stopping" ? status.Get("warnings").Items().FirstOrDefault().Text() :
            phase == "member" && warningCount > 0 ? $"{status.Text("name")} • {warningCount} warning{(warningCount == 1 ? "" : "s")}" :
            phase == "member" ? $"{status.Text("name")} • {status.Number("members")} member{(status.Number("members") == 1 ? "" : "s")}" : status.Text("name"));
        if (!string.IsNullOrWhiteSpace(detail)) Add(detail, null, false);
        menu.Items.Add(new ToolStripSeparator());
        if (failure == null)
        {
            switch (phase)
            {
                case "none":
                    Add("Create Network…", () => Show(() => new CreateForm(client)));
                    Add("Join Network…", () => Show(() => new JoinForm(client)));
                    break;
                case "pending":
                    Add("Status…", ShowStatus);
                    Add("Cancel Join", CancelJoin);
                    break;
                case "member":
                    Add("Status…", ShowStatus);
                    var admin = status.Get("permissions").Flag("admin");
                    Add("Peers…", () => Show(() => new PeersForm(client, admin)));
                    if (pending > 0) Add($"Approvals ({pending})…", () => Show(() => new ApprovalsForm(client)));
                    break;
                case "stopping":
                    Add("Retry Leave", RetryLeave);
                    Add("Open Logs…", Ui.OpenLogs);
                    break;
            }
        }
        else if (title == "Not responding") Add("Open Logs…", Ui.OpenLogs);
        menu.Items.Add(new ToolStripSeparator());
        Add("Close Tray Application", ExitThread);
    }

    internal static string ErrorTitle(Exception error, TimeSpan sinceOpened) => error switch
    {
        DaemonException { Code: "permission_denied" } => "Administrators only",
        DaemonException { Code: "api_version_mismatch" } => "Needs reinstall",
        _ => sinceOpened < TimeSpan.FromSeconds(15) ? "Starting…" : "Not responding",
    };

    private void CancelJoin()
    {
        if (!Ui.Confirm("Cancel the join?", "This cancels the join on this computer. A member may still need to reject the request.", "Cancel Join", "Keep Waiting")) return;
        Open(new OperationForm("Cancel Join", async () =>
        {
            await client.CallAsync("leave");
            return "Join cancelled.";
        }));
    }

    private void RetryLeave()
    {
        Open(new OperationForm("Retry Leave", async () =>
        {
            try { await client.CallAsync("leave"); }
            catch (Exception ex)
            {
                if (!Ui.Confirm("Leaving failed", $"{ex.Message}\n\nLeave anyway?", "Leave Anyway")) return ex.Message;
                await client.CallAsync("leave", "--force");
            }
            return "Left the network.";
        }));
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        icon.Visible = false;
        icon.Dispose();
        trayIcon.Dispose();
        menu.Dispose();
        timer.Dispose();
        exitWait.Unregister(null);
        exitEvent.Dispose();
        base.ExitThreadCore();
    }
}
