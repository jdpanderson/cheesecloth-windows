using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
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
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

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

    public TrayContext()
    {
        // The menu, a control, installed this context for the UI thread.
        var ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("the tray app has no UI synchronization context");
        exitEvent = CreateExitEvent();
        exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent,
            (_, _) => ui.Post(_ => ExitThread(), null), null, Timeout.Infinite, executeOnlyOnce: true);
        trayIcon = DrawIcon();
        icon = new NotifyIcon { Icon = trayIcon, Text = "Cheesecloth", Visible = true, ContextMenuStrip = menu };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) menu.Show(Cursor.Position); };
        menu.Opening += async (_, _) =>
        {
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

    private static Icon DrawIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Use the same network shape as menubar_template.svg. The dark disc
        // keeps the white shape visible on light and dark Windows taskbars.
        using var dark = new SolidBrush(Color.FromArgb(45, 45, 45));
        using var white = new SolidBrush(Color.White);
        const float scale = 25f / 18f;
        PointF P(float x, float y) => new(3.5f + x * scale, 3.5f + y * scale);
        using var line = new Pen(Color.White, 0.675f * scale)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
        };
        using var thin = new Pen(Color.White, 0.49f * scale)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
        };
        graphics.FillEllipse(dark, 1, 1, 30, 30);
        graphics.DrawBezier(thin, P(7.35f, 2.35f), P(5.20f, 3.35f), P(3.03f, 6.40f), P(2.05f, 11.10f));
        graphics.DrawBezier(thin, P(7.85f, 9.55f), P(5.20f, 8.90f), P(3.04f, 9.12f), P(2.05f, 11.10f));
        graphics.DrawBezier(line, P(7.35f, 2.35f), P(7.45f, 5.13f), P(7.81f, 7.50f), P(7.85f, 9.55f));
        graphics.DrawBezier(line, P(7.35f, 2.35f), P(9.96f, 2.28f), P(12.17f, 4.27f), P(13.55f, 6.32f));
        graphics.DrawBezier(line, P(7.85f, 9.55f), P(9.35f, 8.12f), P(11.37f, 6.82f), P(13.55f, 6.32f));
        graphics.DrawBezier(line, P(7.85f, 9.55f), P(9.77f, 10.80f), P(11.87f, 11.62f), P(14.42f, 12.24f));
        graphics.DrawBezier(line, P(13.55f, 6.32f), P(14.00f, 8.31f), P(14.36f, 10.20f), P(14.42f, 12.24f));
        graphics.DrawBezier(thin, P(7.85f, 9.55f), P(7.30f, 11.03f), P(6.25f, 12.13f), P(5.02f, 12.92f));
        void Node(float x, float y, float radius)
        {
            var center = P(x, y);
            graphics.FillEllipse(white, center.X - radius * scale, center.Y - radius * scale,
                2 * radius * scale, 2 * radius * scale);
        }
        Node(7.35f, 2.35f, 1.12f);
        Node(7.85f, 9.55f, 1.06f);
        Node(13.55f, 6.32f, 1.06f);
        Node(14.42f, 12.24f, 1.06f);
        var handle = bitmap.GetHicon();
        using var borrowed = Icon.FromHandle(handle);
        var owned = (Icon)borrowed.Clone();
        DestroyIcon(handle);
        return owned;
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
                    Add("Create Network…", () => Open(new CreateForm(client)));
                    Add("Join Network…", () => Open(new JoinForm(client)));
                    break;
                case "pending":
                    Add("Status…", () => Open(new StatusForm(client)));
                    Add("Cancel Join", CancelJoin);
                    break;
                case "member":
                    Add("Status…", () => Open(new StatusForm(client)));
                    var admin = status.Get("permissions").Flag("admin");
                    Add("Peers…", () => Open(new PeersForm(client, admin)));
                    if (pending > 0) Add($"Approvals ({pending})…", () => Open(new ApprovalsForm(client)));
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
