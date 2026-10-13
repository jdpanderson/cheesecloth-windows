# Windows tray checks

The Windows tray uses the same network shape as the macOS menu bar icon in
[`tray/menubar_template.svg`](tray/menubar_template.svg). Windows draws it in
white on a dark disc so it remains visible on both light and dark taskbars.

## Automated checks

CI runs `dotnet run --project tests/TrayChecks/TrayChecks.csproj --configuration Release`
on both x64 and Arm64 Windows runners. It constructs each window without a
daemon, applies normal, 150%, and 200% fonts, and checks that buttons have
accessible names and enough space for their text. It checks names for the
member table and section list, and that dialog buttons remain inside their
windows. This is a font and layout check; it does not simulate a monitor's
actual DPI setting or inspect what Narrator announces.

The existing installer check starts the tray and checks that it runs without
a main window. XML validation and comparison with the macOS SVG can run on
any machine with `xmllint` and `cmp`.

## Checks on a Windows desktop

These need a person using an interactive desktop. Record the Windows version,
display scale, text size, theme, and results when they are done.

| Check | What to verify |
| --- | --- |
| Display scale | At 100%, 150%, and 200%, open every window. Text, buttons, warnings, and status rows remain readable and reachable. Move an open window between monitors with different scales. |
| Text size | Increase Windows text size to 200%. Repeat create, join, status, peers, invite, approvals, and confirmation flows. No text or action is cut off. |
| Themes | In light, dark, and high contrast themes, the tray icon is visible and the status text, focus mark, and buttons have sufficient contrast. The network shape matches the macOS icon. |
| Keyboard | Open the tray menu and use every item without a mouse. Use Tab, Shift+Tab, arrows, Enter, Escape, and the close control in every window. Focus remains visible and moves in a useful order. |
| Narrator | Read the menu, status sections and their health, peer table and actions, invite token, approvals, progress, results, and errors. Each control has a useful name and each state is understandable without its colour. |
| Windows sessions | Sign in twice or switch users. Closing one tray must not surprise the other user or change the VPN service. Check install, upgrade, and uninstall while a tray is open. |

Use Accessibility Insights for Windows to inspect names, roles, focus order,
and contrast, then check the same flows with Narrator. Automated checks cannot
replace these observations.

## Service lifecycle decision

Today the installer configures an automatic system service. Each signed-in
user gets a tray app. **Close Tray Application** closes only that app; the VPN
service keeps running. The menu and README use that name to make the current
behaviour clear.

The tray could start the service on launch and stop it on **Exit Cheesecloth**.
That would require an on-demand service, an explicit grant of start and stop
rights to the allowed operator, and a rule for multiple signed-in sessions.
The default Windows service permissions do not give an unelevated tray those
rights. A single tray must not stop the system service while another session
uses it. The design also needs a rule for tray crashes, forced termination,
sign-out, and startup before sign-in. Decide these rules before changing the
installer or service control code.

The current self-signed installer remains the distribution choice for this
personal installation.
