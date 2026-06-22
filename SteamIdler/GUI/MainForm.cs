using SteamGameIdler.Shared;
using System.Drawing;
using System.Windows.Forms;

namespace SteamGameIdler.GUI;

/*
 * Authenticator
 */
internal sealed class GuiAuthenticator : SteamKit2.Authentication.IAuthenticator
{
    private readonly Form _owner;
    public GuiAuthenticator(Form owner) => _owner = owner;

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        string prompt = previousCodeWasIncorrect
            ? "The previous code was incorrect.\n\nEnter your Steam Guard mobile authenticator code:"
            : "Enter your Steam Guard mobile authenticator code:";
        return Task.FromResult(ShowInput("Steam Guard — Mobile Authenticator", prompt));
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        string prompt = previousCodeWasIncorrect
            ? $"The previous code was incorrect.\n\nEnter the Steam Guard code sent to {email}:"
            : $"Enter the Steam Guard code sent to {email}:";
        return Task.FromResult(ShowInput("Steam Guard — Email Code", prompt));
    }

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        string result = ShowInput(
            "Steam Guard — Device Confirmation",
            "Check your Steam mobile app and tap Confirm.\n\nType 'ok' here once you have confirmed, then click OK.");
        return Task.FromResult(true); // always continue
    }

    // Run on the UI thread
    private string ShowInput(string title, string prompt)
    {
        string value = "";
        _owner.Invoke(() =>
        {
            using var dlg = new InputDialog(title, prompt);
            if (dlg.ShowDialog(_owner) == DialogResult.OK)
                value = dlg.Value;
        });
        return value;
    }
}

/*
 * Input Dialog
 */
internal sealed class InputDialog : Form
{
    public string Value => _txt.Text.Trim();
    private readonly TextBox _txt;

    public InputDialog(string title, string prompt)
    {
        Text = title;
        Size = new Size(420, 180);
        MinimumSize = new Size(360, 160);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        BackColor = Color.FromArgb(30, 30, 38);
        ForeColor = Color.FromArgb(210, 210, 210);

        var lbl = new Label
        {
            Text = prompt,
            Location = new Point(12, 12),
            Size = new Size(380, 60),
            ForeColor = Color.FromArgb(200, 200, 200)
        };

        _txt = new TextBox
        {
            Location = new Point(12, 80),
            Size = new Size(380, 24),
            BackColor = Color.FromArgb(40, 40, 50),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        var ok = new Button
        {
            Text = "OK",
            Location = new Point(210, 112),
            Size = new Size(88, 28),
            BackColor = Color.FromArgb(40, 90, 50),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.OK
        };
        var cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(306, 112),
            Size = new Size(88, 28),
            BackColor = Color.FromArgb(60, 40, 40),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.Cancel
        };

        Controls.AddRange(new Control[] { lbl, _txt, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/*
 * Main FORM
 */
public partial class MainForm : Form
{
    private SteamAuth? _auth;
    private GameIdler? _idler;
    private AppConfig _config = AppConfig.LoadOrCreate();
    private CancellationTokenSource? _cts;

    // Controls
    private TabControl tabs = null!;
    private TabPage tabStatus = null!, tabFriends = null!,
                          tabChat = null!, tabSettings = null!;

    // Status tab
    private RichTextBox logBox = null!;
    private Button btnConnect = null!, btnStop = null!;
    private Label lblStatus = null!;
    private ListBox lstGames = null!;
    private Button btnAddGame = null!, btnRemoveGame = null!;

    // Friends tab
    private ListView lvFriends = null!;
    private Button btnRefreshFriends = null!;

    // Chat tab
    private RichTextBox chatLog = null!;
    private TextBox txtTo = null!, txtMessage = null!;
    private Button btnSend = null!;
    private Label lblChatHint = null!;

    // Settings tab
    private TextBox txtUsername = null!, txtPassword = null!,
                          txtAutoReply = null!;
    private CheckBox chkAutoReply = null!;
    private Button btnSaveSettings = null!, btnClearToken = null!;
    private Label lblSaved = null!, lblTokenStatus = null!;

    // Status bar
    private StatusStrip statusBar = null!;
    private ToolStripStatusLabel lblBarLeft = null!, lblBarRight = null!;

    public MainForm()
    {
        InitializeComponent();
        LoadSettingsIntoUI();
    }

    /*
     * UI CONSTRUCTION
     */
    private void InitializeComponent()
    {
        Text = "Steam Game Idler v2.1  — by SyntX";
        Size = new Size(860, 620);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.FromArgb(23, 23, 28);
        ForeColor = Color.FromArgb(220, 220, 220);
        Icon = SystemIcons.Application;

        BuildStatusBar();
        BuildTabs();
        BuildStatusTab();
        BuildFriendsTab();
        BuildChatTab();
        BuildSettingsTab();

        Controls.Add(tabs);
        Controls.Add(statusBar);

        Shown += (_, _) => RefreshGameList();
        FormClosing += OnFormClosing;
    }

    /*
     * Status Bar
     */
    private void BuildStatusBar()
    {
        statusBar = new StatusStrip { BackColor = Color.FromArgb(30, 30, 36) };
        lblBarLeft = new ToolStripStatusLabel("● Disconnected") { ForeColor = Color.FromArgb(160, 160, 160), Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        lblBarRight = new ToolStripStatusLabel("Idle") { ForeColor = Color.FromArgb(120, 120, 120) };
        statusBar.Items.Add(lblBarLeft);
        statusBar.Items.Add(new ToolStripSeparator());
        statusBar.Items.Add(lblBarRight);
    }

    /*
     * Tabs
     */
    private void BuildTabs()
    {
        tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(12, 4),
            DrawMode = TabDrawMode.OwnerDrawFixed
        };
        tabs.DrawItem += DrawTab;

        tabStatus   = new TabPage("  Dashboard  ") { BackColor = Color.FromArgb(23, 23, 28), ForeColor = Color.FromArgb(220, 220, 220) };
        tabFriends  = new TabPage("  Friends    ") { BackColor = Color.FromArgb(23, 23, 28), ForeColor = Color.FromArgb(220, 220, 220) };
        tabChat     = new TabPage("  Chat       ") { BackColor = Color.FromArgb(23, 23, 28), ForeColor = Color.FromArgb(220, 220, 220) };
        tabSettings = new TabPage("  Settings   ") { BackColor = Color.FromArgb(23, 23, 28), ForeColor = Color.FromArgb(220, 220, 220) };

        tabs.TabPages.AddRange(new[] { tabStatus, tabFriends, tabChat, tabSettings });
    }

    private void DrawTab(object? sender, DrawItemEventArgs e)
    {
        var tc = (TabControl)sender!;
        var page = tc.TabPages[e.Index];
        var rect = e.Bounds;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        e.Graphics.FillRectangle(new SolidBrush(selected ? Color.FromArgb(40, 40, 50) : Color.FromArgb(28, 28, 35)), rect);

        var accent = Color.FromArgb(100, 149, 237);
        if (selected)
            e.Graphics.FillRectangle(new SolidBrush(accent), rect.X, rect.Bottom - 2, rect.Width, 2);

        var tf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(page.Text, new Font("Segoe UI", 9f, selected ? FontStyle.Bold : FontStyle.Regular),
            new SolidBrush(selected ? Color.White : Color.FromArgb(170, 170, 170)), rect, tf);
    }

    /*
     * Dashboard Tab
     */
    private void BuildStatusTab()
    {
        var p = tabStatus;

        lblStatus = MakeLabel("● Not connected", 12, 12, 400, 22, Color.FromArgb(200, 80, 80));
        lblStatus.Font = new Font("Segoe UI", 10f, FontStyle.Bold);

        btnConnect  = MakeDarkButton("Connect & Idle", 12, 42, 130, 34);
        btnStop     = MakeDarkButton("Stop", 152, 42, 80, 34, enabled: false);
        var btnRefLog = MakeDarkButton("Open Chat Log", 242, 42, 120, 34);

        btnConnect.BackColor = Color.FromArgb(40, 90, 50);
        btnStop.BackColor    = Color.FromArgb(90, 40, 40);

        var lblGames = MakeLabel("Games to idle  (AppIDs):", 12, 90, 220, 18);
        lstGames = new ListBox
        {
            Location = new Point(12, 112),
            Size = new Size(220, 200),
            BackColor = Color.FromArgb(32, 32, 40),
            ForeColor = Color.FromArgb(210, 210, 210),
            BorderStyle = BorderStyle.FixedSingle
        };
        btnAddGame    = MakeDarkButton("+ Add",    12, 320, 105, 28);
        btnRemoveGame = MakeDarkButton("− Remove", 127, 320, 105, 28);

        var lblLog = MakeLabel("Live Log:", 250, 90, 100, 18);
        logBox = new RichTextBox
        {
            Location = new Point(250, 112),
            Size = new Size(580, 370),
            BackColor = Color.FromArgb(16, 16, 20),
            ForeColor = Color.FromArgb(180, 220, 180),
            Font = new Font("Consolas", 8.5f),
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        p.Controls.AddRange(new Control[] {
            lblStatus, btnConnect, btnStop, btnRefLog,
            lblGames, lstGames, btnAddGame, btnRemoveGame,
            lblLog, logBox
        });

        btnConnect.Click    += OnConnectClick;
        btnStop.Click       += OnStopClick;
        btnRefLog.Click     += (_, _) => OpenChatLog();
        btnAddGame.Click    += OnAddGame;
        btnRemoveGame.Click += OnRemoveGame;
    }

    /*
     * Friends Tab
     */
    private void BuildFriendsTab()
    {
        var p = tabFriends;

        var lbl = MakeLabel("Your Steam Friends", 12, 12, 300, 20);
        lbl.Font = new Font("Segoe UI", 10f, FontStyle.Bold);

        btnRefreshFriends = MakeDarkButton("Refresh", 12, 40, 100, 28);

        lvFriends = new ListView
        {
            Location = new Point(12, 78),
            Size = new Size(810, 430),
            BackColor = Color.FromArgb(28, 28, 36),
            ForeColor = Color.FromArgb(210, 210, 210),
            BorderStyle = BorderStyle.None,
            FullRowSelect = true,
            GridLines = false,
            View = View.Details,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        lvFriends.Columns.Add("Name", 200);
        lvFriends.Columns.Add("SteamID64", 180);
        lvFriends.Columns.Add("Status", 100);

        lvFriends.DoubleClick += (_, _) =>
        {
            if (lvFriends.SelectedItems.Count == 0) return;
            var item = lvFriends.SelectedItems[0];
            txtTo.Text = item.SubItems[1].Text;
            tabs.SelectedTab = tabChat;
        };

        var hint = MakeLabel("Double-click a friend to open a chat with them.", 12, 516, 500, 18, Color.FromArgb(120, 120, 120));

        p.Controls.AddRange(new Control[] { lbl, btnRefreshFriends, lvFriends, hint });
        btnRefreshFriends.Click += (_, _) => RefreshFriendList();
    }

    /*
     * Chat Tab
     */
    private void BuildChatTab()
    {
        var p = tabChat;

        var lbl = MakeLabel("Chat", 12, 12, 100, 20);
        lbl.Font = new Font("Segoe UI", 10f, FontStyle.Bold);

        lblChatHint = MakeLabel("Incoming messages appear here in real-time. You can also send messages to friends.", 12, 36, 700, 18, Color.FromArgb(120, 120, 120));

        chatLog = new RichTextBox
        {
            Location = new Point(12, 62),
            Size = new Size(810, 380),
            BackColor = Color.FromArgb(16, 16, 20),
            ForeColor = Color.FromArgb(200, 220, 200),
            Font = new Font("Consolas", 8.5f),
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        var lblTo = MakeLabel("To (SteamID64):", 12, 454, 120, 20);
        txtTo = new TextBox
        {
            Location = new Point(134, 452),
            Size = new Size(180, 24),
            BackColor = Color.FromArgb(32, 32, 40),
            ForeColor = Color.FromArgb(210, 210, 210),
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblMsg = MakeLabel("Message:", 12, 486, 80, 20);
        txtMessage = new TextBox
        {
            Location = new Point(134, 484),
            Size = new Size(550, 24),
            BackColor = Color.FromArgb(32, 32, 40),
            ForeColor = Color.FromArgb(210, 210, 210),
            BorderStyle = BorderStyle.FixedSingle
        };

        btnSend = MakeDarkButton("Send", 694, 484, 80, 24);
        btnSend.BackColor = Color.FromArgb(40, 80, 120);

        p.Controls.AddRange(new Control[] {
            lbl, lblChatHint, chatLog,
            lblTo, txtTo, lblMsg, txtMessage, btnSend
        });

        btnSend.Click += OnSendMessage;
        txtMessage.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { OnSendMessage(null, EventArgs.Empty); e.Handled = true; }
        };
    }

    /*
     * Settings Tab
     */
    private void BuildSettingsTab()
    {
        var p = tabSettings;

        var lbl = MakeLabel("Settings", 12, 12, 200, 22);
        lbl.Font = new Font("Segoe UI", 11f, FontStyle.Bold);

        var lblUser = MakeLabel("Steam Username:", 12, 56, 150, 20);
        txtUsername = MakeTextBox(170, 54, 300);

        var lblPass = MakeLabel("Steam Password:", 12, 92, 150, 20);
        txtPassword = MakeTextBox(170, 90, 300);
        txtPassword.PasswordChar = '●';

        // Session token status row
        lblTokenStatus = MakeLabel(
            HasSavedToken() ? "✓ Saved session token — will log in automatically (no password needed)."
                            : "No saved token — will prompt for Steam Guard on first login.",
            12, 128, 600, 20,
            HasSavedToken() ? Color.FromArgb(80, 200, 120) : Color.FromArgb(160, 160, 160));

        btnClearToken = MakeDarkButton("Clear Saved Token", 12, 154, 160, 28);
        btnClearToken.BackColor = Color.FromArgb(80, 40, 40);
        btnClearToken.Enabled   = HasSavedToken();

        var sep1 = new Label
        {
            Location  = new Point(12, 196),
            Size      = new Size(780, 1),
            BackColor = Color.FromArgb(60, 60, 70)
        };

        var lblAR = MakeLabel("Auto-Reply Settings", 12, 212, 300, 20);
        lblAR.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);

        chkAutoReply = new CheckBox
        {
            Location  = new Point(12, 240),
            Size      = new Size(250, 22),
            Text      = "Enable auto-reply to friends",
            ForeColor = Color.FromArgb(210, 210, 210),
            BackColor = Color.Transparent
        };

        var lblARMsg = MakeLabel("Auto-reply message:", 12, 272, 150, 20);
        txtAutoReply = MakeTextBox(170, 270, 500);

        var sep2 = new Label
        {
            Location  = new Point(12, 312),
            Size      = new Size(780, 1),
            BackColor = Color.FromArgb(60, 60, 70)
        };

        btnSaveSettings = MakeDarkButton("Save Settings", 12, 328, 140, 34);
        btnSaveSettings.BackColor = Color.FromArgb(40, 90, 50);
        lblSaved = MakeLabel("", 164, 336, 300, 20, Color.FromArgb(80, 200, 120));

        var note = MakeLabel(
            "After changing username/password, stop and reconnect for changes to take effect.",
            12, 378, 700, 18, Color.FromArgb(120, 120, 120));

        p.Controls.AddRange(new Control[] {
            lbl, lblUser, txtUsername, lblPass, txtPassword,
            lblTokenStatus, btnClearToken,
            sep1, lblAR, chkAutoReply, lblARMsg, txtAutoReply,
            sep2, btnSaveSettings, lblSaved, note
        });

        btnSaveSettings.Click += OnSaveSettings;
        btnClearToken.Click   += OnClearToken;
    }

    /*
     * EVENTS
     */
    private async void OnConnectClick(object? sender, EventArgs e)
    {
        if (_auth != null) return;

        btnConnect.Enabled = false;
        SetStatus("Connecting...", Color.FromArgb(200, 180, 60));

        _cts  = new CancellationTokenSource();
        _auth = new SteamAuth(_config, new GuiAuthenticator(this));

        _auth.OnSystemEvent      += msg => AppendLog(msg);
        _auth.OnChatMessage      += (name, msg) => AppendChat($"[{DateTime.Now:HH:mm:ss}] {name}: {msg}", incoming: true);
        _auth.OnLoggedInElsewhere += () =>
        {
            AppendLog("⚠ Account logged in elsewhere! Auto-reconnecting...");
            SetStatus("⚠ Logged in elsewhere — reconnecting...", Color.FromArgb(220, 160, 50));
        };
        _auth.OnReconnected += () =>
        {
            SetStatus("● Connected", Color.FromArgb(80, 200, 80));
            AppendLog("✓ Reconnected.");
        };

        _auth.StartCallbackPump(_cts.Token);

        if (!await _auth.ConnectAsync(_cts.Token))
        {
            AppendLog("✗ Could not connect to Steam.");
            SetStatus("✗ Connection failed", Color.FromArgb(200, 80, 80));
            _auth = null;
            btnConnect.Enabled = true;
            return;
        }

        if (!await _auth.LoginAsync(_cts.Token))
        {
            AppendLog("✗ Login failed — check username/password in Settings.");
            SetStatus("✗ Login failed", Color.FromArgb(200, 80, 80));
            _auth = null;
            btnConnect.Enabled = true;
            return;
        }

        // Update token status label after successful login
        UpdateTokenStatusLabel();

        SetStatus($"● Connected as {_auth.LoggedInSteamID}", Color.FromArgb(80, 200, 80));
        SetBarRight("Idling");
        btnStop.Enabled    = true;
        btnConnect.Enabled = false;

        var gameIds = LoadGameIds();
        if (gameIds.Count == 0)
        {
            AppendLog("No AppIDs in the list — add some using '+ Add' and reconnect.");
            return;
        }

        _idler = new GameIdler(_auth);
        _idler.OnStatusUpdate += msg => AppendLog(msg);

        _ = Task.Run(async () =>
        {
            await _idler.StartIdling(gameIds, _cts.Token);
            Invoke(() =>
            {
                SetStatus("● Stopped", Color.FromArgb(200, 80, 80));
                btnConnect.Enabled = true;
                btnStop.Enabled    = false;
                SetBarRight("Idle");
                _auth  = null;
                _idler = null;
            });
        });
    }

    private void OnStopClick(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        btnStop.Enabled = false;
        AppendLog("Stopping...");
    }

    private void OnAddGame(object? sender, EventArgs e)
    {
        using var dlg = new Form
        {
            Text = "Add AppID",
            Size = new Size(300, 130),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Color.FromArgb(30, 30, 38),
            ForeColor = Color.FromArgb(210, 210, 210),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var lbl = new Label  { Text = "Steam AppID:", Location = new Point(12, 16), Size = new Size(120, 20), ForeColor = Color.FromArgb(200, 200, 200) };
        var txt = new TextBox { Location = new Point(140, 14), Size = new Size(130, 22), BackColor = Color.FromArgb(40, 40, 50), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        var ok  = new Button  { Text = "Add", Location = new Point(105, 52), Size = new Size(80, 28), BackColor = Color.FromArgb(40, 90, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
        dlg.Controls.AddRange(new Control[] { lbl, txt, ok });
        dlg.AcceptButton = ok;

        if (dlg.ShowDialog(this) == DialogResult.OK && uint.TryParse(txt.Text.Trim(), out uint id) && id > 0)
        {
            if (!lstGames.Items.Contains(id.ToString()))
                lstGames.Items.Add(id.ToString());
            SaveGameIds();
        }
    }

    private void OnRemoveGame(object? sender, EventArgs e)
    {
        if (lstGames.SelectedIndex >= 0)
        {
            lstGames.Items.RemoveAt(lstGames.SelectedIndex);
            SaveGameIds();
        }
    }

    private void OnSendMessage(object? sender, EventArgs e)
    {
        if (_auth == null) { AppendChat("Not connected.", incoming: false); return; }
        if (!ulong.TryParse(txtTo.Text.Trim(), out var sid64))
        {
            AppendChat("Invalid SteamID64 in the To field.", incoming: false);
            return;
        }
        var msg = txtMessage.Text.Trim();
        if (string.IsNullOrEmpty(msg)) return;

        bool ok = _auth.SendMessage(sid64, msg);
        if (ok)
        {
            AppendChat($"[{DateTime.Now:HH:mm:ss}] [You → {sid64}]: {msg}", incoming: false);
            txtMessage.Clear();
        }
        else
        {
            AppendChat("Failed to send — not logged in?", incoming: false);
        }
    }

    private void OnSaveSettings(object? sender, EventArgs e)
    {
        _config.Username         = txtUsername.Text.Trim();
        _config.Password         = txtPassword.Text;
        _config.AutoReplyEnabled = chkAutoReply.Checked;
        _config.AutoReplyMessage = txtAutoReply.Text.Trim();
        _config.Save();

        lblSaved.Text = "✓ Saved!";
        var t = new System.Windows.Forms.Timer { Interval = 2000 };
        t.Tick += (_, _) => { lblSaved.Text = ""; t.Stop(); t.Dispose(); };
        t.Start();
    }

    private void OnClearToken(object? sender, EventArgs e)
    {
        _config.RefreshToken = "";
        _config.Save();
        UpdateTokenStatusLabel();
        AppendLog("Saved session token cleared — you will be asked for Steam Guard on next login.");
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _cts?.Cancel();
    }

    /*
     * HELPERS
     */
    private bool HasSavedToken() => !string.IsNullOrWhiteSpace(_config.RefreshToken);

    private void UpdateTokenStatusLabel()
    {
        if (lblTokenStatus.InvokeRequired) { Invoke(UpdateTokenStatusLabel); return; }
        bool has = HasSavedToken();
        lblTokenStatus.Text      = has
            ? "✓ Saved session token — will log in automatically (no password needed)."
            : "No saved token — will prompt for Steam Guard on next login.";
        lblTokenStatus.ForeColor = has ? Color.FromArgb(80, 200, 120) : Color.FromArgb(160, 160, 160);
        btnClearToken.Enabled    = has;
    }

    private void RefreshFriendList()
    {
        lvFriends.Items.Clear();
        if (_auth == null) { AppendLog("Connect first to see friends."); return; }
        var friends = _auth.GetFriendList();
        foreach (var (id, name, state) in friends)
        {
            var item = new ListViewItem(name);
            item.SubItems.Add(id.ToString());
            item.SubItems.Add(state);
            lvFriends.Items.Add(item);
        }
    }

    private void RefreshGameList()
    {
        lstGames.Items.Clear();
        const string f = "games.txt";
        if (!File.Exists(f)) return;
        foreach (var line in File.ReadAllLines(f))
        {
            var clean = line.Split("//")[0].Trim();
            if (uint.TryParse(clean, out uint id) && id > 0)
                lstGames.Items.Add(id.ToString());
        }
    }

    private List<uint> LoadGameIds()
    {
        var list = new List<uint>();
        foreach (string s in lstGames.Items)
            if (uint.TryParse(s, out uint id)) list.Add(id);
        return list;
    }

    private void SaveGameIds()
    {
        var lines = new List<string> { "// Managed by Steam Idler GUI" };
        foreach (string s in lstGames.Items) lines.Add(s);
        File.WriteAllLines("games.txt", lines);
    }

    private void LoadSettingsIntoUI()
    {
        txtUsername.Text      = _config.Username;
        txtPassword.Text      = _config.Password;
        chkAutoReply.Checked  = _config.AutoReplyEnabled;
        txtAutoReply.Text     = _config.AutoReplyMessage;
    }

    private void SetStatus(string text, Color color)
    {
        if (InvokeRequired) { Invoke(() => SetStatus(text, color)); return; }
        lblStatus.Text      = text;
        lblStatus.ForeColor = color;
        lblBarLeft.Text     = text;
    }

    private void SetBarRight(string text)
    {
        if (InvokeRequired) { Invoke(() => SetBarRight(text)); return; }
        lblBarRight.Text = text;
    }

    private void AppendLog(string msg)
    {
        if (logBox.InvokeRequired) { logBox.Invoke(() => AppendLog(msg)); return; }
        logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        logBox.ScrollToCaret();
    }

    private void AppendChat(string msg, bool incoming)
    {
        if (chatLog.InvokeRequired) { chatLog.Invoke(() => AppendChat(msg, incoming)); return; }
        chatLog.SelectionColor = incoming ? Color.FromArgb(150, 220, 255) : Color.FromArgb(200, 200, 200);
        chatLog.AppendText(msg + "\n");
        chatLog.SelectionColor = chatLog.ForeColor;
        chatLog.ScrollToCaret();
        try { File.AppendAllText("chat_log.txt", msg + Environment.NewLine); } catch { }
    }

    private void OpenChatLog()
    {
        const string f = "chat_log.txt";
        if (!File.Exists(f)) { MessageBox.Show("No chat log yet.", "Info"); return; }
        System.Diagnostics.Process.Start("notepad.exe", f);
    }

    /*
     * Factory Helpers
     */
    private static Label MakeLabel(string text, int x, int y, int w, int h, Color? color = null)
    {
        return new Label
        {
            Text      = text,
            Location  = new Point(x, y),
            Size      = new Size(w, h),
            ForeColor = color ?? Color.FromArgb(190, 190, 190),
            BackColor = Color.Transparent
        };
    }

    private static Button MakeDarkButton(string text, int x, int y, int w, int h, bool enabled = true)
    {
        var b = new Button
        {
            Text      = text,
            Location  = new Point(x, y),
            Size      = new Size(w, h),
            BackColor = Color.FromArgb(50, 50, 62),
            ForeColor = Color.FromArgb(210, 210, 210),
            FlatStyle = FlatStyle.Flat,
            Enabled   = enabled
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 85);
        return b;
    }

    private static TextBox MakeTextBox(int x, int y, int w)
    {
        return new TextBox
        {
            Location    = new Point(x, y),
            Size        = new Size(w, 24),
            BackColor   = Color.FromArgb(32, 32, 40),
            ForeColor   = Color.FromArgb(210, 210, 210),
            BorderStyle = BorderStyle.FixedSingle
        };
    }
}