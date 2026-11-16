using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Vfs365.Core;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>An item to show. <see cref="Missing"/>: required columns a save left empty, shown in bold.</summary>
public sealed record DetailsRequest(string DriveId, string ItemId, string Name, string? Location, bool ReadOnly, IReadOnlyCollection<string>? Missing = null);

/// <summary>Details windows: one per item (asked again, it comes to the front), each on a UI thread of its own.</summary>
/// <param name="saved">A save went through with nothing required left empty.</param>
public sealed class DetailsWindows(ColumnsApi api, Action<string> open, Action<string> log, Action<DetailsRequest>? saved = null)
{
    readonly ConcurrentDictionary<string, DetailsWindow> shown = new(StringComparer.Ordinal);

    /// <summary>Once, before any window of the process: sharp text on scaled screens.</summary>
    public static void PrepareProcess()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }
        catch (InvalidOperationException)
        {
            // A window was made already: windows still work, at the process's scaling
        }
        try
        {
            // Never Windows Forms' own dialog, whose Quit ends the agent with exit code 0 (no restart, no cleanup); see Guard
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>On the window's own thread, before its loop: an exception in one of its handlers is logged and closes that window only.</summary>
    internal static void Guard(DetailsWindow window, Action<string> log) =>
        Application.ThreadException += (_, e) =>
        {
            log($"Details window: closed after an unexpected error: {e.Exception.GetType().Name}: {e.Exception.Message}");
            window.Abandon();
        };

    public void Show(DetailsRequest request)
    {
        var key = $"{request.DriveId}|{request.ItemId}";
        if (shown.TryGetValue(key, out var existing))
        {
            existing.ComeForward();
            return;
        }
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new DetailsWindow(api, request, open, log, saved);
                if (!shown.TryAdd(key, window))
                {
                    return;
                }
                try
                {
                    Guard(window, log);
                    Application.Run(window);
                }
                finally
                {
                    shown.TryRemove(key, out _);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log($"Details window: {e.Message}");
            }
        })
        { IsBackground = true, Name = "VFS365 details" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}

/// <summary>
/// The windows' look, after Windows 11's own dialogs: Segoe UI, white content over a grey footer, the user's accent colour for the
/// main button and links. In high contrast the system colours instead. Sizes are at 96 DPI; <see cref="Scaled"/> turns them into pixels.
/// </summary>
static class Look
{
    static readonly bool HighContrast = SystemInformation.HighContrast;

    public static Color Accent { get; } = HighContrast ? SystemColors.Highlight : ReadAccent();
    public static Color Window => HighContrast ? SystemColors.Window : Color.White;
    public static Color Text => HighContrast ? SystemColors.WindowText : Color.FromArgb(26, 26, 26);
    public static Color Subtle => HighContrast ? SystemColors.GrayText : Color.FromArgb(96, 96, 96);
    public static Color Line => HighContrast ? SystemColors.WindowFrame : Color.FromArgb(229, 229, 229);
    public static Color Footer => HighContrast ? SystemColors.Control : Color.FromArgb(243, 243, 243);
    public static Color Card => HighContrast ? SystemColors.Window : Color.FromArgb(249, 249, 249);
    public static Color Error => HighContrast ? SystemColors.WindowText : Color.FromArgb(196, 43, 28);
    public static Color Warning => HighContrast ? SystemColors.WindowText : Color.FromArgb(157, 93, 0);
    public static Color Chip => HighContrast ? SystemColors.Control : Blend(Accent, Color.White, 0.88);

    static readonly string Family = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    static readonly string Strong = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text Semibold") ? "Segoe UI Variable Text Semibold" : "Segoe UI Semibold";

    public static Font Body { get; } = new(Family, 9f);
    public static Font Label { get; } = new(Strong, 9f);
    public static Font Small { get; } = new(Family, 8.25f);
    public static Font Section { get; } = new(Strong, 10.5f);
    public static Font Title { get; } = new(Strong, 14f);

    public static int Scaled(this Control control, int pixels) => control.LogicalToDeviceUnits(pixels);

    public static Padding Scaled(this Control control, int left, int top, int right, int bottom) =>
        new(control.Scaled(left), control.Scaled(top), control.Scaled(right), control.Scaled(bottom));

    /// <summary>Windows' accent colour (DWM, ABGR), when it is dark enough for white text on it; else Windows' default blue.</summary>
    static Color ReadAccent()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
            {
                var color = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                if (color.GetBrightness() < 0.55f)
                {
                    return color;
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }
        return Color.FromArgb(0, 95, 184);
    }

    public static Color Blend(Color color, Color with, double amount) => Color.FromArgb(
        (int)(color.R + (with.R - color.R) * amount), (int)(color.G + (with.G - color.G) * amount), (int)(color.B + (with.B - color.B) * amount));

    /// <summary>The filled button: accent colour, white text; grey when it can't be pressed.</summary>
    public static Button Primary(string text)
    {
        var button = Plain(text);
        button.Font = Label;
        button.FlatAppearance.BorderSize = 0;
        void Paint()
        {
            button.BackColor = button.Enabled ? Accent : Blend(Line, Footer, 0.2);
            button.ForeColor = button.Enabled ? Color.White : Subtle;
            button.FlatAppearance.MouseOverBackColor = Blend(Accent, Color.Black, 0.1);
            button.FlatAppearance.MouseDownBackColor = Blend(Accent, Color.Black, 0.2);
        }
        button.EnabledChanged += (_, _) => Paint();
        Paint();
        return button;
    }

    public static Button Secondary(string text)
    {
        var button = Plain(text);
        button.BackColor = Window;
        button.ForeColor = Text;
        button.FlatAppearance.BorderColor = Color.FromArgb(208, 208, 208);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(246, 246, 246);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(235, 235, 235);
        return button;
    }

    /// <summary>A button that reads as a link: accent text, no frame.</summary>
    public static Button Link(string text, Color background)
    {
        var button = Plain(text);
        button.BackColor = background;
        button.ForeColor = Accent;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Blend(background, Color.Black, 0.04);
        button.FlatAppearance.MouseDownBackColor = Blend(background, Color.Black, 0.08);
        button.Cursor = Cursors.Hand;
        button.Padding = new Padding(2, 0, 2, 0);
        return button;
    }

    static Button Plain(string text) => new()
    {
        Text = text,
        FlatStyle = HighContrast ? FlatStyle.System : FlatStyle.Flat,
        Font = Body,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowOnly,
        MinimumSize = new Size(88, 32),
        Padding = new Padding(10, 0, 10, 0),
        UseVisualStyleBackColor = false,
    };

    /// <summary>The 32-pixel icon Explorer shows for a file of this name, or a folder; from the extension only, the file isn't touched.</summary>
    public static Bitmap? IconOf(string name, bool folder)
    {
        var info = new ShFileInfo();
        var flags = 0x100u | 0x0u | 0x10u; // SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES
        var found = SHGetFileInfo(folder ? "folder" : name, folder ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        if (found == IntPtr.Zero || info.Icon == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(info.Icon);
            return icon.ToBitmap();
        }
        finally
        {
            DestroyIcon(info.Icon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);
}

/// <summary>
/// An item's details and SharePoint columns, editable where the library allows it. Values are checked here before a save (SharePoint
/// takes a choice that isn't on the list, and Graph lets a required column go empty); what SharePoint still refuses shows at its column.
/// </summary>
sealed class DetailsWindow : Form
{
    readonly ColumnsApi api;
    readonly DetailsRequest request;
    readonly Action<string> open;
    readonly Action<string> log;
    readonly Action<DetailsRequest>? saved;
    readonly CancellationTokenSource closing = new();
    readonly TableLayoutPanel content = new() { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top };
    readonly Panel body = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly Label title = new() { AutoSize = true, AutoEllipsis = true };
    readonly Label subtitle = new() { AutoSize = true, AutoEllipsis = true };
    readonly PictureBox picture = new() { SizeMode = PictureBoxSizeMode.Zoom };
    readonly Label message = new() { AutoSize = true, Dock = DockStyle.Fill };
    readonly Button save = Look.Primary(UiText.Get("Details.Save"));
    readonly Button close = Look.Secondary(UiText.Get("Details.Close"));
    readonly Button browser = Look.Link(UiText.Get("Details.Browser"), Look.Footer);
    readonly Button versions = Look.Link(UiText.Get("Details.VersionHistory"), Look.Footer);
    readonly List<Editor> editors = [];
    readonly List<Control> columnRows = [];
    readonly Dictionary<Editor, Label> problems = [];
    readonly Dictionary<string, IReadOnlyList<Term>> termCache = new(StringComparer.OrdinalIgnoreCase);
    ItemColumns? item;
    ComboBox? contentType;
    string? contentTypeId;
    bool done, reported, switching;

    public DetailsWindow(ColumnsApi api, DetailsRequest request, Action<string> open, Action<string> log, Action<DetailsRequest>? saved = null)
    {
        (this.api, this.request, this.open, this.log, this.saved) = (api, request, open, log, saved);
        AskClose = question => MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        Text = UiText.Get("Details.Window", request.Name);
        Font = Look.Body;
        ForeColor = Look.Text;
        BackColor = Look.Window;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(this.Scaled(600), this.Scaled(680));
        MinimumSize = new Size(this.Scaled(460), this.Scaled(380));
        ShowInTaskbar = true;
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
        }

        // Header: the item's icon, name and where it is
        title.Font = Look.Title;
        title.Text = request.Name;
        title.Margin = this.Scaled(0, 0, 0, 2);
        subtitle.ForeColor = Look.Subtle;
        subtitle.Text = request.Location ?? "";
        picture.Size = new Size(this.Scaled(36), this.Scaled(36));
        picture.Margin = this.Scaled(0, 2, 14, 0);
        picture.Image = Look.IconOf(request.Name, folder: !Path.HasExtension(request.Name));
        var names = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        names.Controls.AddRange([title, subtitle]);
        var header = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = this.Scaled(24, 20, 24, 14) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(picture, 0, 0);
        header.Controls.Add(names, 1, 0);

        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.Padding = this.Scaled(24, 0, 24, 16);
        body.Controls.Add(content);
        message.Padding = this.Scaled(24, 8, 24, 8);

        // Footer: links on the left, the buttons on the right, on Windows' grey
        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Left, Margin = Padding.Empty, BackColor = Look.Footer };
        links.Controls.AddRange([browser, versions]);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Dock = DockStyle.Right, Margin = Padding.Empty, BackColor = Look.Footer };
        buttons.Controls.AddRange([close, save]);
        save.Margin = close.Margin = this.Scaled(8, 0, 0, 0);
        var footer = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, BackColor = Look.Footer, Padding = this.Scaled(20, 14, 24, 14) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(links, 0, 0);
        footer.Controls.Add(buttons, 1, 0);
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Look.Line);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(body, 0, 1);
        layout.Controls.Add(message, 0, 2);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);

        save.Enabled = browser.Enabled = false;
        versions.Visible = false;
        AcceptButton = save;
        CancelButton = close;
        save.Click += async (_, _) => await SaveAsync();
        close.Click += (_, _) => Close();
        browser.Click += (_, _) => OpenForm();
        versions.Click += (_, _) =>
        {
            if (item is not null && Uri.TryCreate(item.WebUrl, UriKind.Absolute, out _))
            {
                open(ItemUrls.VersionHistory(item.WebUrl, item.ListId, item.ListItemId));
            }
        };
        Say(UiText.Get("Details.Loading"));
        Shown += async (_, _) =>
        {
            ComeForward();
            await LoadAsync();
        };
        // Windows ending the session, or the agent stopping, doesn't wait for an answer
        FormClosing += (_, e) => e.Cancel = e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing or CloseReason.ApplicationExitCall) && !ConfirmClose();
        FormClosed += (_, _) =>
        {
            closing.Cancel();
            if (item is not null && !EmptyRequired().Any())
            {
                ReportFilled();
            }
        };
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
            BeginInvoke(ComeForward);
            return;
        }
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Activate();
    }

    /// <summary>Closes without asking, pickers included; when even that fails, ends the window's thread.</summary>
    internal void Abandon()
    {
        try
        {
            done = true;
            foreach (var owned in OwnedForms)
            {
                owned.Close();
            }
            Close();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Application.ExitThread();
        }
    }

    /// <summary>The line above the footer: progress, a hint or why something failed (<paramref name="problem"/>: in red).</summary>
    void Say(string text, bool problem = false)
    {
        message.Text = text;
        message.ForeColor = problem ? Look.Error : Look.Subtle;
        message.Visible = text.Length > 0;
    }

    async Task LoadAsync()
    {
        content.SuspendLayout();
        content.Controls.Clear();
        content.RowStyles.Clear();
        content.RowCount = 0;
        editors.Clear();
        columnRows.Clear();
        problems.Clear();
        content.ResumeLayout();
        save.Enabled = false;
        try
        {
            item = await Off(() => api.GetAsync(request.DriveId, request.ItemId, closing.Token), closing.Token);
        }
        catch (Exception e) when (e is not OutOfMemoryException && !closing.IsCancellationRequested)
        {
            log($"Details window: {request.Name}: {e.Message}");
            Say(e is NoListItemException ? UiText.Get("Details.NoListItem") : UiText.Get("Details.LoadFailed", PlainCause.Text(PlainCause.Of(e.Message))), e is not NoListItemException);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        contentTypeId = item.ContentTypeId;
        browser.Enabled = true;
        versions.Visible = versions.Enabled = !item.Details.IsFolder;
        if (item.Details.IsFolder != !Path.HasExtension(request.Name))
        {
            picture.Image = Look.IconOf(request.Name, item.Details.IsFolder);
        }
        if (request.Location is null && item.Details.Library is { } library)
        {
            subtitle.Text = library;
        }
        content.SuspendLayout();
        ShowDetails(item.Details);
        AddSection(UiText.Get("Details.Columns"));
        if (item.ContentTypes.Count > 1)
        {
            contentType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = !request.ReadOnly };
            foreach (var type in item.ContentTypes)
            {
                contentType.Items.Add(new Choice(type.Name, type.Id));
            }
            contentType.SelectedItem = contentType.Items.Cast<Choice>().FirstOrDefault(c => ((string)c.Value).Equals(item.ContentTypeId, StringComparison.OrdinalIgnoreCase));
            contentType.SelectedIndexChanged += async (_, _) => await ContentTypeChangedAsync();
            AddField(UiText.Get("Details.ContentType"), contentType, null, false);
        }
        ShowColumns(item.Columns, name => item.ValueOf(name));
        content.ResumeLayout();
        Relayout();
        Say(request.ReadOnly ? UiText.Get("Details.ReadOnly") : request.Missing is { Count: > 0 } ? UiText.Get("Details.Missing") : "");
        save.Visible = !request.ReadOnly;
    }

    /// <summary>The facts about the item in a light card, label and value side by side.</summary>
    void ShowDetails(ItemDetails details)
    {
        string When(DateTimeOffset time, string? by) => time == DateTimeOffset.MinValue ? by ?? ""
            : by is null ? time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : UiText.Get("Details.By", time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), by);
        var card = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, BackColor = Look.Card, Padding = this.Scaled(14, 10, 14, 10), Margin = this.Scaled(0, 0, 0, 18) };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Look.Line);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        void Row(string caption, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            var row = card.RowCount++;
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Look.Subtle, Margin = this.Scaled(0, 4, 18, 4) }, 0, row);
            card.Controls.Add(new Label { Text = value, AutoSize = true, ForeColor = Look.Text, Margin = this.Scaled(0, 4, 0, 4) }, 1, row);
        }
        if (request.Location is not null)
        {
            Row(UiText.Get("Details.Library"), details.Library);
        }
        if (!details.IsFolder)
        {
            Row(UiText.Get("Details.Size"), Sizes.Text(details.Size));
        }
        Row(UiText.Get("Details.Created"), When(details.Created, details.CreatedBy));
        Row(UiText.Get("Details.Modified"), When(details.Modified, details.ModifiedBy));
        Row(UiText.Get("Details.Versions"), details.Versions?.ToString(CultureInfo.CurrentCulture));
        Row(UiText.Get("Details.CheckedOut"), details.CheckedOutTo);
        Row(UiText.Get("Details.Retention"), details.RetentionLabel);
        Row(UiText.Get("Details.Sensitivity"), details.SensitivityLabel);
        Add(card);
    }

    /// <summary>The columns' fields; read-only kinds when they have a value or are required (then filled in the browser).</summary>
    void ShowColumns(IReadOnlyList<Column> columns, Func<string, ColumnValue> valueOf)
    {
        var shown = columns.Where(c => c.Kind != ColumnKind.ReadOnly || c.Required || !string.IsNullOrEmpty(valueOf(c.Name).Display)).ToList();
        if (shown.Count == 0)
        {
            var none = new Label { Text = UiText.Get("Details.NoColumns"), AutoSize = true, ForeColor = Look.Subtle, Margin = this.Scaled(0, 0, 0, 12) };
            Add(none);
            columnRows.Add(none);
        }
        Control? first = null;
        foreach (var column in shown)
        {
            var editor = Editor.For(column, valueOf(column.Name), item!.ValueOf(column.Name), this);
            editor.Control.Enabled = column.Kind == ColumnKind.ReadOnly || !request.ReadOnly;
            editor.Changed += () =>
            {
                save.Enabled = !request.ReadOnly;
                ShowProblem(editor, null);
            };
            editors.Add(editor);
            var missing = request.Missing?.Contains(column.Title, StringComparer.OrdinalIgnoreCase) == true || request.Missing?.Contains(column.Name, StringComparer.OrdinalIgnoreCase) == true;
            problems[editor] = AddField(column.Title + (column.Required ? " *" : ""), editor.Control, column.Description, missing);
            columnRows.Add(editor.Control.Parent!);
            if (missing)
            {
                first ??= editor.Control;
            }
        }
        if (contentTypeId is not null && !contentTypeId.Equals(item!.ContentTypeId, StringComparison.OrdinalIgnoreCase))
        {
            save.Enabled = !request.ReadOnly;
        }
        first?.Focus();
    }

    async Task ContentTypeChangedAsync()
    {
        if (switching || item is null || contentType?.SelectedItem is not Choice { Value: string id } || id.Equals(contentTypeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var kept = editors.ToDictionary(e => e.Column.Name, e => e.Value, StringComparer.OrdinalIgnoreCase);
        Say(UiText.Get("Details.Loading"));
        IReadOnlyList<Column> columns;
        try
        {
            columns = await Off(() => api.ColumnsOfAsync(item.WebUrl, item.ListId, id, closing.Token), closing.Token);
        }
        catch (Exception e) when (e is not OutOfMemoryException && !closing.IsCancellationRequested)
        {
            Say(UiText.Get("Details.LoadFailed", PlainCause.Text(PlainCause.Of(e.Message))), true);
            switching = true; // the list shows the type that is in force again
            contentType.SelectedItem = contentType.Items.Cast<Choice>().FirstOrDefault(c => ((string)c.Value).Equals(contentTypeId, StringComparison.OrdinalIgnoreCase));
            switching = false;
            return;
        }
        contentTypeId = id;
        content.SuspendLayout();
        foreach (var row in columnRows)
        {
            content.Controls.Remove(row);
        }
        columnRows.Clear();
        editors.Clear();
        problems.Clear();
        ShowColumns(columns, name => kept.TryGetValue(name, out var value) ? value : item.ValueOf(name));
        content.ResumeLayout();
        Relayout();
        Say("");
        save.Enabled = !request.ReadOnly;
    }

    async Task SaveAsync()
    {
        if (item is null || request.ReadOnly)
        {
            return;
        }
        var invalid = false;
        foreach (var editor in editors)
        {
            var problem = editor.Problem();
            ShowProblem(editor, problem);
            invalid |= problem is not null;
        }
        if (invalid)
        {
            Say(UiText.Get("Details.Problems"), true);
            return;
        }
        var changes = editors.Where(e => e.Column.Kind != ColumnKind.ReadOnly && !e.Value.SameAs(e.Original, e.Column.Kind)).Select(e => (e.Column, e.Value)).ToList();
        if (changes.Count == 0 && contentTypeId!.Equals(item.ContentTypeId, StringComparison.OrdinalIgnoreCase))
        {
            done = !EmptyRequired().Any();
            Close();
            return;
        }
        save.Enabled = false;
        Say(UiText.Get("Details.Saving"));
        SaveProblems refused;
        try
        {
            refused = await Off(() => api.SaveAsync(item, contentTypeId!, changes, closing.Token), closing.Token);
        }
        catch (Exception e) when (e is not OutOfMemoryException && !closing.IsCancellationRequested)
        {
            log($"Details window: saving {request.Name}: {e.Message}");
            Say(UiText.Get("Details.Failed", PlainCause.Text(PlainCause.Of(e.Message))), true);
            save.Enabled = true;
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (refused.None)
        {
            log($"Details window: saved {changes.Count} column(s) of {request.Name}");
            done = true;
            if (!EmptyRequired().Any())
            {
                ReportFilled();
            }
            Close();
            return;
        }
        if (refused.ETag is { } now)
        {
            // Part went in: the next save starts from the item as it is now, not from a change "elsewhere" that was this one
            item = item with { ETag = now, ContentTypeId = refused.ContentTypeSaved ? contentTypeId! : item.ContentTypeId };
        }
        if (refused.Item == "changed")
        {
            if (MessageBox.Show(this, UiText.Get("Details.Changed"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                await LoadAsync();
            }
            else
            {
                Say("");
                save.Enabled = true;
            }
            return;
        }
        foreach (var (name, problem) in refused.Columns)
        {
            if (editors.FirstOrDefault(e => e.Column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } editor)
            {
                ShowProblem(editor, problem);
            }
        }
        log($"Details window: saving {request.Name}: {refused.Item ?? string.Join("; ", refused.Columns.Select(p => $"{p.Key}: {p.Value}"))}");
        Say(refused.Item is { } whole ? UiText.Get("Details.Failed", whole) : UiText.Get("Details.Problems"), true);
        save.Enabled = true;
    }

    /// <summary>
    /// Lays the fields out again once their handles exist: a drop-down knows its height only then (at 175% the rows were measured too
    /// low and cut it off). Also after a move to a screen with another scale.
    /// </summary>
    void Relayout()
    {
        BeginInvoke(() =>
        {
            foreach (var combo in content.Controls.Cast<Control>().SelectMany(Descendants).OfType<ComboBox>())
            {
                combo.MinimumSize = new Size(0, combo.Height);
            }
            content.PerformLayout();
            body.PerformLayout();
        });
    }

    static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(Descendants).Prepend(control);

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (IsHandleCreated)
        {
            foreach (var combo in content.Controls.Cast<Control>().SelectMany(Descendants).OfType<ComboBox>())
            {
                combo.MinimumSize = Size.Empty;
            }
            Relayout();
        }
    }

    void ShowProblem(Editor editor, string? problem)
    {
        if (problems.TryGetValue(editor, out var label))
        {
            label.Text = problem ?? "";
            label.Visible = problem is not null;
        }
    }

    /// <summary>
    /// Closing with a required column still empty asks first: the library needs it (a save would refuse it too), and the agent reminds
    /// the user later. A read-only window, a saved one and one still loading close at once.
    /// </summary>
    bool ConfirmClose()
    {
        if (done || request.ReadOnly || item is null)
        {
            return true;
        }
        var empty = EmptyRequired().Select(e => e.Column.Title).ToList();
        if (empty.Count == 0)
        {
            return true;
        }
        return AskClose(UiText.Get("Details.CloseEmpty", string.Join(", ", empty)));
    }

    /// <summary>Required columns empty as the window shows them, those changed only in the browser included.</summary>
    IEnumerable<Editor> EmptyRequired() => editors.Where(e => e.Column.Required && e.Value.IsEmpty(e.Column.Kind));

    /// <summary>Nothing required is left empty: the file leaves the reminders (once per window).</summary>
    void ReportFilled()
    {
        if (!reported)
        {
            reported = true;
            saved?.Invoke(request);
        }
    }

    /// <summary>Asks whether to close anyway; Yes is true. Replaceable for tests.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, bool> AskClose { get; set; }

    void OpenForm()
    {
        if (item is not null)
        {
            open(ItemUrls.EditForm(item.WebUrl, item.ListId, item.ListItemId));
        }
    }

    void Add(Control control)
    {
        var row = content.RowCount++;
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(control, 0, row);
    }

    void AddSection(string caption)
    {
        Add(new Label { Text = caption, AutoSize = true, Font = Look.Section, ForeColor = Look.Text, Margin = this.Scaled(0, 2, 0, 10) });
    }

    /// <summary>A field: its name above the control (bold and in amber when the library is waiting for it), a description, and a line for a problem. Returns that line.</summary>
    Label AddField(string caption, Control control, string? description, bool missing)
    {
        var block = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = this.Scaled(0, 0, 0, 14) };
        block.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var label = new Label { Text = caption, AutoSize = true, Font = missing ? new Font(Look.Label, FontStyle.Bold) : Look.Label, ForeColor = missing ? Look.Warning : Look.Text, Margin = this.Scaled(0, 0, 0, 4) };
        block.Controls.Add(label, 0, 0);
        var row = 1;
        if (description is not null)
        {
            block.Controls.Add(new Label { Text = description, AutoSize = true, Font = Look.Small, ForeColor = Look.Subtle, Margin = this.Scaled(0, 0, 0, 4) }, 0, row++);
        }
        control.Margin = Padding.Empty;
        control.Anchor = AnchorStyles.Top | AnchorStyles.Left | (control is CheckBox ? AnchorStyles.None : AnchorStyles.Right);
        block.Controls.Add(control, 0, row++);
        var problem = new Label { AutoSize = true, Font = Look.Small, ForeColor = Look.Error, Visible = false, Margin = this.Scaled(0, 4, 0, 0) };
        block.Controls.Add(problem, 0, row);
        Add(block);
        return problem;
    }

    /// <summary>
    /// Runs a call to Microsoft 365 off this window's thread. Awaited here, the call's own continuations would be posted to this
    /// thread and be lost when the window closes meanwhile, leaving what the call shares with others (a token request) unfinished.
    /// </summary>
    static Task<T> Off<T>(Func<Task<T>> call, CancellationToken ct) => Task.Run(call, ct);

    internal Task<IReadOnlyList<Person>> SearchPeopleAsync(Column column, string query, CancellationToken ct) => Off(() => api.SearchPeopleAsync(item!.WebUrl, column, query, ct), ct);

    internal async Task<IReadOnlyList<Term>> TermsAsync(Column column, CancellationToken ct)
    {
        if (!termCache.TryGetValue(column.Name, out var terms))
        {
            termCache[column.Name] = terms = await Off(() => api.TermsAsync(item!.Site, column, CultureInfo.CurrentUICulture.Name, ct), ct);
        }
        return terms;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing.Dispose();
            picture.Image?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>A list entry with its own text.</summary>
    internal sealed record Choice(string Text, object Value)
    {
        public override string ToString() => Text;
    }

    /// <summary>One column's control: its value as now shown, and why it can't be saved (null when it can).</summary>
    internal abstract class Editor(Column column, ColumnValue original)
    {
        public Column Column { get; } = column;
        public ColumnValue Original { get; } = original;
        public abstract Control Control { get; }
        public abstract ColumnValue Value { get; }
        public event Action? Changed;

        protected void OnChanged() => Changed?.Invoke();

        public virtual string? Problem() => Column.Required && Column.Kind != ColumnKind.ReadOnly && Value.IsEmpty(Column.Kind) ? UiText.Get("Details.Required") : null;

        public static Editor For(Column column, ColumnValue value, ColumnValue original, DetailsWindow window) => column.Kind switch
        {
            ColumnKind.Text or ColumnKind.Note => new TextEditor(column, value, original),
            ColumnKind.Number => new NumberEditor(column, value, original),
            ColumnKind.DateTime => new DateEditor(column, value, original),
            ColumnKind.Boolean => new BooleanEditor(column, value, original),
            ColumnKind.Choice when column.Multi => new ChoicesEditor(column, value, original),
            ColumnKind.Choice => new ChoiceEditor(column, value, original),
            ColumnKind.User or ColumnKind.Taxonomy => new PickedEditor(column, value, original, window),
            _ => new ShownEditor(column, value, original),
        };
    }

    sealed class TextEditor : Editor
    {
        readonly TextBox box;

        public TextEditor(Column column, ColumnValue value, ColumnValue original) : base(column, original)
        {
            var note = column.Kind == ColumnKind.Note;
            box = new TextBox { Text = value.Text ?? "", Multiline = note, ScrollBars = note ? ScrollBars.Vertical : ScrollBars.None, MaxLength = note ? 63999 : column.MaxLength ?? 255 };
            if (note)
            {
                box.Height = box.Font.Height * 4 + 10;
                box.AcceptsReturn = true;
            }
            box.TextChanged += (_, _) => OnChanged();
        }

        public override Control Control => box;

        public override ColumnValue Value => new() { Text = box.Text.Length == 0 ? null : box.Text };
    }

    sealed class NumberEditor : Editor
    {
        readonly TextBox box;

        public NumberEditor(Column column, ColumnValue value, ColumnValue original) : base(column, original)
        {
            box = new TextBox { Text = value.Number?.ToString("G", CultureInfo.CurrentCulture) ?? "" };
            box.TextChanged += (_, _) => OnChanged();
        }

        public override Control Control => box;

        double? Parsed => double.TryParse(box.Text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var number) ? number : null;

        public override ColumnValue Value => new() { Number = Parsed };

        public override string? Problem()
        {
            if (box.Text.Trim().Length > 0 && Parsed is null)
            {
                return UiText.Get("Details.NotNumber");
            }
            if (Parsed is { } value && (value < (Column.Minimum ?? double.MinValue) || value > (Column.Maximum ?? double.MaxValue)))
            {
                return UiText.Get("Details.Range", Column.Minimum?.ToString("G", CultureInfo.CurrentCulture) ?? "", Column.Maximum?.ToString("G", CultureInfo.CurrentCulture) ?? "");
            }
            return base.Problem();
        }
    }

    sealed class DateEditor : Editor
    {
        readonly DateTimePicker picker;

        public DateEditor(Column column, ColumnValue value, ColumnValue original) : base(column, original)
        {
            var format = CultureInfo.CurrentCulture.DateTimeFormat;
            picker = new DateTimePicker
            {
                ShowCheckBox = true,
                Checked = value.Date is not null,
                Format = DateTimePickerFormat.Custom,
                CustomFormat = column.DateOnly ? TwoDigits(format.ShortDatePattern) : $"{TwoDigits(format.ShortDatePattern)} {format.ShortTimePattern}",
            };
            if (value.Date is { } date)
            {
                picker.Value = date.ToLocalTime().DateTime;
            }
            picker.ValueChanged += (_, _) => OnChanged();
        }

        public override Control Control => picker;

        /// <summary>d-M-yyyy as dd-MM-yyyy: the picker pads single-digit fields with a space ("12- 11-2026").</summary>
        static string TwoDigits(string pattern) =>
            System.Text.RegularExpressions.Regex.Replace(pattern, "(?<![dM])([dM])(?![dM])", "$1$1");

        public override ColumnValue Value => new()
        {
            Date = !picker.Checked ? null
                : Original.Date is { } before && (Column.DateOnly ? before.ToLocalTime().Date == picker.Value.Date : before.ToLocalTime().DateTime == picker.Value) ? before
                : Column.DateOnly ? new DateTimeOffset(picker.Value.Date, TimeZoneInfo.Local.GetUtcOffset(picker.Value.Date))
                : new DateTimeOffset(picker.Value, TimeZoneInfo.Local.GetUtcOffset(picker.Value)),
        };
    }

    sealed class BooleanEditor : Editor
    {
        readonly CheckBox box;

        public BooleanEditor(Column column, ColumnValue value, ColumnValue original) : base(column, original)
        {
            box = new CheckBox { Checked = value.Boolean ?? false, AutoSize = true, Text = UiText.Get("Details.Yes") };
            box.CheckedChanged += (_, _) => OnChanged();
        }

        public override Control Control => box;

        public override ColumnValue Value => new() { Boolean = box.Checked };

        public override string? Problem() => null;
    }

    /// <summary>One choice: from the list, or typed where the column allows it. A value not on the list stays while it isn't changed.</summary>
    sealed class ChoiceEditor : Editor
    {
        readonly ComboBox box;

        public ChoiceEditor(Column column, ColumnValue value, ColumnValue original) : base(column, original)
        {
            box = new ComboBox { DropDownStyle = column.FillIn ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList };
            box.Items.Add("");
            box.Items.AddRange([.. column.Choices]);
            var current = value.Choices.FirstOrDefault() ?? "";
            if (current.Length > 0 && !column.Choices.Contains(current))
            {
                box.Items.Add(current);
            }
            box.SelectedItem = current;
            if (column.FillIn)
            {
                box.Text = current;
            }
            box.SelectedIndexChanged += (_, _) => OnChanged();
            box.TextChanged += (_, _) => OnChanged();
        }

        public override Control Control => box;

        string Current => (Column.FillIn ? box.Text : box.SelectedItem as string ?? "").Trim();

        public override ColumnValue Value => new() { Choices = Current.Length == 0 ? [] : [Current] };

        public override string? Problem() =>
            Current.Length > 0 && !Column.FillIn && !Column.Choices.Contains(Current) && Current != Original.Choices.FirstOrDefault()
                ? UiText.Get("Details.FromList") : base.Problem();
    }

    sealed class ChoicesEditor : Editor
    {
        readonly CheckedListBox list;

        public ChoicesEditor(Column column, ColumnValue value, ColumnValue original) : base(column, original)
        {
            list = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
            foreach (var choice in column.Choices.Concat(value.Choices.Where(c => !column.Choices.Contains(c))))
            {
                list.Items.Add(choice, value.Choices.Contains(choice));
            }
            list.Height = Math.Min(Math.Max(list.Items.Count, 1), 6) * list.ItemHeight + 6;
            list.ItemCheck += (_, _) => list.BeginInvoke(OnChanged);
        }

        public override Control Control => list;

        public override ColumnValue Value => new() { Choices = [.. list.CheckedItems.Cast<string>()] };
    }

    /// <summary>People or terms as chips (each with its own remove), and Add or Change (a picker).</summary>
    sealed class PickedEditor : Editor
    {
        readonly FlowLayoutPanel panel = new() { AutoSize = true, Margin = Padding.Empty };
        readonly List<Choice> picked = [];
        readonly Button add;
        readonly DetailsWindow window;

        public PickedEditor(Column column, ColumnValue value, ColumnValue original, DetailsWindow window) : base(column, original)
        {
            this.window = window;
            panel.WrapContents = column.Multi;
            add = Look.Link("", Look.Window);
            add.MinimumSize = new Size(0, window.Scaled(30));
            add.Margin = Padding.Empty;
            add.Click += async (_, _) => await AddAsync();
            picked.AddRange(column.Kind == ColumnKind.User ? value.People.Select(p => new Choice(p.Name, p)) : value.Terms.Select(t => new Choice(t.Label, t)));
            panel.EnabledChanged += (_, _) => Show();
            Show();
        }

        public override Control Control => panel;

        public override ColumnValue Value => Column.Kind == ColumnKind.User
            ? new() { People = [.. picked.Select(c => (Person)c.Value)] }
            : new() { Terms = [.. picked.Select(c => (Term)c.Value)] };

        void Show()
        {
            panel.SuspendLayout();
            foreach (var chip in panel.Controls.OfType<ChipControl>().ToList())
            {
                panel.Controls.Remove(chip);
                chip.Dispose();
            }
            panel.Controls.Remove(add);
            foreach (var choice in picked)
            {
                var chip = new ChipControl(choice.Text, panel.Enabled) { Margin = new Padding(0, 0, window.Scaled(6), window.Scaled(6)) };
                chip.Removed += () =>
                {
                    picked.Remove(choice);
                    Show();
                    OnChanged();
                };
                panel.Controls.Add(chip);
            }
            add.Text = UiText.Get(panel.Enabled ? picked.Count > 0 && !Column.Multi ? "Details.Change" : "Details.Add" : "Details.NotSet");
            add.Enabled = panel.Enabled;
            add.Visible = panel.Enabled || picked.Count == 0;
            add.ForeColor = panel.Enabled ? Look.Accent : Look.Subtle;
            panel.Controls.Add(add);
            panel.ResumeLayout();
        }

        async Task AddAsync()
        {
            using var picker = new PickerDialog(Column, window);
            if (picker.ShowDialog(window) != DialogResult.OK || picker.Picked.Count == 0)
            {
                return;
            }
            if (!Column.Multi)
            {
                picked.Clear();
            }
            picked.AddRange(picker.Picked.Where(p => !picked.Any(c => c.Value.Equals(p.Value))));
            Show();
            OnChanged();
            await Task.CompletedTask;
        }
    }

    /// <summary>A rounded tag with a name and a small cross that removes it.</summary>
    sealed class ChipControl : Control
    {
        readonly bool removable;
        Rectangle cross;
        bool overCross;

        public event Action? Removed;

        public ChipControl(string text, bool removable)
        {
            this.removable = removable;
            Text = text;
            Font = Look.Body;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Default;
            AccessibleName = text;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Size = GetPreferredSize(Size.Empty);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text, Font);
            var height = Math.Max(text.Height + LogicalToDeviceUnits(10), LogicalToDeviceUnits(30));
            return new Size(text.Width + LogicalToDeviceUnits(removable ? 34 : 20), height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            var radius = bounds.Height / 2;
            using (var path = new GraphicsPath())
            {
                path.AddArc(bounds.X, bounds.Y, radius * 2, bounds.Height, 90, 180);
                path.AddArc(bounds.Right - radius * 2, bounds.Y, radius * 2, bounds.Height, 270, 180);
                path.CloseFigure();
                using var fill = new SolidBrush(Look.Chip);
                e.Graphics.FillPath(fill, path);
            }
            var textBounds = new Rectangle(LogicalToDeviceUnits(10), 0, Width - LogicalToDeviceUnits(removable ? 30 : 20), Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Look.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (removable)
            {
                var size = LogicalToDeviceUnits(8);
                cross = new Rectangle(Width - LogicalToDeviceUnits(20), (Height - size) / 2, size, size);
                using var pen = new Pen(overCross ? Look.Text : Look.Subtle, LogicalToDeviceUnits(1) + 0.4f);
                e.Graphics.DrawLine(pen, cross.Left, cross.Top, cross.Right, cross.Bottom);
                e.Graphics.DrawLine(pen, cross.Right, cross.Top, cross.Left, cross.Bottom);
            }
        }

        Rectangle CrossArea => Rectangle.Inflate(cross, LogicalToDeviceUnits(5), LogicalToDeviceUnits(5));

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var over = removable && CrossArea.Contains(e.Location);
            if (over != overCross)
            {
                overCross = over;
                Cursor = over ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            overCross = false;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (removable && CrossArea.Contains(e.Location))
            {
                Removed?.Invoke();
            }
        }
    }

    /// <summary>Lookups, links, rich text and the like: SharePoint's own text, changed in the browser.</summary>
    sealed class ShownEditor : Editor
    {
        readonly Label label;

        public ShownEditor(Column column, ColumnValue value, ColumnValue original) : base(column, original)
        {
            label = new Label { Text = string.IsNullOrEmpty(value.Display) ? UiText.Get("Details.NotSet") + " (" + UiText.Get("Details.InBrowser").ToLowerInvariant() + ")" : value.Display, AutoSize = true, ForeColor = Look.Subtle };
            new ToolTip().SetToolTip(label, UiText.Get("Details.InBrowser"));
        }

        public override Control Control => label;

        public override ColumnValue Value => Original;

        public override string? Problem() => null;
    }

    /// <summary>
    /// Picks people (SharePoint's people picker, searched as the user types) or terms (the column's term set, read once, filtered here).
    /// </summary>
    internal sealed class PickerDialog : Form
    {
        readonly Column column;
        readonly DetailsWindow window;
        internal readonly TextBox query = new() { Dock = DockStyle.Fill };
        readonly ListBox results = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed };
        readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill };
        readonly Button ok = Look.Primary(UiText.Get("Picker.Ok"));
        readonly System.Windows.Forms.Timer typing = new() { Interval = 350 };
        readonly CancellationTokenSource closing = new();
        CancellationTokenSource? searching;
        IReadOnlyList<Term>? terms;

        public List<Choice> Picked { get; } = [];

        public PickerDialog(Column column, DetailsWindow window)
        {
            (this.column, this.window) = (column, window);
            var people = column.Kind == ColumnKind.User;
            Text = UiText.Get(people ? "Picker.People" : "Picker.Terms");
            Font = Look.Body;
            BackColor = Look.Window;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 420);
            MinimizeBox = MaximizeBox = ShowInTaskbar = false;
            query.PlaceholderText = UiText.Get(people ? "Picker.SearchPeople" : "Picker.FilterTerms");
            results.SelectionMode = column.Multi ? System.Windows.Forms.SelectionMode.MultiExtended : System.Windows.Forms.SelectionMode.One;
            results.ItemHeight = 32;
            results.DrawItem += DrawResult;
            status.ForeColor = Look.Subtle;
            status.Padding = new Padding(0, 6, 0, 0);
            ok.DialogResult = DialogResult.OK;
            ok.Enabled = false;
            var cancel = Look.Secondary(UiText.Get("Picker.Cancel"));
            cancel.DialogResult = DialogResult.Cancel;
            ok.Margin = cancel.Margin = new Padding(8, 0, 0, 0);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, BackColor = Look.Footer, Padding = new Padding(16, 14, 20, 14) };
            buttons.Controls.AddRange([cancel, ok]);
            buttons.Paint += (_, e) =>
            {
                using var pen = new Pen(Look.Line);
                e.Graphics.DrawLine(pen, 0, 0, buttons.Width, 0);
            };
            var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Look.Line, Margin = new Padding(0, 10, 0, 0) };
            frame.Controls.Add(results);
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(20, 18, 20, 10) };
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            top.Controls.Add(query, 0, 0);
            top.Controls.Add(frame, 0, 1);
            top.Controls.Add(status, 0, 2);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(top, 0, 0);
            layout.Controls.Add(buttons, 0, 1);
            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;
            query.TextChanged += (_, _) =>
            {
                typing.Stop();
                typing.Start();
            };
            typing.Tick += async (_, _) =>
            {
                typing.Stop();
                await UpdateAsync();
            };
            results.SelectedIndexChanged += (_, _) => ok.Enabled = results.SelectedItems.Count > 0;
            results.DoubleClick += (_, _) =>
            {
                if (results.SelectedItems.Count > 0)
                {
                    DialogResult = DialogResult.OK;
                }
            };
            FormClosing += (_, _) =>
            {
                closing.Cancel();
                Picked.AddRange(DialogResult == DialogResult.OK ? results.SelectedItems.Cast<Choice>() : []);
            };
            Shown += async (_, _) =>
            {
                results.ItemHeight = LogicalToDeviceUnits(32); // owner-drawn rows don't scale with the window
                query.Focus();
                await UpdateAsync();
            };
        }

        /// <summary>A roomy row: the name, the selection in the accent's light tint with a bar at its left.</summary>
        void DrawResult(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0)
            {
                return;
            }
            var selected = (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(selected ? Look.Chip : Look.Window))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
            }
            if (selected)
            {
                using var bar = new SolidBrush(Look.Accent);
                e.Graphics.FillRectangle(bar, e.Bounds.X, e.Bounds.Y + e.Bounds.Height / 4, LogicalToDeviceUnits(3), e.Bounds.Height / 2);
            }
            var text = new Rectangle(e.Bounds.X + LogicalToDeviceUnits(12), e.Bounds.Y, e.Bounds.Width - LogicalToDeviceUnits(16), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, results.Items[e.Index].ToString(), results.Font, text, Look.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        async Task UpdateAsync()
        {
            searching?.Cancel();
            searching = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            var ct = searching.Token;
            var text = query.Text.Trim();
            try
            {
                if (column.Kind == ColumnKind.User)
                {
                    if (text.Length < 2)
                    {
                        Show([], UiText.Get("Picker.TypeMore"));
                        return;
                    }
                    status.Text = UiText.Get("Picker.Searching");
                    var found = await window.SearchPeopleAsync(column, text, ct);
                    if (!ct.IsCancellationRequested)
                    {
                        Show([.. found.Select(p => new Choice(p.Name, p))], found.Count == 0 ? UiText.Get("Picker.None") : "");
                    }
                }
                else
                {
                    if (terms is null)
                    {
                        status.Text = UiText.Get("Details.Loading");
                        terms = await window.TermsAsync(column, ct);
                    }
                    var matching = terms.Where(t => text.Length == 0 || t.Label.Contains(text, StringComparison.CurrentCultureIgnoreCase)).Take(500).ToList();
                    Show([.. matching.Select(t => new Choice(t.Label, t))], matching.Count == 0 ? UiText.Get("Picker.None") : "");
                }
            }
            catch (TermStoreUnavailableException)
            {
                Show([], UiText.Get("Picker.NoTermStore"));
                query.Enabled = false;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException && !ct.IsCancellationRequested)
            {
                Show([], PlainCause.Text(PlainCause.Of(e.Message)));
            }
        }

        void Show(IReadOnlyList<Choice> found, string text)
        {
            results.BeginUpdate();
            results.Items.Clear();
            results.Items.AddRange([.. found]);
            results.EndUpdate();
            status.Text = text;
            ok.Enabled = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                typing.Dispose();
                searching?.Dispose();
                closing.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>A size as people read it: bytes, KB, MB or GB, in the user's number format.</summary>
static class Sizes
{
    public static string Text(long bytes) => bytes switch
    {
        < 1024 => $"{bytes.ToString(CultureInfo.CurrentCulture)} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture)} KB",
        < 1024L * 1024 * 1024 => $"{(bytes / 1048576.0).ToString("0.#", CultureInfo.CurrentCulture)} MB",
        _ => $"{(bytes / 1073741824.0).ToString("0.##", CultureInfo.CurrentCulture)} GB",
    };
}
