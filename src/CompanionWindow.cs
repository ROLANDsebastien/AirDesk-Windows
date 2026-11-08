using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AirDesk.Companion;

internal sealed class CompanionWindow : Form
{
    private const string AppTitle = "AirDesk Companion";
    private const string StartupValue = "AirDeskCompanion";
    private const string StartupPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly Companion companion;
    private readonly EventWaitHandle showRequest;
    private readonly CancellationTokenSource browserStop = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly NotifyIcon tray;
    private readonly Icon icon;
    private readonly ToolStripMenuItem pauseMenu;
    private readonly ToolStripMenuItem captureMenu;
    private readonly Label summary = TextLabel("");
    private readonly Label activity = TextLabel("");
    private readonly Label accountStatus = TextLabel("");
    private readonly Label captureStatus = TextLabel("");
    private readonly Label privacyStatus = TextLabel("");
    private readonly TextBox code = new() { Width = 285, MaxLength = 42, PlaceholderText = "Code à 32 caractères", AccessibleName = "Code d’association AirDesk" };
    private readonly Button link = PushButton("Associer");
    private readonly Button refresh = PushButton("Actualiser les comptes");
    private readonly Button remove = PushButton("Retirer le compte");
    private readonly Button disconnect = PushButton("Arrêter les envois");
    private readonly ListBox accounts = new() { Width = 410, Height = 76, AccessibleName = "Comptes associés" };
    private readonly Button retry = PushButton("Réessayer l’envoi");
    private FlowLayoutPanel? accountActions;
    private FlowLayoutPanel? syncActions;
    private readonly CheckBox pause = Toggle("Mettre la collecte en pause");
    private readonly CheckBox startup = Toggle("Démarrer AirDesk à l’ouverture de session Windows");
    private readonly CheckBox apps = Toggle("Suivre l’application au premier plan");
    private readonly CheckBox sites = Toggle("Suivre les domaines avec l’extension Edge");
    private readonly CheckBox links = Toggle("Conserver les adresses complètes des sites suivis");
    private readonly TextBox excluded = new() { Width = 370, PlaceholderText = "exemple.be, banque.be" };
    private readonly Button saveExcluded = PushButton("Enregistrer");
    private readonly NumericUpDown idle = Number(1, 30);
    private readonly CheckBox periodic = Toggle("Capturer l’écran principal à intervalle régulier");
    private readonly CheckBox appChange = Toggle("Capturer lors d’un changement d’application");
    private readonly CheckBox siteChange = Toggle("Capturer lors d’un changement de site dans le navigateur");
    private readonly CheckBox cloud = Toggle("Envoyer les nouvelles captures aux comptes associés");
    private readonly NumericUpDown interval = Number(1, 60);
    private readonly Button capture = PushButton("Capturer maintenant");
    private readonly ComboBox retention = new() { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListBox captures = new() { Width = 405, Height = 115, SelectionMode = SelectionMode.MultiExtended, AccessibleName = "Captures enregistrées sur ce PC" };
    private readonly Button openImage = PushButton("Ouvrir l’image");
    private readonly Button deleteImages = PushButton("Supprimer la sélection");
    private readonly Button deleteAll = PushButton("Supprimer toutes les captures locales");
    private readonly Label systemStatus = TextLabel("");
    private readonly Button scan = PushButton("Rechercher les mises à jour Windows");
    private LocalCapture[] displayedCaptures = [];
    private Account[] displayedAccounts = [];
    private IntPtr notificationWindow;
    private IntPtr displayNotification;
    private bool refreshing;
    private bool quitting;

    public CompanionWindow(Companion companion, EventWaitHandle showRequest)
    {
        this.companion = companion;
        this.showRequest = showRequest;
        Text = AppTitle;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(685, 620);
        MinimumSize = new Size(530, 440);

        using var stream = typeof(CompanionWindow).Assembly.GetManifestResourceStream("AirDesk.ico")!;
        icon = new Icon(stream);
        Icon = icon;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Ouvrir AirDesk Companion", null, (_, _) => Reveal());
        pauseMenu = new ToolStripMenuItem("Mettre en pause", null, (_, _) => SetPaused(!companion.State.Settings.Paused));
        captureMenu = new ToolStripMenuItem("Capturer l’écran principal", null, (_, _) => companion.TakeCapture());
        menu.Items.Add(pauseMenu);
        menu.Items.Add(captureMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => Exit());
        tray = new NotifyIcon { Text = AppTitle, Icon = icon, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Reveal();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(StatusTab());
        tabs.TabPages.Add(PrivacyTab());
        tabs.TabPages.Add(CaptureTab());
        tabs.TabPages.Add(SystemTab());
        Controls.Add(tabs);

        var settings = companion.State.Settings;
        excluded.Text = string.Join(", ", settings.ExcludedDomains);
        startup.Checked = IsStartupEnabled();
        pause.CheckedChanged += (_, _) => { if (!refreshing) SetPaused(pause.Checked); };
        startup.CheckedChanged += (_, _) => { if (!refreshing) SetStartup(startup.Checked); };
        foreach (var box in new[] { apps, sites, links, periodic, appChange, siteChange, cloud })
            box.CheckedChanged += (_, _) => SaveSettings();
        idle.ValueChanged += (_, _) => SaveSettings();
        interval.ValueChanged += (_, _) => SaveSettings();
        retention.SelectedIndexChanged += (_, _) =>
        {
            if (refreshing || retention.SelectedItem is not int limit) return;
            if (limit < companion.State.Captures.Length &&
                !Confirm($"Conserver les {limit:N0} images les plus récentes et supprimer les autres sur ce PC ?"))
            {
                UpdateView();
                return;
            }
            companion.SettingsChanged(companion.State.Settings with { CaptureLimit = limit });
        };

        companion.Changed += UpdateView;
        timer.Tick += (_, _) =>
        {
            if (showRequest.WaitOne(0)) Reveal();
            companion.Tick();
        };
    }

    public void Start(bool trayOnly)
    {
        _ = Handle; // Receive session and power events even while the window is hidden.
        _ = BrowserBridge.Serve((request, cancel) => InvokeAsync(() => companion.ReceiveBrowser(request), cancel), browserStop.Token);
        if (!trayOnly) Reveal();
        timer.Start();
    }

    private TabPage StatusTab()
    {
        var (page, panel) = Tab("État et comptes");
        accountActions = Row(refresh, remove);
        syncActions = Row(disconnect, retry);
        retry.Click += (_, _) => companion.RetryNow();
        Add(panel, Heading("AirDesk Companion"), summary, activity, pause, startup,
            Heading("Associer ce PC"), TextLabel("Dans AirDesk, créez un code d’association et saisissez-le ici."),
            Row(code, link), accountStatus, accounts, accountActions, syncActions);
        link.Click += async (_, _) =>
        {
            var value = code.Text;
            code.Clear();
            await companion.PairOrAdd(value);
            if (companion.Linked && !companion.AccountsLoaded) await companion.ManageAccounts();
        };
        code.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            args.SuppressKeyPress = true;
            link.PerformClick();
        };
        refresh.Click += async (_, _) => await companion.ManageAccounts();
        accounts.SelectedIndexChanged += (_, _) => remove.Enabled = companion.Linked && !companion.Busy && accounts.SelectedIndex >= 0;
        remove.Click += async (_, _) =>
        {
            if (accounts.SelectedItem is AccountItem entry &&
                Confirm("Retirer ce compte d’AirDesk pour ce PC ? Son accès distant sera révoqué."))
                await companion.ManageAccounts(remove: entry.Account.Id);
        };
        disconnect.Click += (_, _) =>
        {
            if (Confirm("Effacer l’association sur ce PC et arrêter les envois ? Les données locales seront conservées."))
                companion.Disconnect();
        };
        return page;
    }

    private TabPage PrivacyTab()
    {
        var (page, panel) = Tab("Activité et sites");
        Add(panel, Heading("Activité Windows"), apps, Row(TextLabel("Inactivité après"), idle, TextLabel("minutes")),
            Heading("Navigation"), PushButton("Configurer l’extension Edge", ConfigureEdge), sites, links,
            TextLabel("Les domaines exclus et leurs sous-domaines sont ignorés, y compris pour les captures déclenchées par un changement de site."),
            Row(excluded, saveExcluded), privacyStatus);
        saveExcluded.Click += (_, _) =>
        {
            var entries = excluded.Text.Split([',', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(WindowsTelemetry.NormalizeDomain).Distinct().ToArray();
            if (entries.Contains(null) || entries.Length > 100)
            {
                MessageBox.Show(this, "Saisissez jusqu’à 100 noms de domaine, séparés par des virgules.", AppTitle);
                return;
            }
            companion.SettingsChanged(companion.State.Settings with { ExcludedDomains = entries.OfType<string>().ToArray() });
            excluded.Text = string.Join(", ", companion.State.Settings.ExcludedDomains);
        };
        return page;
    }

    private TabPage CaptureTab()
    {
        var (page, panel) = Tab("Captures");
        retention.Items.AddRange([500, 1000, 5000]);
        Add(panel, Heading("Écran principal"), periodic, Row(TextLabel("Intervalle"), interval, TextLabel("minutes")),
            appChange, siteChange, cloud, captureStatus,
            Row(capture, PushButton("Ouvrir les captures", () => OpenFolder(Path.Combine(companion.Root, "Captures")))),
            Row(TextLabel("Nombre maximal d’images"), retention),
            TextLabel("Les captures expirent au bout de 7 jours."), captures,
            Row(openImage, deleteImages), deleteAll);
        openImage.Click += (_, _) => OpenSelectedCapture();
        deleteImages.Click += (_, _) => DeleteSelectedCaptures();
        deleteAll.Click += (_, _) =>
        {
            if (Confirm("Supprimer toutes les captures de ce PC ? Les images déjà envoyées se gèrent depuis AirDesk."))
                companion.DeleteLocalCaptures();
        };
        captures.SelectedIndexChanged += (_, _) => UpdateCaptureActions();
        captures.DoubleClick += (_, _) => OpenSelectedCapture();
        capture.Click += (_, _) =>
        {
            // Let the companion window disappear before a manual screenshot.
            Hide();
            var delay = new System.Windows.Forms.Timer { Interval = 300 };
            delay.Tick += (_, _) =>
            {
                delay.Dispose();
                if (quitting) return;
                companion.TakeCapture();
                Reveal();
            };
            delay.Start();
        };
        return page;
    }

    private TabPage SystemTab()
    {
        var (page, panel) = Tab("Système");
        var inventoryButton = PushButton("Actualiser l’inventaire");
        Add(panel, Heading("Diagnostic"), TextLabel("Les exports restent sur ce PC et excluent les liens, images et identifiants."),
            Row(PushButton("Exporter le bilan technique", () => Export("diagnostic", TechnicalReport())),
                PushButton("Exporter les performances", () => Export("performances", companion.PerformanceReport()))),
            PushButton("Ouvrir le journal local", () => OpenFolder(companion.Root)),
            Heading("Système et versions"), TextLabel(WindowsTelemetry.OperatingSystemName()),
            Row(inventoryButton, PushButton("Ouvrir Windows Update", () => OpenFolder("ms-settings:windowsupdate"))),
            scan, systemStatus);
        inventoryButton.Click += async (_, _) =>
        {
            inventoryButton.Enabled = false;
            try { systemStatus.Text = await companion.RefreshInventory(); }
            catch (Exception) { systemStatus.Text = "Inventaire Windows indisponible."; }
            finally { inventoryButton.Enabled = true; }
        };
        scan.Click += async (_, _) =>
        {
            scan.Enabled = false;
            systemStatus.Text = "Recherche Windows Update en cours…";
            try { systemStatus.Text = await Task.Run(WindowsUpdate.Search); }
            finally { scan.Enabled = true; }
        };
        return page;
    }

    private void ConfigureEdge()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Extension Edge");
        try { Clipboard.SetText(path); }
        catch (ExternalException) { }
        OpenFolder(path);
        OpenFolder("microsoft-edge:edge://extensions");
        MessageBox.Show(this, "Dans Edge, ouvrez edge://extensions, activez le Mode développeur, puis cliquez sur Charger l’extension décompressée. Sélectionnez ce dossier :\n\n" + path,
            AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void UpdateCaptureActions()
    {
        openImage.Enabled = captures.SelectedItems.Count == 1;
        deleteImages.Enabled = captures.SelectedItems.Count > 0;
        deleteAll.Enabled = captures.Items.Count > 0;
    }

    private void OpenSelectedCapture()
    {
        if (captures.SelectedItems.Count == 1 && captures.SelectedItem is CaptureItem item)
            OpenFolder(Path.Combine(companion.Root, "Captures", item.Capture.Id.ToString("N") + ".jpg"));
    }

    private void DeleteSelectedCaptures()
    {
        var selected = captures.SelectedItems.OfType<CaptureItem>().Select(item => item.Capture.Id).ToHashSet();
        if (selected.Count > 0 && Confirm($"Supprimer {selected.Count} image(s) de ce PC ? Les copies déjà envoyées restent accessibles depuis AirDesk."))
            companion.DeleteLocalCaptures(selected);
    }

    private string TechnicalReport()
    {
        var state = companion.State;
        return string.Join(Environment.NewLine,
            "AirDesk Companion Windows " + typeof(CompanionWindow).Assembly.GetName().Version,
            WindowsTelemetry.OperatingSystemName(),
            $"Association : {(companion.Linked ? "active" : "à configurer")}",
            $"Démarrage à la connexion : {(IsStartupEnabled() ? "activé" : "désactivé")}",
            $"Pause : {state.Settings.Paused}",
            $"Mesures en attente : {state.Pending.Length}",
            $"Dernier envoi confirmé : {state.LastSent?.ToLocalTime():g}",
            $"Captures locales : {state.Captures.Length}",
            $"Captures en attente : {state.Captures.Count(item => item.CloudPending)}",
            $"Capture possible : {companion.CaptureAllowed}",
            companion.SiteStatus,
            companion.Error ?? companion.SyncStatus);
    }

    private void Export(string name, string text)
    {
        using var dialog = new SaveFileDialog { FileName = $"AirDesk-Companion-{name}.txt", Filter = "Fichiers texte (*.txt)|*.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, text); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Le rapport n’a pas pu être enregistré.", AppTitle);
        }
    }

    private void SaveSettings()
    {
        if (refreshing) return;
        var settings = companion.State.Settings;
        var prompt = cloud.Checked && !settings.SyncCaptures ? "Partager les nouvelles captures avec les comptes associés à ce PC ?"
            : periodic.Checked && !settings.PeriodicCapture ? "Activer les captures périodiques de l’écran principal ?"
            : appChange.Checked && !settings.CaptureOnAppChange ? "Activer les captures lorsque l’application au premier plan change ?"
            : siteChange.Checked && !settings.CaptureOnSiteChange ? "Activer les captures quand le site actif change ?"
            : sites.Checked && !settings.TrackSites ? "Enregistrer les domaines de l’onglet actif transmis par l’extension ?"
            : links.Checked && !settings.FullLinks ? "Autoriser la lecture des adresses complètes des onglets suivis ?"
            : null;
        var newSettings = settings with
        {
            TrackApps = apps.Checked, TrackSites = sites.Checked, FullLinks = links.Checked,
            PeriodicCapture = periodic.Checked, CaptureOnAppChange = appChange.Checked,
            CaptureOnSiteChange = siteChange.Checked, SyncCaptures = cloud.Checked,
            IdleMinutes = (int)idle.Value, CaptureMinutes = (int)interval.Value
        };
        if (prompt is not null && !Confirm(prompt))
        {
            UpdateView();
            return;
        }
        companion.SettingsChanged(newSettings with { Paused = companion.State.Settings.Paused });
    }

    private void SetPaused(bool paused) =>
        companion.SettingsChanged(companion.State.Settings with { Paused = paused });

    private void UpdateView()
    {
        if (quitting) return;
        refreshing = true;
        try
        {
            var state = companion.State;
            var settings = state.Settings;
            pause.Checked = settings.Paused;
            apps.Checked = settings.TrackApps;
            sites.Checked = settings.TrackSites;
            links.Checked = settings.FullLinks;
            links.Enabled = settings.TrackSites || settings.CaptureOnSiteChange;
            periodic.Checked = settings.PeriodicCapture;
            appChange.Checked = settings.CaptureOnAppChange;
            siteChange.Checked = settings.CaptureOnSiteChange;
            cloud.Checked = settings.SyncCaptures;
            retention.SelectedItem = settings.CaptureLimit;
            idle.Value = Math.Clamp(settings.IdleMinutes, (int)idle.Minimum, (int)idle.Maximum);
            interval.Value = Math.Clamp(settings.CaptureMinutes, (int)interval.Minimum, (int)interval.Maximum);
            summary.Text = companion.Error ?? (companion.Linked
                ? state.LastSent is { } sent
                    ? $"Associé à AirDesk · dernier envoi confirmé le {sent.LocalDateTime:g}"
                    : "Code accepté · en attente du premier envoi confirmé"
                : "Collecte locale · association AirDesk requise");
            var activityName = state.Latest?.State switch
            {
                "active" => "Actif", "idle" => "Inactif", "paused" => "En pause",
                "asleep" => "Session indisponible", _ => "Activité inconnue"
            };
            var measures = state.Pending.Length <= 1 ? "mesure" : "mesures";
            activity.Text = $"{activityName} · {state.Pending.Length} {measures} en attente";
            if (companion.SyncStatus is { } sync) activity.Text += "\n" + sync;
            accountStatus.Text = companion.AccountStatus ?? "";
            var images = state.Captures.Length <= 1 ? "image locale" : "images locales";
            captureStatus.Text = $"{state.Captures.Length} {images} · {state.Captures.Count(item => item.CloudPending)} en attente d’envoi\n" +
                (companion.CaptureStatus ?? (companion.CaptureAllowed ? "Capture disponible" : "Capture suspendue"));
            privacyStatus.Text = companion.SiteStatus;
            link.Text = companion.Linked ? "Ajouter un compte" : "Associer";
            link.Enabled = !companion.Busy && companion.Error is null;
            refresh.Enabled = companion.Linked && !companion.Busy;
            remove.Enabled = companion.Linked && !companion.Busy && accounts.SelectedIndex >= 0;
            accounts.Visible = companion.Linked;
            accountActions!.Visible = companion.Linked;
            syncActions!.Visible = companion.HasLocalAssociation;
            disconnect.Enabled = companion.HasLocalAssociation && !companion.Busy;
            retry.Enabled = companion.Linked && !companion.Busy && state.Pending.Length > 0;
            captureMenu.Enabled = capture.Enabled = companion.CaptureAllowed;
            pauseMenu.Text = settings.Paused ? "Reprendre la collecte" : "Mettre en pause";
            tray.Text = AppTitle + (settings.Paused ? " · En pause" : " · " + activityName);
            if (!ReferenceEquals(displayedCaptures, state.Captures))
            {
                var selected = captures.SelectedItems.OfType<CaptureItem>().Select(item => item.Capture.Id).ToHashSet();
                displayedCaptures = state.Captures;
                captures.BeginUpdate();
                try
                {
                    captures.Items.Clear();
                    captures.Items.AddRange(displayedCaptures.Reverse().Select(item => new CaptureItem(item)).ToArray());
                    for (var i = 0; i < captures.Items.Count; i++)
                        if (captures.Items[i] is CaptureItem item && selected.Contains(item.Capture.Id))
                            captures.SetSelected(i, true);
                }
                finally { captures.EndUpdate(); }
            }
            UpdateCaptureActions();
            if (!ReferenceEquals(displayedAccounts, companion.Accounts))
            {
                displayedAccounts = companion.Accounts;
                accounts.Items.Clear();
                accounts.Items.AddRange(displayedAccounts.Select(item => new AccountItem(item)).ToArray());
            }
        }
        finally { refreshing = false; }
    }

    private void Reveal()
    {
        if (quitting) return;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        UpdateView();
        if (companion.Linked && !companion.AccountsLoaded) _ = companion.ManageAccounts();
    }

    private static string StartupCommand => $"\"{Application.ExecutablePath}\" --tray";

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupPath);
            return string.Equals(key?.GetValue(StartupValue) as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupPath);
            if (enabled) key.SetValue(StartupValue, StartupCommand);
            else key.DeleteValue(StartupValue, throwOnMissingValue: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(this, "Windows a refusé la modification du démarrage automatique.", AppTitle);
            refreshing = true;
            startup.Checked = IsStartupEnabled();
            refreshing = false;
        }
    }

    private void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { MessageBox.Show(this, "Dossier indisponible.", AppTitle); }
    }

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        notificationWindow = Handle;
        WindowsTelemetry.WTSRegisterSessionNotification(notificationWindow, 0);
        displayNotification = WindowsTelemetry.RegisterPowerSettingNotification(Handle, WindowsTelemetry.SessionDisplayStatus, 0);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (displayNotification != IntPtr.Zero) WindowsTelemetry.UnregisterPowerSettingNotification(displayNotification);
        WindowsTelemetry.WTSUnRegisterSessionNotification(notificationWindow);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x02B1) // WM_WTSSESSION_CHANGE
        {
            switch (message.WParam.ToInt32())
            {
                case 2 or 4 or 6 or 7: companion.SetSession(isLocked: true); break;
                case 1 or 3 or 5 or 8: companion.SetSession(isLocked: false); break;
            }
        }
        if (message.Msg == 0x0218) // WM_POWERBROADCAST
        {
            switch (message.WParam.ToInt32())
            {
                case 4: companion.SetSession(isSleeping: true); break;
                case 7 or 18: companion.SetSession(isSleeping: false); break;
                case 0x8013 when message.LParam != IntPtr.Zero &&
                    Marshal.PtrToStructure<Guid>(message.LParam) == WindowsTelemetry.SessionDisplayStatus &&
                    Marshal.ReadInt32(message.LParam, 16) >= 4:
                    companion.SetSession(screenOff: Marshal.ReadInt32(message.LParam, 20) == 0);
                    break;
            }
        }
        base.WndProc(ref message);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else if (e.CloseReason == CloseReason.WindowsShutDown) Application.ExitThread();
        base.OnFormClosing(e);
    }

    private void Exit()
    {
        quitting = true;
        tray.Visible = false;
        Close();
        Application.ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            browserStop.Cancel();
            companion.Changed -= UpdateView;
            timer.Dispose();
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
            icon.Dispose();
        }
        base.Dispose(disposing);
    }

    private static (TabPage, FlowLayoutPanel) Tab(string name)
    {
        var page = new TabPage(name);
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(18), BackColor = SystemColors.Window
        };
        page.Controls.Add(panel);
        panel.SizeChanged += (_, _) =>
        {
            foreach (var label in panel.Controls.OfType<Label>())
                label.MaximumSize = new Size(Math.Max(260, panel.ClientSize.Width - 55), 0);
        };
        return (page, panel);
    }

    private static void Add(Control parent, params Control[] children) => parent.Controls.AddRange(children);
    private static FlowLayoutPanel Row(params Control[] children)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        Add(row, children);
        return row;
    }

    private static Label TextLabel(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 5, 3, 5) };
    private static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 13), Margin = new Padding(3, 10, 3, 4)
    };
    private static CheckBox Toggle(string text) => new() { Text = text, AutoSize = true };
    private static Button PushButton(string text, Action? action = null)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(4, 2, 4, 2) };
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }
    private static NumericUpDown Number(int minimum, int maximum) => new() { Minimum = minimum, Maximum = maximum, Width = 58 };
    private sealed record CaptureItem(LocalCapture Capture)
    {
        public override string ToString() => Capture.CapturedAt.LocalDateTime.ToString("g") +
            (Capture.CloudPending ? " · en attente d’envoi" : "");
    }

    private sealed record AccountItem(Account Account)
    {
        public override string ToString() => Account.Email ?? Account.Id.ToString();
    }
}
