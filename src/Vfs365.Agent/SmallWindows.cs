using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Vfs365.Agent;

/// <summary>The files waiting to upload, as the window shows them: at most <see cref="StatusFile.MaxWaitingFiles"/>, and how many wait in all.</summary>
public sealed record WaitingSnapshot(IReadOnlyList<WaitingFile> Files, int Total);

/// <summary>
/// The tray's small windows (waiting files, an explanation), each on a UI thread of its own like <see cref="DetailsWindows"/>: an
/// exception in one closes that window only, never the agent. The waiting-files window is one at a time: asked again, it comes forward.
/// </summary>
public sealed class SmallWindows(Action<string> open, Action<string> log)
{
    readonly Lock gate = new();
    WaitingFilesWindow? waiting;
    bool opening;

    /// <summary>The waiting files (refreshed while it is open); <paramref name="folderOf"/> gives the Windows folder of a file's volume path.</summary>
    public void ShowWaiting(Func<WaitingSnapshot> files, Func<string, string?> folderOf)
    {
        lock (gate)
        {
            if (opening)
            {
                waiting?.ComeForward();
                return;
            }
            opening = true;
        }
        Start("VFS365 waiting files", () =>
        {
            var window = new WaitingFilesWindow(files, folderOf, open);
            lock (gate)
            {
                waiting = window;
            }
            return window;
        }, () =>
        {
            lock (gate)
            {
                (waiting, opening) = (null, false);
            }
        });
    }

    /// <summary>A read-only text, such as an explanation.</summary>
    public void ShowText(string title, string text) =>
        Start("VFS365 text", () => new TextWindow(title, text), () => { });

    void Start(string name, Func<SmallWindow> make, Action ended)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var window = make();
                var owner = Thread.CurrentThread;
                ThreadExceptionEventHandler guard = (_, e) =>
                {
                    if (Thread.CurrentThread != owner)
                    {
                        return;
                    }
                    log($"{name}: closed after an unexpected error: {e.Exception.GetType().Name}: {e.Exception.Message}");
                    window.Abandon();
                };
                Application.ThreadException += guard;
                try
                {
                    Application.Run(window);
                }
                finally
                {
                    Application.ThreadException -= guard;
                    window.Dispose();
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log($"{name}: {e.Message}");
            }
            finally
            {
                ended();
            }
        })
        { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}

/// <summary>A window in the look of <see cref="DetailsWindow"/>: a heading, content, and a grey footer with the buttons on the right, Close last.</summary>
abstract class SmallWindow : Form
{
    protected readonly Label Heading = new() { AutoSize = true, Dock = DockStyle.Fill };
    protected readonly Button CloseButton = Look.Secondary(UiText.Get("WaitList.Close"));

    protected SmallWindow(string title, string heading)
    {
        Text = title;
        Font = Look.Body;
        ForeColor = Look.Text;
        BackColor = Look.Window;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(this.Scaled(720), this.Scaled(420));
        MinimumSize = new Size(this.Scaled(480), this.Scaled(280));
        ShowInTaskbar = true;
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
        }
        Heading.Text = heading;
        Heading.Padding = this.Scaled(20, 16, 20, 10);
    }

    /// <summary>Lays out the heading, <paramref name="content"/> and the footer with <paramref name="buttons"/> (then Close).</summary>
    protected void Arrange(Control content, params Button[] buttons)
    {
        content.Dock = DockStyle.Fill;
        content.Margin = this.Scaled(20, 0, 20, 12);
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Dock = DockStyle.Right, Margin = Padding.Empty, BackColor = Look.Footer };
        row.Controls.Add(CloseButton);
        row.Controls.AddRange(buttons.Reverse().ToArray());
        foreach (Control button in row.Controls)
        {
            button.Margin = this.Scaled(8, 0, 0, 0);
        }
        var footer = new Panel { AutoSize = true, Dock = DockStyle.Fill, BackColor = Look.Footer, Padding = this.Scaled(20, 14, 24, 14) };
        footer.Controls.Add(row);
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Look.Line);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Heading, 0, 0);
        layout.Controls.Add(content, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        CancelButton = CloseButton;
        CloseButton.Click += (_, _) => Close();
    }

    /// <summary>From any thread: the window to the front.</summary>
    public void ComeForward()
    {
        if (IsDisposed)
        {
            return;
        }
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(ComeForward);
            }
            catch (InvalidOperationException)
            {
                // The window is gone already
            }
            return;
        }
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Activate();
    }

    /// <summary>Closes the window; when even that fails, ends its thread.</summary>
    internal void Abandon()
    {
        try
        {
            Close();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Application.ExitThread();
        }
    }
}

/// <summary>Files waiting to upload: name, folder, why in plain words and since when; refreshed every few seconds while it is open.</summary>
sealed class WaitingFilesWindow : SmallWindow
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    readonly Func<WaitingSnapshot> files;
    readonly Func<string, string?> folderOf;
    readonly Action<string> open;
    readonly ListView list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Look.Window };
    readonly Label detail = new() { AutoSize = true, Dock = DockStyle.Bottom, ForeColor = Look.Subtle, Padding = new Padding(0, 6, 0, 0) };
    readonly Button openFolder = Look.Primary(UiText.Get("WaitList.OpenFolder"));
    readonly System.Windows.Forms.Timer refresh = new() { Interval = (int)RefreshEvery.TotalMilliseconds };
    string[] shownPaths = [];

    public WaitingFilesWindow(Func<WaitingSnapshot> files, Func<string, string?> folderOf, Action<string> open)
        : base(UiText.Get("WaitList.Window"), UiText.Get("WaitList.Heading"))
    {
        (this.files, this.folderOf, this.open) = (files, folderOf, open);
        list.Columns.Add(UiText.Get("WaitList.File"), this.Scaled(190));
        list.Columns.Add(UiText.Get("WaitList.Folder"), this.Scaled(170));
        list.Columns.Add(UiText.Get("WaitList.Why"), this.Scaled(220));
        list.Columns.Add(UiText.Get("WaitList.Since"), this.Scaled(100));
        var content = new Panel();
        content.Controls.Add(list);
        content.Controls.Add(detail);
        list.Dock = DockStyle.Fill;
        Arrange(content, openFolder);
        AcceptButton = openFolder;
        list.SelectedIndexChanged += (_, _) => ShowSelection(files());
        list.DoubleClick += (_, _) => OpenSelectedFolder();
        openFolder.Click += (_, _) => OpenSelectedFolder();
        refresh.Tick += (_, _) => Reload();
        FormClosed += (_, _) => refresh.Stop();
        Reload();
        refresh.Start();
    }

    /// <summary>The columns of a file's row: name, folder (volume path), the cause in plain words, and since when (the time today, else the date).</summary>
    internal static string[] RowOf(WaitingFile file, DateTimeOffset now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var cut = file.Path.LastIndexOf('\\');
        var since = file.Since.ToLocalTime();
        return
        [
            cut >= 0 ? file.Path[(cut + 1)..] : file.Path,
            cut > 0 ? file.Path[..cut] : @"\",
            PlainCause.Text(PlainCause.Of(file.Cause), culture, upload: true),
            since.Date == now.ToLocalTime().Date ? since.ToString("t", culture) : since.ToString("g", culture),
        ];
    }

    /// <summary>The Windows folder of a file on the drive (<paramref name="root"/> "M:" or \\VFS365\user, <paramref name="volumePath"/> \Sites\Sales\a.docx).</summary>
    internal static string FolderOf(string root, string volumePath)
    {
        var cut = volumePath.LastIndexOf('\\');
        return cut > 0 ? root.TrimEnd('\\') + volumePath[..cut] : root.TrimEnd('\\') + @"\";
    }

    /// <summary>Rows as shown, for tests.</summary>
    internal IReadOnlyList<string[]> Rows => list.Items.Cast<ListViewItem>().Select(i => i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).ToArray()).ToList();

    internal string HeadingText => Heading.Text;

    internal string DetailText => detail.Text;

    internal bool CanOpenFolder => openFolder.Enabled;

    internal void SelectRow(int index) => list.Items[index].Selected = true;

    /// <summary>Reads the files again: rows added, removed or kept (the selection stays on its file).</summary>
    internal void Reload()
    {
        var snapshot = files();
        var now = DateTimeOffset.UtcNow;
        var paths = snapshot.Files.Select(f => f.Path).ToArray();
        if (!paths.SequenceEqual(shownPaths, StringComparer.Ordinal))
        {
            var keep = (list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as string : null) ?? paths.FirstOrDefault();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var file in snapshot.Files)
            {
                list.Items.Add(new ListViewItem(RowOf(file, now)) { Tag = file.Path, Selected = file.Path == keep });
            }
            list.EndUpdate();
            shownPaths = paths;
        }
        Heading.Text = snapshot.Files.Count == 0 ? UiText.Get("WaitList.None") : UiText.Get("WaitList.Heading");
        ShowSelection(snapshot);
    }

    /// <summary>The full cause of the selected file (the column may cut it), and how many files there are beyond the list.</summary>
    void ShowSelection(WaitingSnapshot snapshot)
    {
        var path = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as string : null;
        openFolder.Enabled = path is not null && folderOf(path) is not null;
        var more = snapshot.Total > snapshot.Files.Count ? UiText.Get("WaitList.More", snapshot.Files.Count, snapshot.Total) : "";
        detail.Text = string.Join("  ", new[] { list.SelectedItems.Count > 0 ? list.SelectedItems[0].SubItems[2].Text : "", more }.Where(t => t.Length > 0));
    }

    internal void OpenSelectedFolder()
    {
        if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is string path && folderOf(path) is { } folder)
        {
            open(folder);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refresh.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>A read-only text with Close: the explanation of a missing site.</summary>
sealed class TextWindow : SmallWindow
{
    readonly TextBox box = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BorderStyle = BorderStyle.FixedSingle,
        BackColor = Look.Window, ForeColor = Look.Text, Font = new Font("Consolas", 9f),
    };

    public TextWindow(string title, string text) : base(title, title)
    {
        box.Text = text;
        Arrange(box);
        Shown += (_, _) =>
        {
            box.Select(0, 0);
            ComeForward();
        };
    }

    internal string Content => box.Text;
}
