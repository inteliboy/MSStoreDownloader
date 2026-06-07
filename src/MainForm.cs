// =============================================================================
// MainForm.cs - Main application window
// C# 5 compatible (no string interpolation, no expression-bodied members,
//                  no auto-property initializers)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace MSStoreDownloader
{
    public class MainForm : Form
    {
        // ── Services ───────────────────────────────────────────────────────────
        private Logger          _logger;
        private StoreClient     _storeClient;
        private DownloadManager _downloadManager;

        // ── Background query ───────────────────────────────────────────────────
        private CancellationTokenSource _queryCts;

        // ── Data ───────────────────────────────────────────────────────────────
        private readonly List<PackageGridRow> _rows = new List<PackageGridRow>();

        // ── Controls ───────────────────────────────────────────────────────────
        private Panel       _inputPanel;
        private Label       _lblTitle;
        private Label       _lblUrl;
        private TextBox     _txtUrl;
        private Label       _lblOrId;
        private TextBox     _txtProductId;
        private Button      _btnSearch;
        private Button      _btnCancelSearch;
        private CheckBox    _chkIncludeDeps;
        private CheckBox    _chkClipboardMonitor;
        private CheckBox    _chkCreateInstaller;
        private CheckBox    _chkSkipLicense;
        private CheckBox    _chkIndividualFolders;
        private CheckBox    _chkDebugLog;

        private ToolStrip         _gridToolStrip;
        private ToolStripButton   _tsBtnDownloadSelected;
        private ToolStripButton   _tsBtnDownloadAll;
        private ToolStripButton   _tsBtnCancelDownloads;
        private ToolStripButton   _tsBtnCopyUrl;
        private ToolStripButton   _tsBtnSelectAll;
        private ToolStripButton   _tsBtnClearResults;
        private ToolStripLabel    _tsLblFilter;
        private ToolStripComboBox _tsFilterArch;
        private ToolStripComboBox _tsRingSelector;

        private DataGridView _grid;
        private Panel        _headerStrip;

        private const int ColCheck   = 0;
        private const int ColFile    = 1;
        private const int ColType    = 2;
        private const int ColVersion = 3;
        private const int ColArch    = 4;
        private const int ColKind    = 5;
        private const int ColSize    = 6;
        private const int ColUrl     = 7;
        private const int ColStatus  = 8;

        private SplitContainer _splitContainer;
        private RichTextBox    _logBox;
        private ToolStrip      _logToolStrip;
        private ToolStripButton _tsBtnClearLog;
        private ToolStripButton _tsBtnSaveLog;

        private StatusStrip          _statusStrip;
        private ToolStripStatusLabel _statusLabel;
        private ToolStripProgressBar _statusProgress;
        private ToolStripStatusLabel _statusPackageCount;

        // Placeholder simulation
        private static readonly Color PlaceholderColor = Color.FromArgb(90, 90, 95);

        // ── Clipboard monitor ──────────────────────────────────────────────────
        private System.Windows.Forms.Timer _clipboardTimer;
        private string _lastClipboard = "";
        private static readonly System.Text.RegularExpressions.Regex StoreUrlRx =
            new System.Text.RegularExpressions.Regex(
                @"(?:apps\.microsoft\.com/(?:store/apps|detail)/|"
                + @"microsoft\.com/(?:[a-z]{2}-[a-z]{2}/)?(?:p|store/(?:apps|productId)/)[^/?]*)"  
                + @".*?([A-Za-z0-9]{9,20})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex ProductIdRx =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9]{9,20}$");

        // ── Constructor ────────────────────────────────────────────────────────

        public MainForm()
        {
            Text          = "Microsoft Store Package Downloader";
            MinimumSize   = new Size(900, 600);
            Size          = new Size(1150, 750);
            StartPosition = FormStartPosition.CenterScreen;
            Font          = new Font("Segoe UI", 9f);
            BackColor     = Color.FromArgb(30, 30, 30);
            ForeColor     = Color.FromArgb(220, 220, 220);

            InitializeComponent();
            WireEvents();

            _logger          = new Logger(_logBox);
            _logger.DebugEnabled = false;   // toggled by Debug log checkbox
            _storeClient     = new StoreClient(_logger);
            LoadAllCheckboxStates();
            _downloadManager = new DownloadManager(_logger);
            WireDownloadManagerEvents();

            _logger.Info("Microsoft Store Package Downloader ready.");
            _logger.Info("Enter a Microsoft Store URL or Product ID and click Search.");
            // Start clipboard monitor (no browser/WebView dependency)
            _clipboardTimer = new System.Windows.Forms.Timer();
            _clipboardTimer.Interval = 800;
            _clipboardTimer.Tick += ClipboardTimer_Tick;
            _clipboardTimer.Start();
            _logger.Info("Clipboard monitor active: copy a Store URL to auto-search.");

            SetStatus("Ready");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // UI CONSTRUCTION
        // ═══════════════════════════════════════════════════════════════════════

        private void InitializeComponent()
        {
            SuspendLayout();

            // Status bar
            _statusStrip = new StatusStrip();
            _statusStrip.BackColor  = Color.FromArgb(24, 24, 24);
            _statusStrip.ForeColor  = Color.FromArgb(180, 180, 180);
            _statusStrip.SizingGrip = true;

            _statusLabel = new ToolStripStatusLabel("Ready");
            _statusLabel.AutoSize  = true;
            _statusLabel.ForeColor = Color.FromArgb(180, 180, 180);
            _statusLabel.Spring    = true;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

            _statusProgress = new ToolStripProgressBar();
            _statusProgress.Width   = 200;
            _statusProgress.Visible = false;
            _statusProgress.Minimum = 0;
            _statusProgress.Maximum = 100;

            _statusPackageCount = new ToolStripStatusLabel("No packages");
            _statusPackageCount.ForeColor = Color.FromArgb(120, 180, 120);
            _statusPackageCount.AutoSize  = true;

            _statusStrip.Items.Add(_statusLabel);
            _statusStrip.Items.Add(_statusProgress);
            _statusStrip.Items.Add(new ToolStripSeparator());
            _statusStrip.Items.Add(_statusPackageCount);
            Controls.Add(_statusStrip);

            // Main panel
            Panel mainPanel = new Panel();
            mainPanel.Dock      = DockStyle.Fill;
            mainPanel.BackColor = Color.FromArgb(30, 30, 30);
            Controls.Add(mainPanel);
            mainPanel.BringToFront();

            BuildInputPanel(mainPanel);

            _splitContainer = new SplitContainer();
            _splitContainer.Dock            = DockStyle.Fill;
            _splitContainer.Orientation     = Orientation.Horizontal;
            _splitContainer.SplitterDistance = 340;
            _splitContainer.Panel1MinSize   = 150;
            _splitContainer.Panel2MinSize   = 80;
            _splitContainer.BackColor       = Color.FromArgb(30, 30, 30);
            mainPanel.Controls.Add(_splitContainer);
            _splitContainer.BringToFront();

            BuildGridPanel(_splitContainer.Panel1);
            BuildLogPanel(_splitContainer.Panel2);

            ResumeLayout(false);
        }

        private void BuildInputPanel(Panel parent)
        {
            _inputPanel = new Panel();
            _inputPanel.Dock      = DockStyle.Top;
            _inputPanel.Height    = 100;
            _inputPanel.BackColor = Color.FromArgb(38, 38, 38);
            _inputPanel.Padding   = new Padding(10, 8, 10, 8);
            parent.Controls.Add(_inputPanel);

            _lblTitle = new Label();
            _lblTitle.Text      = "Microsoft Store Package Downloader";
            _lblTitle.Font      = new Font("Segoe UI", 12f, FontStyle.Bold);
            _lblTitle.ForeColor = Color.FromArgb(90, 180, 255);
            _lblTitle.AutoSize  = true;
            _lblTitle.Location  = new Point(10, 8);
            _inputPanel.Controls.Add(_lblTitle);

            _lblUrl = CreateLabel("Store URL:", new Point(10, 42));
            _inputPanel.Controls.Add(_lblUrl);

            _txtUrl = new TextBox();
            _txtUrl.Location    = new Point(90, 39);
            _txtUrl.Width       = 430;
            _txtUrl.BackColor   = Color.FromArgb(50, 50, 55);
            _txtUrl.ForeColor   = Color.FromArgb(220, 220, 220);
            _txtUrl.BorderStyle = BorderStyle.FixedSingle;
            _inputPanel.Controls.Add(_txtUrl);

            _lblOrId = CreateLabel("or Product ID:", new Point(530, 42));
            _inputPanel.Controls.Add(_lblOrId);

            _txtProductId = new TextBox();
            _txtProductId.Location        = new Point(630, 39);
            _txtProductId.Width           = 130;
            _txtProductId.BackColor       = Color.FromArgb(50, 50, 55);
            _txtProductId.ForeColor       = Color.FromArgb(220, 220, 220);
            _txtProductId.BorderStyle     = BorderStyle.FixedSingle;
            _txtProductId.CharacterCasing = CharacterCasing.Upper;
            _inputPanel.Controls.Add(_txtProductId);

            _chkIncludeDeps = new CheckBox();
            _chkIncludeDeps.Text      = "Include dependencies";
            _chkIncludeDeps.ForeColor = Color.FromArgb(180, 180, 180);
            _chkIncludeDeps.AutoSize  = true;
            _chkIncludeDeps.Location  = new Point(10, 72);
            _chkIncludeDeps.Checked   = true;
            _inputPanel.Controls.Add(_chkIncludeDeps);

            _btnSearch = CreateButton("Search", new Point(930, 36),
                Color.FromArgb(0, 120, 212), Color.White);
            _inputPanel.Controls.Add(_btnSearch);

            _btnCancelSearch = CreateButton("Cancel", new Point(1025, 36),
                Color.FromArgb(180, 50, 50), Color.White);
            _btnCancelSearch.Enabled = false;
            _inputPanel.Controls.Add(_btnCancelSearch);

            // Row 2 — option checkboxes
            _chkClipboardMonitor = new CheckBox();
            _chkClipboardMonitor.Text      = "Clipboard monitor";
            _chkClipboardMonitor.ForeColor = Color.FromArgb(180, 180, 180);
            _chkClipboardMonitor.AutoSize  = true;
            _chkClipboardMonitor.Location  = new Point(185, 72);
            _chkClipboardMonitor.Checked   = true;
            _inputPanel.Controls.Add(_chkClipboardMonitor);

            _chkCreateInstaller = new CheckBox();
            _chkCreateInstaller.Text      = "Create installer script";
            _chkCreateInstaller.ForeColor = Color.FromArgb(180, 180, 180);
            _chkCreateInstaller.AutoSize  = true;
            _chkCreateInstaller.Location  = new Point(340, 72);
            _chkCreateInstaller.Checked   = false;
            _inputPanel.Controls.Add(_chkCreateInstaller);

            _chkSkipLicense = new CheckBox();
            _chkSkipLicense.Text      = "Allow unsigned";
            _chkSkipLicense.ForeColor = Color.FromArgb(180, 180, 180);
            _chkSkipLicense.AutoSize  = true;
            _chkSkipLicense.Location  = new Point(490, 72);
            _chkSkipLicense.Checked   = false;
            _inputPanel.Controls.Add(_chkSkipLicense);

            _chkIndividualFolders = new CheckBox();
            _chkIndividualFolders.Text      = "Individual app folders";
            _chkIndividualFolders.ForeColor = Color.FromArgb(180, 180, 180);
            _chkIndividualFolders.AutoSize  = true;
            _chkIndividualFolders.Location  = new Point(615, 72);
            _chkIndividualFolders.Checked   = false;
            _inputPanel.Controls.Add(_chkIndividualFolders);

            _chkDebugLog = new CheckBox();
            _chkDebugLog.Text      = "Debug log";
            _chkDebugLog.ForeColor = Color.FromArgb(150, 150, 180);
            _chkDebugLog.AutoSize  = true;
            _chkDebugLog.Location  = new Point(775, 72);
            _chkDebugLog.Checked   = false;
            _inputPanel.Controls.Add(_chkDebugLog);
        }

        private void BuildGridPanel(Panel parent)
        {
            _gridToolStrip = new ToolStrip();
            _gridToolStrip.Dock      = DockStyle.Top;
            _gridToolStrip.BackColor = Color.FromArgb(35, 35, 38);
            _gridToolStrip.ForeColor = Color.FromArgb(200, 200, 200);
            _gridToolStrip.GripStyle = ToolStripGripStyle.Hidden;
            _gridToolStrip.Renderer  = new DarkToolStripRenderer();

            _tsBtnDownloadSelected = CreateTsButton("Download Selected", "Download checked packages");
            _tsBtnDownloadSelected.BackColor = Color.FromArgb(0, 110, 60);
            _tsBtnDownloadSelected.ForeColor = Color.FromArgb(180, 255, 180);
            _tsBtnDownloadAll      = CreateTsButton("Download All",      "Download all listed packages");
            _tsBtnDownloadAll.BackColor = Color.FromArgb(0, 90, 50);
            _tsBtnDownloadAll.ForeColor = Color.FromArgb(160, 240, 160);
            _tsBtnCancelDownloads  = CreateTsButton("Cancel Downloads",  "Cancel all active downloads");
            _tsBtnCancelDownloads.BackColor = Color.FromArgb(120, 40, 40);
            _tsBtnCancelDownloads.ForeColor = Color.FromArgb(255, 180, 180);
            _tsBtnCopyUrl          = CreateTsButton("Copy URL",          "Copy download URL(s) to clipboard");
            _tsBtnCopyUrl.ForeColor = Color.FromArgb(160, 210, 255);
            _tsBtnSelectAll        = CreateTsButton("Select All",        "Toggle select all");
            _tsBtnClearResults     = CreateTsButton("Clear",             "Clear results list");
            _tsBtnClearResults.ForeColor = Color.FromArgb(200, 140, 140);

            _tsLblFilter  = new ToolStripLabel("  Filter arch: ");
            _tsLblFilter.ForeColor = Color.FromArgb(160, 160, 160);

            _tsFilterArch = new ToolStripComboBox();
            _tsFilterArch.DropDownStyle = ComboBoxStyle.DropDownList;
            _tsFilterArch.Width         = 100;
            _tsFilterArch.BackColor     = Color.FromArgb(50, 50, 55);
            _tsFilterArch.ForeColor     = Color.FromArgb(220, 220, 220);
            _tsFilterArch.Items.AddRange(new object[] { "All", "x64", "x86", "arm64", "arm", "neutral" });
            _tsFilterArch.SelectedIndex = 1;

            _gridToolStrip.Items.Add(_tsBtnDownloadSelected);
            _gridToolStrip.Items.Add(_tsBtnDownloadAll);
            _gridToolStrip.Items.Add(_tsBtnCancelDownloads);
            _gridToolStrip.Items.Add(new ToolStripSeparator());
            _gridToolStrip.Items.Add(_tsBtnCopyUrl);
            _gridToolStrip.Items.Add(_tsBtnSelectAll);
            _gridToolStrip.Items.Add(_tsBtnClearResults);
            _gridToolStrip.Items.Add(_tsLblFilter);
            _gridToolStrip.Items.Add(_tsFilterArch);

            ToolStripLabel tsLblRing = new ToolStripLabel("  Ring: ");
            tsLblRing.ForeColor = Color.FromArgb(160, 160, 160);
            _tsRingSelector = new ToolStripComboBox();
            _tsRingSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            _tsRingSelector.Width         = 90;
            _tsRingSelector.BackColor     = Color.FromArgb(50, 50, 55);
            _tsRingSelector.ForeColor     = Color.FromArgb(220, 220, 220);
            _tsRingSelector.ToolTipText   = "Release ring: Retail=stable, RP=Release Preview, Slow/Fast=Insider";
            _tsRingSelector.Items.AddRange(new object[] { "Retail", "Release Preview", "Slow", "Fast" });
            _tsRingSelector.SelectedIndex = 1;   // Release Preview by default
            _gridToolStrip.Items.Add(tsLblRing);
            _gridToolStrip.Items.Add(_tsRingSelector);
            parent.Controls.Add(_gridToolStrip);

            _grid = new DataGridView();
            _grid.Dock                       = DockStyle.Fill;
            _grid.AllowUserToAddRows         = false;
            _grid.AllowUserToDeleteRows      = false;
            _grid.AllowUserToResizeRows      = false;
            _grid.ReadOnly                   = false;
            _grid.MultiSelect                = true;
            _grid.SelectionMode              = DataGridViewSelectionMode.FullRowSelect;
            _grid.RowHeadersVisible          = false;
            _grid.AutoSizeRowsMode           = DataGridViewAutoSizeRowsMode.None;
            _grid.RowTemplate.Height         = 24;
            _grid.BackgroundColor            = Color.FromArgb(28, 28, 30);
            _grid.GridColor                  = Color.FromArgb(50, 50, 55);
            _grid.BorderStyle                = BorderStyle.None;
            _grid.ScrollBars                 = ScrollBars.Both;
            // Hide the native column header row entirely — we use a static
            // dark-themed label row as the first grid row instead, because the
            // native header cannot be themed reliably on .NET Framework 4.8.
            _grid.ColumnHeadersVisible       = false;
            _grid.CellBorderStyle            = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.AllowUserToOrderColumns    = false;   // columns not movable
            _grid.AllowUserToResizeColumns   = true;

            _grid.DefaultCellStyle.BackColor          = Color.FromArgb(28, 28, 30);
            _grid.DefaultCellStyle.ForeColor          = Color.FromArgb(210, 210, 210);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 90, 160);
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.DefaultCellStyle.Font               = new Font("Consolas", 8.5f);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(32, 32, 36);

            AddCheckBoxColumn();
            AddTextColumn(ColFile,    "Package",    380, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(ColType,    "Type",        90, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(ColVersion, "Version",    100, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(ColArch,    "Arch",        70, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(ColKind,    "Relevancy",   90, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(ColSize,    "Size",        80, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(ColUrl,     "URL",        300, DataGridViewAutoSizeColumnMode.Fill);
            AddTextColumn(ColStatus,  "Status",     140, DataGridViewAutoSizeColumnMode.None);

            // Manual dark-themed header strip (docked above the grid).
            // The native header is hidden; this strip mirrors column widths.
            _headerStrip = new Panel();
            _headerStrip.Dock      = DockStyle.Top;
            _headerStrip.Height    = 26;
            _headerStrip.BackColor = Color.FromArgb(45, 65, 110);
            _headerStrip.Paint    += HeaderStrip_Paint;
            parent.Controls.Add(_headerStrip);
            parent.Controls.Add(_grid);
            _grid.BringToFront();
            _headerStrip.BringToFront();

            // Repaint the strip when columns resize or the grid scrolls horizontally
            _grid.ColumnWidthChanged += delegate(object s, DataGridViewColumnEventArgs ev)
            { _headerStrip.Invalidate(); };
            _grid.Scroll += delegate(object s, ScrollEventArgs ev)
            { if (ev.ScrollOrientation == ScrollOrientation.HorizontalScroll) _headerStrip.Invalidate(); };
        }

        private void HeaderStrip_Paint(object sender, PaintEventArgs e)
        {
            if (_grid == null || _grid.Columns.Count == 0) return;
            Graphics g = e.Graphics;
            g.Clear(Color.FromArgb(45, 65, 110));

            Font  font = new Font("Segoe UI", 9f, FontStyle.Bold);
            Color fg   = Color.FromArgb(225, 238, 255);
            Color line = Color.FromArgb(80, 120, 180);

            // Account for horizontal scroll offset
            int scrollX = _grid.HorizontalScrollingOffset;
            int x = -scrollX;

            for (int i = 0; i < _grid.Columns.Count; i++)
            {
                DataGridViewColumn col = _grid.Columns[i];
                if (!col.Visible) continue;
                int w = col.Width;
                Rectangle cellRect = new Rectangle(x, 0, w, _headerStrip.Height);

                // vertical separator
                using (Pen pen = new Pen(line))
                    g.DrawLine(pen, x + w - 1, 0, x + w - 1, _headerStrip.Height);

                string label = col.HeaderText;
                if (!string.IsNullOrEmpty(label))
                {
                    Rectangle textRect = new Rectangle(x + 4, 0, w - 6, _headerStrip.Height);
                    TextRenderer.DrawText(g, label, font, textRect, fg,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                x += w;
            }

            // bottom border
            using (Pen pen = new Pen(line))
                g.DrawLine(pen, 0, _headerStrip.Height - 1, _headerStrip.Width, _headerStrip.Height - 1);
        }

        private void BuildLogPanel(Panel parent)
        {
            _logToolStrip = new ToolStrip();
            _logToolStrip.Dock      = DockStyle.Top;
            _logToolStrip.BackColor = Color.FromArgb(35, 35, 38);
            _logToolStrip.ForeColor = Color.FromArgb(200, 200, 200);
            _logToolStrip.GripStyle = ToolStripGripStyle.Hidden;
            _logToolStrip.Renderer  = new DarkToolStripRenderer();

            ToolStripLabel logLabel = new ToolStripLabel("Output Log");
            logLabel.ForeColor = Color.FromArgb(140, 140, 140);
            _logToolStrip.Items.Add(logLabel);
            _logToolStrip.Items.Add(new ToolStripSeparator());

            _tsBtnClearLog = CreateTsButton("Clear Log", "Clear log output");
            _tsBtnSaveLog  = CreateTsButton("Save Log",  "Save log to file");
            _logToolStrip.Items.Add(_tsBtnClearLog);
            _logToolStrip.Items.Add(_tsBtnSaveLog);
            parent.Controls.Add(_logToolStrip);

            _logBox = new RichTextBox();
            _logBox.Dock        = DockStyle.Fill;
            _logBox.ReadOnly    = true;
            _logBox.BackColor   = Color.FromArgb(18, 18, 20);
            _logBox.ForeColor   = Color.FromArgb(200, 200, 200);
            _logBox.Font        = new Font("Consolas", 8.5f);
            _logBox.BorderStyle = BorderStyle.None;
            _logBox.WordWrap    = false;
            _logBox.ScrollBars  = RichTextBoxScrollBars.Both;
            parent.Controls.Add(_logBox);
        }

        // ── Column helpers ─────────────────────────────────────────────────────

        private void AddCheckBoxColumn()
        {
            DataGridViewCheckBoxColumn col = new DataGridViewCheckBoxColumn();
            col.Name               = "colCheck";
            col.HeaderText         = "V";
            col.Width              = 30;
            col.ReadOnly           = false;
            col.Resizable          = DataGridViewTriState.False;
            col.SortMode           = DataGridViewColumnSortMode.NotSortable;
            col.FalseValue         = false;
            col.TrueValue          = true;
            col.IndeterminateValue = false;
            _grid.Columns.Add(col);
        }

        private void AddTextColumn(int index, string header, int width,
                                   DataGridViewAutoSizeColumnMode autoSize)
        {
            DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn();
            col.Name         = "col" + header.Replace(" ", "");
            col.HeaderText   = header;
            col.Width        = width;
            col.ReadOnly     = true;
            col.SortMode     = DataGridViewColumnSortMode.NotSortable;
            col.AutoSizeMode = autoSize;
            _grid.Columns.Add(col);
        }

        private static Label CreateLabel(string text, Point location)
        {
            Label lbl = new Label();
            lbl.Text      = text;
            lbl.ForeColor = Color.FromArgb(160, 160, 160);
            lbl.AutoSize  = true;
            lbl.Location  = location;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            return lbl;
        }

        private static Button CreateButton(string text, Point location,
                                           Color backColor, Color foreColor)
        {
            Button btn = new Button();
            btn.Text      = text;
            btn.Location  = location;
            btn.Size      = new Size(90, 26);
            btn.BackColor = backColor;
            btn.ForeColor = foreColor;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.Cursor    = Cursors.Hand;
            return btn;
        }

        private static ToolStripButton CreateTsButton(string text, string tooltip)
        {
            ToolStripButton btn = new ToolStripButton(text);
            btn.ToolTipText  = tooltip;
            btn.DisplayStyle = ToolStripItemDisplayStyle.Text;
            return btn;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // EVENT WIRING
        // ═══════════════════════════════════════════════════════════════════════

        private void WireEvents()
        {
            _btnSearch.Click       += BtnSearch_Click;
            _btnCancelSearch.Click += BtnCancelSearch_Click;
            _txtUrl.KeyDown        += TxtUrl_KeyDown;
            _txtProductId.KeyDown  += TxtProductId_KeyDown;

            // Placeholder text simulation
            AddPlaceholder(_txtUrl,       "https://www.microsoft.com/en-us/p/app-name/9NBLGGH4NNS1");
            AddPlaceholder(_txtProductId, "9NBLGGH4NNS1");

            _tsBtnDownloadSelected.Click     += TsBtnDownloadSelected_Click;
            _tsBtnDownloadAll.Click          += TsBtnDownloadAll_Click;
            _tsBtnCancelDownloads.Click      += TsBtnCancelDownloads_Click;
            _tsBtnCopyUrl.Click              += TsBtnCopyUrl_Click;
            _tsBtnSelectAll.Click            += TsBtnSelectAll_Click;
            _tsBtnClearResults.Click         += TsBtnClearResults_Click;
            _tsFilterArch.SelectedIndexChanged += TsFilterArch_Changed;
            _tsRingSelector.SelectedIndexChanged += delegate(object s, EventArgs e)
            {
                // Re-run search when ring changes if a product is loaded
                bool hasUrl = _txtUrl.ForeColor != PlaceholderColor && !string.IsNullOrWhiteSpace(_txtUrl.Text);
                bool hasId  = _txtProductId.ForeColor != PlaceholderColor && !string.IsNullOrWhiteSpace(_txtProductId.Text);
                if ((hasUrl || hasId) && _rows.Count > 0)
                    BtnSearch_Click(this, EventArgs.Empty);
            };

            _grid.CellDoubleClick              += Grid_CellDoubleClick;
            _grid.CellValueChanged             += Grid_CellValueChanged;
            _grid.CurrentCellDirtyStateChanged += Grid_DirtyStateChanged;

            _tsBtnClearLog.Click += TsBtnClearLog_Click;
            _tsBtnSaveLog.Click  += TsBtnSaveLog_Click;

            FormClosing += MainForm_FormClosing;
            Resize      += MainForm_Resize;

            _chkDebugLog.CheckedChanged += delegate(object s, EventArgs e)
            {
                if (_logger != null) _logger.DebugEnabled = _chkDebugLog.Checked;
            };

            _chkClipboardMonitor.CheckedChanged += delegate(object s, EventArgs e)
            {
                if (_clipboardTimer != null)
                {
                    if (_chkClipboardMonitor.Checked) _clipboardTimer.Start();
                    else _clipboardTimer.Stop();
                }
            };
        }

        private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) BtnSearch_Click(sender, e);
        }

        private void TxtProductId_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) BtnSearch_Click(sender, e);
        }

        private void TsBtnClearResults_Click(object sender, EventArgs e) { ClearResults(); }
        private void TsBtnClearLog_Click(object sender, EventArgs e) { if (_logger != null) _logger.Clear(); }
        private void MainForm_Resize(object sender, EventArgs e) { AdjustInputLayout(); }

        private void WireDownloadManagerEvents()
        {
            _downloadManager.DownloadProgress  += DownloadManager_Progress;
            _downloadManager.DownloadCompleted += DownloadManager_Completed;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // PLACEHOLDER HELPER
        // ═══════════════════════════════════════════════════════════════════════

        private static void AddPlaceholder(TextBox box, string hint)
        {
            Color normalFg = box.ForeColor;
            box.ForeColor = PlaceholderColor;
            box.Text      = hint;

            box.GotFocus += delegate(object s, EventArgs e)
            {
                if (box.Text == hint && box.ForeColor == PlaceholderColor)
                {
                    box.ForeColor = normalFg;
                    box.Text      = "";
                }
            };
            box.LostFocus += delegate(object s, EventArgs e)
            {
                if (string.IsNullOrWhiteSpace(box.Text))
                {
                    box.ForeColor = PlaceholderColor;
                    box.Text      = hint;
                }
            };
        }

        // ═══════════════════════════════════════════════════════════════════════
        // SEARCH
        // ═══════════════════════════════════════════════════════════════════════

        private void BtnSearch_Click(object sender, EventArgs e)
        {
            string rawInput  = (_txtUrl.ForeColor       == PlaceholderColor) ? "" : _txtUrl.Text.Trim();
            string productId = (_txtProductId.ForeColor == PlaceholderColor) ? "" : _txtProductId.Text.Trim();

            string input = string.IsNullOrWhiteSpace(productId) ? rawInput : productId;

            if (string.IsNullOrWhiteSpace(input))
            {
                MessageBox.Show("Please enter a Microsoft Store URL or Product ID.",
                    "Input Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtUrl.Focus();
                return;
            }

            if (_queryCts != null) _queryCts.Cancel();
            _queryCts = new CancellationTokenSource();
            CancellationToken ct = _queryCts.Token;

            SetSearching(true);
            ClearResults();

            string capturedInput = input;
            string capturedRing  = GetSelectedRing();
            CancellationTokenSource capturedCts = _queryCts;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                StoreQueryResult result = null;
                try
                {
                    string resolvedId = _storeClient.ResolveProductId(capturedInput, ct);
                    if (resolvedId == null || ct.IsCancellationRequested)
                    {
                        BeginInvoke(new Action(delegate { SetSearching(false); }));
                        return;
                    }

                    string id = resolvedId;
                    BeginInvoke(new Action(delegate { _txtProductId.Text = id; }));

                    result = _storeClient.GetPackages(resolvedId, capturedRing, ct);
                }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    if (!ct.IsCancellationRequested)
                        BeginInvoke(new Action(delegate
                        {
                            MessageBox.Show("Error: " + msg, "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }));
                }

                StoreQueryResult finalResult = result;
                if (!ct.IsCancellationRequested)
                    BeginInvoke(new Action(delegate { OnSearchCompleted(finalResult); }));
                BeginInvoke(new Action(delegate { SetSearching(false); }));
            });
        }

        private void BtnCancelSearch_Click(object sender, EventArgs e)
        {
            if (_queryCts != null) _queryCts.Cancel();
            SetSearching(false);
            SetStatus("Search cancelled.");
            _logger.Warning("Search cancelled by user.");
        }

        private void OnSearchCompleted(StoreQueryResult result)
        {
            if (result == null) return;

            if (!result.Succeeded)
            {
                SetStatus("Search failed.");
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    MessageBox.Show(result.ErrorMessage, "Search Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool   includeDeps = _chkIncludeDeps.Checked;
            string archFilter  = _tsFilterArch.SelectedItem != null
                                 ? _tsFilterArch.SelectedItem.ToString() : "All";

            List<PackageInfo> toShow = new List<PackageInfo>();
            foreach (PackageInfo p in result.Packages)
            {
                if (!includeDeps && p.IsDependency) continue;
                if (archFilter != "All" &&
                    !string.Equals(p.Architecture, archFilter, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(p.Architecture, "neutral", StringComparison.OrdinalIgnoreCase))
                    continue;
                toShow.Add(p);
            }

            PopulateGrid(toShow);
            SetStatus("Found " + result.Packages.Count + " package(s) for " + result.ProductId +
                      ". Reading manifests...");

            // Fetch manifests in background to get real dependency info
            _queryCts = new CancellationTokenSource();
            CancellationToken ct = _queryCts.Token;
            StoreQueryResult capturedResult = result;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                // Step 1: fetch real file sizes for placeholder entries
                try
                {
                    _storeClient.FetchMissingSizes(capturedResult,
                        delegate(int cur, int tot)
                        {
                            BeginInvoke(new Action(delegate
                            {
                                SetStatus("Fetching sizes " + cur + "/" + tot + "...");
                            }));
                        }, ct);
                }
                catch { }

                if (!ct.IsCancellationRequested)
                    BeginInvoke(new Action(delegate { RefreshGridSizes(); }));

                // Step 2: read manifests for dependency info
                try
                {
                    _storeClient.FetchManifests(capturedResult,
                        delegate(int cur, int tot)
                        {
                            BeginInvoke(new Action(delegate
                            {
                                SetStatus("Reading manifest " + cur + "/" + tot + "...");
                            }));
                        }, ct);
                }
                catch { }

                if (!ct.IsCancellationRequested)
                {
                    BeginInvoke(new Action(delegate
                    {
                        SetStatus("Ready. Manifests loaded - dependency selection is now precise.");
                        if (_updatingCheckboxes) return;
                        _updatingCheckboxes = true;
                        try { SyncDependencyCheckboxes(); }
                        finally { _updatingCheckboxes = false; }
                    }));
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════════════
        // GRID
        // ═══════════════════════════════════════════════════════════════════════

        private void PopulateGrid(List<PackageInfo> packages)
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            _rows.Clear();

            foreach (PackageInfo pkg in packages)
            {
                PackageGridRow row = new PackageGridRow(pkg);
                _rows.Add(row);
                AddGridRow(row);
            }

            // Auto-select the newest version of each main package group.
            // Group main packages by their base name (excluding version), then
            // tick the one with the highest version in each group.
            AutoSelectNewestMainPackages();

            _grid.ResumeLayout();
            _statusPackageCount.Text = _rows.Count + " package(s) listed";
        }

        /// <summary>
        /// For each group of main packages sharing the same identity name, check
        /// the one with the highest version and uncheck the rest.
        /// </summary>
        private void AutoSelectNewestMainPackages()
        {
            // Get the active arch filter (e.g. "x64", "All")
            string archFilter = _tsFilterArch.SelectedItem != null
                                ? _tsFilterArch.SelectedItem.ToString() : "All";

            // Group main packages by identity name (version/arch stripped).
            // Within each group, find the highest version.
            // Among packages of the highest version, prefer:
            //   1. The arch that matches the filter (e.g. x64)
            //   2. neutral / bundle (covers all arches)
            //   3. Any other arch as last resort

            Dictionary<string, string> bestVersionPerName =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (PackageGridRow row in _rows)
            {
                if (row.Package.IsDependency) continue;
                if (row.Package.IsBlocked) continue;   // skip encrypted when plain exists
                string key = ExtractPackageName(row.Package.FileName);
                string cur;
                if (!bestVersionPerName.TryGetValue(key, out cur) ||
                    CompareVersions(row.Package.Version, cur) > 0)
                    bestVersionPerName[key] = row.Package.Version;
            }

            // Among rows at the best version, pick the best arch
            Dictionary<string, PackageGridRow> bestPerName =
                new Dictionary<string, PackageGridRow>(StringComparer.OrdinalIgnoreCase);

            foreach (PackageGridRow row in _rows)
            {
                if (row.Package.IsDependency) continue;
                string key = ExtractPackageName(row.Package.FileName);

                string topVer;
                if (!bestVersionPerName.TryGetValue(key, out topVer)) continue;
                if (CompareVersions(row.Package.Version, topVer) != 0) continue;

                PackageGridRow existing;
                if (!bestPerName.TryGetValue(key, out existing))
                {
                    bestPerName[key] = row;
                }
                else
                {
                    // Prefer the filtered arch, then neutral, then anything
                    int newScore  = ArchScore(row.Package.Architecture,      archFilter);
                    int prevScore = ArchScore(existing.Package.Architecture, archFilter);
                    if (newScore > prevScore)
                        bestPerName[key] = row;
                }
            }

            HashSet<string> bestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PackageGridRow r in bestPerName.Values)
                bestFiles.Add(r.Package.FileName);

            foreach (DataGridViewRow gr in _grid.Rows)
            {
                PackageGridRow row = gr.Tag as PackageGridRow;
                if (row == null || row.Package.IsDependency) continue;
                gr.Cells[ColCheck].Value = bestFiles.Contains(row.Package.FileName);
            }
        }

        /// <summary>
        /// Score how well an architecture matches the active filter.
        /// Higher = more preferred.
        /// </summary>
        private static int ArchScore(string arch, string filter)
        {
            if (string.Equals(arch, filter, StringComparison.OrdinalIgnoreCase))
                return 3;   // exact match
            if (string.Equals(arch, "neutral", StringComparison.OrdinalIgnoreCase))
                return 2;   // neutral/bundle always acceptable
            if (filter == "All")
                return 1;   // any arch fine when filter is All
            return 0;       // wrong arch
        }

        /// <summary>
        /// Adds a static, non-interactive label row styled like a header.
        /// It is read-only, not selectable, and ignored by all download/selection logic.
        /// </summary>
        private void AddGridRow(PackageGridRow row)
        {
            int idx = _grid.Rows.Add();
            DataGridViewRow gr = _grid.Rows[idx];
            gr.Tag                     = row;
            gr.Cells[ColCheck].Value   = false;
            gr.Cells[ColFile].Value    = row.FileName;
            gr.Cells[ColType].Value    = row.PackageType;
            gr.Cells[ColVersion].Value = row.Version;
            gr.Cells[ColArch].Value    = row.Architecture;
            gr.Cells[ColKind].Value    = row.Kind;
            gr.Cells[ColSize].Value    = row.Size;
            gr.Cells[ColUrl].Value     = row.DownloadUrl;
            gr.Cells[ColStatus].Value  = row.DownloadStatus;

            if (row.Package.IsBlocked)
            {
                gr.DefaultCellStyle.ForeColor = Color.FromArgb(100, 100, 100);
                gr.Cells[ColKind].ToolTipText =
                    "Encrypted package - a plain equivalent exists and is preferred.";
            }
            if (row.Package.IsDependency)
            {
                gr.DefaultCellStyle.ForeColor = Color.FromArgb(150, 150, 150);
                gr.Cells[ColKind].ToolTipText =
                    "Dependency  arch:" + row.Package.Architecture +
                    "  ver:" + row.Package.Version +
                    "\nWill be auto-selected when a compatible main package is checked.";
            }
        }

        /// <summary>
        /// Refresh the Size column for all grid rows after real sizes have been fetched.
        /// </summary>
        private void RefreshGridSizes()
        {
            foreach (DataGridViewRow gr in _grid.Rows)
            {
                PackageGridRow row = gr.Tag as PackageGridRow;
                if (row != null)
                    gr.Cells[ColSize].Value = row.Size;  // Size property reads Package.FileSizeDisplay
            }
        }

        private void UpdateGridRow(PackageGridRow row)
        {
            foreach (DataGridViewRow gr in _grid.Rows)
            {
                if (gr.Tag == row)
                {
                    gr.Cells[ColStatus].Value = row.DownloadStatus;
                    break;
                }
            }
        }

        private void ClearResults()
        {
            _grid.Rows.Clear();
            _rows.Clear();
            _statusPackageCount.Text = "No packages";
        }

        private List<PackageGridRow> GetCheckedRows()
        {
            List<PackageGridRow> result = new List<PackageGridRow>();
            foreach (DataGridViewRow gr in _grid.Rows)
            {
                PackageGridRow row = gr.Tag as PackageGridRow;
                if (row == null) continue;
                object val = gr.Cells[ColCheck].Value;
                if (val is bool && (bool)val) result.Add(row);
            }
            return result;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // DOWNLOAD
        // ═══════════════════════════════════════════════════════════════════════

        private void TsBtnDownloadSelected_Click(object sender, EventArgs e)
        {
            List<PackageGridRow> rows = GetCheckedRows();
            if (rows.Count == 0)
            {
                foreach (DataGridViewRow gr in _grid.SelectedRows)
                {
                    PackageGridRow row = gr.Tag as PackageGridRow;
                    if (row != null) rows.Add(row);
                }
            }
            if (rows.Count == 0)
            {
                MessageBox.Show("Please check at least one package to download.",
                    "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DownloadRows(rows);
        }

        private void TsBtnDownloadAll_Click(object sender, EventArgs e)
        {
            if (_rows.Count == 0)
            {
                MessageBox.Show("No packages to download.", "Empty List",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show("Download all " + _rows.Count + " package(s)?", "Confirm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            DownloadRows(_rows);
        }

        private void TsBtnCancelDownloads_Click(object sender, EventArgs e)
        {
            _downloadManager.CancelAll();
            SetStatus("All downloads cancelled.");
        }

        /// <summary>
        /// Returns (creating if needed) the MSStorePackages folder next to the exe.
        /// If subFolder is specified, creates MSStorePackages\subFolder.
        /// </summary>
        private string GetSelectedRing()
        {
            if (_tsRingSelector == null || _tsRingSelector.SelectedItem == null)
                return "Retail";
            string display = _tsRingSelector.SelectedItem.ToString();
            // Map display names to rg-adguard API ring values
            if (display == "Release Preview") return "RP";
            return display;   // Retail, Slow, Fast pass through unchanged
        }

        private static string GetDownloadFolder(string subFolder)
        {
            string exeDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            string folder = Path.Combine(exeDir, "MSStorePackages");
            if (!string.IsNullOrEmpty(subFolder))
                folder = Path.Combine(folder, subFolder);
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            return folder;
        }

        private static string GetDownloadFolder()
        {
            return GetDownloadFolder(null);
        }

        private void DownloadRows(List<PackageGridRow> rows)
        {
            // Derive app name from the first main (non-dependency) package
            string appName = "";
            foreach (PackageGridRow r in rows)
                if (!r.Package.IsDependency) { appName = ExtractPackageName(r.Package.FileName); break; }

            // Determine target folder
            string subFolder = (_chkIndividualFolders.Checked && !string.IsNullOrEmpty(appName))
                               ? appName : null;
            string folder = GetDownloadFolder(subFolder);

            foreach (PackageGridRow row in rows)
            {
                string dest = Path.Combine(folder, row.FileName);
                // Skip if already fully downloaded
                if (File.Exists(dest) && new FileInfo(dest).Length > 0 &&
                    row.Package.FileSize > 0 &&
                    new FileInfo(dest).Length == row.Package.FileSize)
                {
                    _logger.Info("Already downloaded, skipping: " + row.FileName);
                    row.DownloadStatus = "Already exists";
                    UpdateGridRow(row);
                    continue;
                }
                StartDownloadRow(row, dest);
            }
            if (rows.Count > 0)
                SetStatus("Downloading " + rows.Count + " package(s) to: " + folder);

            if (_chkCreateInstaller.Checked)
                GenerateInstallerScript(rows, folder);
        }

        private void StartDownloadRow(PackageGridRow row, string destination)
        {
            Guid taskId = _downloadManager.StartDownload(row.Package, destination);
            row.DownloadTaskId = taskId;
            row.DownloadStatus = "Queued...";
            UpdateGridRow(row);
            SetStatus("Download started: " + row.FileName);
        }

        private void DownloadManager_Progress(object sender, DownloadProgressEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new EventHandler<DownloadProgressEventArgs>(DownloadManager_Progress), sender, e);
                return;
            }

            PackageGridRow row = null;
            foreach (PackageGridRow r in _rows)
            {
                if (r.DownloadTaskId.HasValue && r.DownloadTaskId.Value == e.TaskId)
                { row = r; break; }
            }
            if (row == null) return;

            row.DownloadStatus = string.Format("{0:F0}%  {1}", e.ProgressPct, e.SpeedDisplay);
            UpdateGridRow(row);

            _statusProgress.Visible = true;
            _statusProgress.Value   = Math.Min((int)e.ProgressPct, 100);
            SetStatus(string.Format("Downloading {0} - {1:F1}%  ({2})", e.FileName, e.ProgressPct, e.SpeedDisplay));
        }

        private void DownloadManager_Completed(object sender, DownloadCompletedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new EventHandler<DownloadCompletedEventArgs>(DownloadManager_Completed), sender, e);
                return;
            }

            PackageGridRow row = null;
            foreach (PackageGridRow r in _rows)
            {
                if (r.DownloadTaskId.HasValue && r.DownloadTaskId.Value == e.TaskId)
                { row = r; break; }
            }
            if (row != null)
                row.DownloadStatus = e.Succeeded ? "Complete" : (e.Cancelled ? "Cancelled" : "Failed");
            if (row != null) UpdateGridRow(row);

            if (_downloadManager.ActiveDownloadCount == 0)
            {
                _statusProgress.Visible = false;
                SetStatus(e.Succeeded ? "Download complete." : "Download finished with errors.");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // GRID EVENTS
        // ═══════════════════════════════════════════════════════════════════════

        private void Grid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            DataGridViewRow gr  = _grid.Rows[e.RowIndex];
            PackageGridRow  row = gr.Tag as PackageGridRow;
            if (row == null) return;

            if (e.ColumnIndex == ColUrl)
            {
                try { System.Diagnostics.Process.Start(row.DownloadUrl); } catch { }
                return;
            }

            // Double-click starts download to the auto folder (no dialog)
            string appN = ExtractPackageName(row.Package.FileName);
            string subF = (_chkIndividualFolders.Checked && !string.IsNullOrEmpty(appN) && !row.Package.IsDependency)
                          ? appN : null;
            string dest = Path.Combine(GetDownloadFolder(subF), row.FileName);
            if (File.Exists(dest) && new FileInfo(dest).Length > 0 &&
                row.Package.FileSize > 0 &&
                new FileInfo(dest).Length == row.Package.FileSize)
            {
                _logger.Info("Already downloaded, skipping: " + row.FileName);
                row.DownloadStatus = "Already exists";
                UpdateGridRow(row);
                return;
            }
            StartDownloadRow(row, dest);
        }

        private void Grid_DirtyStateChanged(object sender, EventArgs e)
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        private void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            // Only react to checkbox column changes
            if (e.RowIndex < 0 || e.ColumnIndex != ColCheck) return;
            if (_grid.Rows[e.RowIndex].Tag == null) return;   // skip label row

            // Suppress re-entrancy (we may set cell values below)
            if (_updatingCheckboxes) return;
            _updatingCheckboxes = true;
            try
            {
                SyncDependencyCheckboxes();
            }
            finally
            {
                _updatingCheckboxes = false;
            }
        }

        private bool _updatingCheckboxes;

        /// <summary>
        /// Select dependency rows that are genuinely required by the checked
        /// main packages, using data from AppxManifest.xml when available,
        /// falling back to architecture-only matching otherwise.
        ///
        /// Manifest-based matching:
        ///   Each checked main package carries ManifestDeps (List of
        ///   ManifestDependency), each with a Name (e.g. "Microsoft.VCLibs.140.00")
        ///   and MinVersion (e.g. "14.0.30035.0").
        ///   A dependency row is selected when:
        ///     1. Its PackageIdentityName starts with the declared dep Name, AND
        ///     2. Its arch is "neutral" or matches the main package arch, AND
        ///     3. Its version >= the declared MinVersion.
        ///   When multiple rows satisfy the same dep declaration, the one with the
        ///   lowest version that still satisfies MinVersion is preferred (minimal
        ///   install); if none meets MinVersion exactly, take the highest available.
        ///
        /// Fallback (no manifest data yet):
        ///   Select deps whose arch is neutral or matches any checked main arch,
        ///   keeping only the highest version per base-name+arch group.
        /// </summary>
        private void SyncDependencyCheckboxes()
        {
            // Collect checked main packages
            List<PackageGridRow> checkedMains = new List<PackageGridRow>();
            foreach (DataGridViewRow gr in _grid.Rows)
            {
                PackageGridRow row = gr.Tag as PackageGridRow;
                if (row == null || row.Package.IsDependency) continue;
                object val = gr.Cells[ColCheck].Value;
                if (val is bool && (bool)val)
                    checkedMains.Add(row);
            }

            // Collect all dep rows
            List<DataGridViewRow> depRows = new List<DataGridViewRow>();
            foreach (DataGridViewRow gr in _grid.Rows)
            {
                PackageGridRow row = gr.Tag as PackageGridRow;
                if (row != null && row.Package.IsDependency)
                    depRows.Add(gr);
            }

            if (checkedMains.Count == 0)
            {
                foreach (DataGridViewRow gr in depRows)
                    gr.Cells[ColCheck].Value = false;
                return;
            }

            // All dependency PackageInfo objects visible in the grid
            List<PackageInfo> allDeps = new List<PackageInfo>();
            foreach (DataGridViewRow gr in depRows)
            {
                PackageGridRow row = gr.Tag as PackageGridRow;
                allDeps.Add(row.Package);
            }

            // Decide which dep files to select
            HashSet<string> selectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Check whether any main has manifest data
            bool hasManifestData = false;
            foreach (PackageGridRow m in checkedMains)
                if (m.Package.ManifestDeps != null && m.Package.ManifestDeps.Count > 0)
                { hasManifestData = true; break; }

            if (hasManifestData)
            {
                // ── Manifest-based matching ──────────────────────────────────
                foreach (PackageGridRow main in checkedMains)
                {
                    if (main.Package.ManifestDeps == null) continue;
                    string mainArch = main.Package.Architecture;
                    // A "neutral" main package is a bundle covering ALL arches.
                    // Select dep variants for all arches, not just one.
                    // Determine which dep architectures to select.
                    // If main is neutral (a bundle), use the active arch filter
                    // to pick only the relevant dep variants.
                    // If main is arch-specific, use that arch + neutral.
                    bool mainIsNeutral = string.Equals(mainArch, "neutral",
                                            StringComparison.OrdinalIgnoreCase);
                    string activeFilter = _tsFilterArch.SelectedItem != null
                                         ? _tsFilterArch.SelectedItem.ToString() : "All";

                    List<string> effectiveArchs = new List<string>();
                    if (mainIsNeutral)
                    {
                        if (activeFilter == "All")
                        {
                            // No filter: select all arch variants of each dep
                            effectiveArchs.Add("x64");
                            effectiveArchs.Add("x86");
                            effectiveArchs.Add("arm64");
                            effectiveArchs.Add("arm");
                        }
                        else
                        {
                            // Filter active: select only the filtered arch
                            effectiveArchs.Add(activeFilter);
                        }
                        effectiveArchs.Add("neutral");
                    }
                    else
                    {
                        effectiveArchs.Add(mainArch);
                        effectiveArchs.Add("neutral");
                    }

                    foreach (ManifestDependency decl in main.Package.ManifestDeps)
                    {
                        bool isInferred = decl.MinVersion == "0.0.0.0";

                        // Group best candidate by arch so we pick one dep per arch
                        Dictionary<string, PackageInfo> bestPerArch =
                            new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);

                        foreach (PackageInfo dep in allDeps)
                        {
                            string depName = ExtractPackageName(dep.FileName);

                            // Name match: exact for real manifest deps, prefix for inferred
                            bool nameMatch;
                            if (isInferred)
                                nameMatch = depName.StartsWith(decl.Name,
                                    StringComparison.OrdinalIgnoreCase);
                            else
                                nameMatch = string.Equals(depName, decl.Name,
                                    StringComparison.OrdinalIgnoreCase);
                            if (!nameMatch) continue;

                            // Arch must be in effective set
                            bool archOk = false;
                            foreach (string ea in effectiveArchs)
                                if (string.Equals(dep.Architecture, ea,
                                        StringComparison.OrdinalIgnoreCase))
                                { archOk = true; break; }
                            if (!archOk) continue;

                            // Version >= MinVersion
                            if (!isInferred &&
                                !string.IsNullOrEmpty(decl.MinVersion) &&
                                CompareVersions(dep.Version, decl.MinVersion) < 0)
                                continue;

                            // Keep lowest satisfying version per arch (minimal install)
                            string depArch = dep.Architecture.ToLowerInvariant();
                            PackageInfo existing;
                            if (!bestPerArch.TryGetValue(depArch, out existing) ||
                                CompareVersions(dep.Version, existing.Version) < 0)
                                bestPerArch[depArch] = dep;
                        }

                        foreach (PackageInfo best in bestPerArch.Values)
                            selectedFiles.Add(best.FileName);
                    }
                }
                // ── Fallback: arch-only matching, newest version per group ────
                // Fallback: collect effective archs from checked mains,
                // but if a main is neutral, use the active filter instead of all archs
                string activeFilterFb = _tsFilterArch.SelectedItem != null
                                        ? _tsFilterArch.SelectedItem.ToString() : "All";
                List<string> checkedArchs = new List<string>();
                foreach (PackageGridRow m in checkedMains)
                {
                    bool isNeutralMain = string.Equals(m.Package.Architecture, "neutral",
                                            StringComparison.OrdinalIgnoreCase);
                    if (isNeutralMain)
                    {
                        if (activeFilterFb == "All")
                        {
                            foreach (string a in new string[] { "x64", "x86", "arm64", "arm" })
                                if (!checkedArchs.Contains(a)) checkedArchs.Add(a);
                        }
                        else
                        {
                            if (!checkedArchs.Contains(activeFilterFb))
                                checkedArchs.Add(activeFilterFb);
                        }
                    }
                    else
                    {
                        if (!checkedArchs.Contains(m.Package.Architecture))
                            checkedArchs.Add(m.Package.Architecture);
                    }
                }

                Dictionary<string, PackageInfo> bestByKey =
                    new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);

                foreach (PackageInfo dep in allDeps)
                {
                    bool archOk = string.Equals(dep.Architecture, "neutral",
                                      StringComparison.OrdinalIgnoreCase);
                    if (!archOk)
                        foreach (string ca in checkedArchs)
                            if (string.Equals(dep.Architecture, ca,
                                    StringComparison.OrdinalIgnoreCase))
                            { archOk = true; break; }
                    if (!archOk) continue;

                    string key = DepBaseKey(dep);
                    PackageInfo existing;
                    if (!bestByKey.TryGetValue(key, out existing) ||
                        CompareVersions(dep.Version, existing.Version) > 0)
                        bestByKey[key] = dep;
                }

                foreach (PackageInfo dep in bestByKey.Values)
                    selectedFiles.Add(dep.FileName);
            }

            // Apply selections and update tooltips to explain the decision
            foreach (DataGridViewRow gr in depRows)
            {
                PackageGridRow row  = gr.Tag as PackageGridRow;
                bool           sel  = selectedFiles.Contains(row.Package.FileName);
                gr.Cells[ColCheck].Value = sel;

                string reason;
                if (sel)
                {
                    reason = "Required by the checked main package(s) manifest.";
                }
                else if (checkedMains.Count == 0)
                {
                    reason = "No main package checked.";
                }
                else
                {
                    // Explain why this dep was NOT selected
                    bool archMismatch = true;
                    foreach (PackageGridRow m in checkedMains)
                    {
                        bool neutral = string.Equals(m.Package.Architecture, "neutral",
                                           StringComparison.OrdinalIgnoreCase);
                        bool match   = neutral ||
                                       string.Equals(row.Package.Architecture,
                                           m.Package.Architecture,
                                           StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(row.Package.Architecture, "neutral",
                                           StringComparison.OrdinalIgnoreCase);
                        if (match) { archMismatch = false; break; }
                    }

                    if (archMismatch)
                        reason = "Architecture (" + row.Package.Architecture +
                                 ") does not match any checked main package.";
                    else if (hasManifestData)
                        reason = "Not declared as a dependency in the package manifest. " +
                                 "This package is available but not required by this app.";
                    else
                        reason = "Not matched by architecture/name heuristic.";
                }

                gr.Cells[ColKind].ToolTipText =
                    (sel ? "[Selected] " : "[Not required] ") +
                    row.Package.Architecture + "  v" + row.Package.Version +
                    " - " + reason;
            }

            // Log a summary so the user understands the selection in the output panel
            if (_logger != null && checkedMains.Count > 0)
            {
                int selCount  = selectedFiles.Count;
                int skipCount = depRows.Count - selCount;
                string mode   = hasManifestData ? "manifest" : "heuristic";
                _logger.Info("Dependency selection (" + mode + "): " +
                             selCount + " required and checked, " +
                             skipCount + " not declared as required (hover Kind column for details).");
            }
        }

        /// <summary>
        /// Extract the package identity name from a Store filename.
        /// Store filenames: PackageName_Version_Arch__Hash.ext
        /// The name is everything before the first _digit segment.
        /// e.g. "Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64__8wekyb3d8bbwe.appx"
        ///   -> "Microsoft.VCLibs.140.00.UWPDesktop"
        /// </summary>
        private static string ExtractPackageName(string fileName)
        {
            // Remove extension
            string name = fileName;
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);

            // Split on underscore; the first segment that starts with a digit
            // marks the beginning of the version
            string[] parts = name.Split('_');
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string part in parts)
            {
                if (part.Length > 0 && char.IsDigit(part[0])) break;
                // Skip the tilde separator used in bundle sub-packages
                if (part == "~") break;
                if (sb.Length > 0) sb.Append('_');
                sb.Append(part);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Grouping key for fallback dep matching: normalised base name + arch.
        /// Strips version numbers and hash suffixes from the filename.
        /// </summary>
        private static string DepBaseKey(PackageInfo dep)
        {
            string name = dep.FileName;
            int dotExt = name.LastIndexOf('.');
            if (dotExt > 0) name = name.Substring(0, dotExt);

            string[] parts = name.Split('_');
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string part in parts)
            {
                if (part.Length > 0 && char.IsDigit(part[0])) continue;
                if (part.Length > 16 && IsHex(part)) continue;
                if (IsArchToken(part)) continue;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(part);
            }
            return sb.ToString() + "|" + dep.Architecture.ToLowerInvariant();
        }

        private static bool IsHex(string s)
        {
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        private static bool IsArchToken(string s)
        {
            return string.Equals(s, "x64",     StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "x86",     StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "arm64",   StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "arm",     StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "neutral", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "amd64",   StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareVersions(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 0;
            if (string.IsNullOrEmpty(a)) return -1;
            if (string.IsNullOrEmpty(b)) return  1;

            string[] partsA = a.Split('.');
            string[] partsB = b.Split('.');
            int len = Math.Max(partsA.Length, partsB.Length);
            for (int i = 0; i < len; i++)
            {
                int numA = 0, numB = 0;
                if (i < partsA.Length) int.TryParse(partsA[i], out numA);
                if (i < partsB.Length) int.TryParse(partsB[i], out numB);
                if (numA != numB) return numA.CompareTo(numB);
            }
            return 0;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // TOOLBAR HANDLERS
        // ═══════════════════════════════════════════════════════════════════════

        private void TsBtnCopyUrl_Click(object sender, EventArgs e)
        {
            List<PackageGridRow> rows = GetCheckedRows();
            if (rows.Count == 0)
            {
                foreach (DataGridViewRow gr in _grid.SelectedRows)
                {
                    PackageGridRow row = gr.Tag as PackageGridRow;
                    if (row != null) rows.Add(row);
                }
            }
            if (rows.Count == 0)
            {
                MessageBox.Show("Select or check at least one row.", "No Selection",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<string> urls = new List<string>();
            foreach (PackageGridRow r in rows) urls.Add(r.DownloadUrl);
            Clipboard.SetText(string.Join(Environment.NewLine, urls.ToArray()));
            SetStatus("Copied " + rows.Count + " URL(s) to clipboard.");
        }

        private bool   _allSelected;
        private void TsBtnSelectAll_Click(object sender, EventArgs e)
        {
            _allSelected = !_allSelected;
            _updatingCheckboxes = true;
            try
            {
                foreach (DataGridViewRow gr in _grid.Rows)
                {
                    if (gr.Tag == null) continue;   // skip static label row
                    gr.Cells[ColCheck].Value = _allSelected;
                }
            }
            finally
            {
                _updatingCheckboxes = false;
            }
            // Sync deps after bulk toggle
            SyncDependencyCheckboxes();
            _grid.RefreshEdit();
        }

        private void TsFilterArch_Changed(object sender, EventArgs e)
        {
            // If a product is already loaded, re-run the search so all filtering,
            // manifest reading and dependency selection happen fresh for the new arch.
            bool hasUrl = _txtUrl.ForeColor != PlaceholderColor &&
                          !string.IsNullOrWhiteSpace(_txtUrl.Text);
            bool hasId  = _txtProductId.ForeColor != PlaceholderColor &&
                          !string.IsNullOrWhiteSpace(_txtProductId.Text);

            if ((hasUrl || hasId) && _rows.Count > 0)
            {
                BtnSearch_Click(this, EventArgs.Empty);
                return;
            }

            // No product loaded yet — just re-filter what is already in the grid
            if (_rows.Count == 0) return;
            string archFilter  = _tsFilterArch.SelectedItem != null
                                 ? _tsFilterArch.SelectedItem.ToString() : "All";
            bool   includeDeps = _chkIncludeDeps.Checked;

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (PackageGridRow row in _rows)
            {
                if (!includeDeps && row.Package.IsDependency) continue;
                if (archFilter != "All" &&
                    !string.Equals(row.Architecture, archFilter, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(row.Architecture, "neutral", StringComparison.OrdinalIgnoreCase))
                    continue;
                AddGridRow(row);
            }
            _grid.ResumeLayout();
        }

        private void TsBtnSaveLog_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title    = "Save Log";
                dlg.FileName = "MSStoreDownloader_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log";
                dlg.Filter   = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, _logBox.Text, System.Text.Encoding.UTF8);
                    _logger.Success("Log saved: " + dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not save log: " + ex.Message, "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // UI STATE
        // ═══════════════════════════════════════════════════════════════════════

        private void SetSearching(bool isSearching)
        {
            _btnSearch.Enabled       = !isSearching;
            _btnCancelSearch.Enabled =  isSearching;
            _txtUrl.Enabled          = !isSearching;
            _txtProductId.Enabled    = !isSearching;
            _statusProgress.Visible  =  isSearching;

            if (isSearching)
            {
                _statusProgress.Style = ProgressBarStyle.Marquee;
                SetStatus("Searching...");
            }
            else
            {
                _statusProgress.Style   = ProgressBarStyle.Blocks;
                _statusProgress.Visible = false;
            }
        }

        private void SetStatus(string message)
        {
            _statusLabel.Text = message;
        }

        private void AdjustInputLayout()
        {
            int available  = _inputPanel.Width - 10;
            int fixedWidth = 90 + 10 + 110 + 140 + 10 + 160 + 100 + 100;
            int urlWidth   = Math.Max(200, available - fixedWidth);
            _txtUrl.Width  = urlWidth;

            _lblOrId.Left       = _txtUrl.Right + 8;
            _txtProductId.Left  = _lblOrId.Right + 5;
            _btnSearch.Left     = _txtProductId.Right + 15;
            _btnCancelSearch.Left = _btnSearch.Right + 5;
        }

        private static string BuildFileFilter(string packageType)
        {
            string lower = packageType.ToLowerInvariant();
            if (lower.Contains("bundle"))
                return "Bundle packages (*.appxbundle;*.msixbundle)|*.appxbundle;*.msixbundle|All files (*.*)|*.*";
            if (lower.Contains("msix"))
                return "MSIX packages (*.msix)|*.msix|All files (*.*)|*.*";
            return "APPX packages (*.appx)|*.appx|All files (*.*)|*.*";
        }

        // ═══════════════════════════════════════════════════════════════════════
        // FORM CLOSE
        // ═══════════════════════════════════════════════════════════════════════

        // ── Installer script generation ────────────────────────────────────────

        /// <summary>
        /// Generates a PowerShell install script that installs dependencies first
        /// (runtimes and VCLibs) then the main application package(s).
        /// Named after the main app identity name.
        /// </summary>
        private void GenerateInstallerScript(List<PackageGridRow> rows, string folder)
        {
            try
            {
                // Separate main packages from dependencies
                List<PackageGridRow> mainPkgs = new List<PackageGridRow>();
                List<PackageGridRow> depPkgs  = new List<PackageGridRow>();
                foreach (PackageGridRow r in rows)
                {
                    if (r.Package.IsDependency) depPkgs.Add(r);
                    else                        mainPkgs.Add(r);
                }

                // Sort dependencies: runtimes and VCLibs before UI.Xaml before others
                depPkgs.Sort(delegate(PackageGridRow a, PackageGridRow b)
                {
                    return GetDepPriority(a.Package.FileName).CompareTo(
                           GetDepPriority(b.Package.FileName));
                });

                // Determine script name from first main package identity
                string scriptBaseName = "Install";
                if (mainPkgs.Count > 0)
                {
                    string pkgName = ExtractPackageName(mainPkgs[0].Package.FileName);
                    if (!string.IsNullOrEmpty(pkgName))
                        scriptBaseName = "Install_" + pkgName;
                }

                string scriptPath = Path.Combine(folder, scriptBaseName + ".ps1");

                System.Text.StringBuilder sb = new System.Text.StringBuilder();

                // Build script using a dollar-sign constant to avoid C# compiler issues
                string D = "$"; // PowerShell dollar sign

                // Header
                sb.AppendLine("# ============================================================");
                sb.AppendLine("# Installer script generated by MSStoreDownloader");
                sb.AppendLine("# Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("# Run with: PowerShell -ExecutionPolicy Bypass -File " + scriptBaseName + ".ps1");
                if (_chkSkipLicense.Checked)
                    sb.AppendLine("# Note: -AllowUnsigned flag is included (for unsigned/sideloaded packages)");
                if (_chkSkipLicense.Checked)
                    sb.AppendLine("# Note: -SkipLicense flag is included (use for sideloaded/developer packages)");
                sb.AppendLine("# ============================================================");
                sb.AppendLine();
                sb.AppendLine(D + "ErrorActionPreference = 'Stop'");
                sb.AppendLine(D + "ScriptDir = Split-Path -Parent " + D + "MyInvocation.MyCommand.Definition");
                sb.AppendLine();

                // Helper function
                sb.AppendLine("function Install-Package([string]" + D + "fileName) {");
                sb.AppendLine("    " + D + "path = Join-Path " + D + "ScriptDir " + D + "fileName");
                sb.AppendLine("    if (-not (Test-Path " + D + "path)) {");
                sb.AppendLine("        Write-Warning ('File not found: ' + " + D + "fileName + ' - skipping')");
                sb.AppendLine("        return");
                sb.AppendLine("    }");
                sb.AppendLine("    Write-Host ('Installing: ' + " + D + "fileName) -ForegroundColor Cyan");
                sb.AppendLine("    try {");
                string extraFlags = _chkSkipLicense.Checked ? " -AllowUnsigned" : "";
                sb.AppendLine("        Add-AppxPackage -Path " + D + "path -ForceApplicationShutdown" + extraFlags);
                sb.AppendLine("        Write-Host '  OK' -ForegroundColor Green");
                sb.AppendLine("    } catch {");
                sb.AppendLine("        Write-Warning ('  Failed: ' + " + D + "_.Exception.Message)");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine();

                // Dependencies first
                if (depPkgs.Count > 0)
                {
                    sb.AppendLine("# --- Dependencies (runtimes, VCLibs, frameworks) ---");
                    foreach (PackageGridRow dep in depPkgs)
                        sb.AppendLine("Install-Package '" + dep.FileName + "'");
                    sb.AppendLine();
                }

                // Main package(s) last
                if (mainPkgs.Count > 0)
                {
                    sb.AppendLine("# --- Main application ---");
                    foreach (PackageGridRow main in mainPkgs)
                        sb.AppendLine("Install-Package '" + main.FileName + "'");
                    sb.AppendLine();
                }

                sb.AppendLine("Write-Host 'Installation complete.' -ForegroundColor Green");
                sb.AppendLine("Read-Host 'Press Enter to exit'");
                File.WriteAllText(scriptPath, sb.ToString(), System.Text.Encoding.UTF8);
                _logger.Success("Installer script created: " + Path.GetFileName(scriptPath));
                SetStatus("Installer script saved: " + Path.GetFileName(scriptPath));
            }
            catch (Exception ex)
            {
                _logger.Warning("Could not create installer script: " + ex.Message);
            }
        }

        /// <summary>
        /// Install order priority (lower = install first).
        /// 0 = .NET Native runtime/framework
        /// 1 = VCLibs
        /// 2 = WindowsAppRuntime
        /// 3 = UI.Xaml
        /// 4 = other deps
        /// </summary>
        private static int GetDepPriority(string fileName)
        {
            string lower = fileName.ToLowerInvariant();
            if (lower.Contains("microsoft.net.native"))    return 0;
            if (lower.Contains("vclibs"))                  return 1;
            if (lower.Contains("windowsappruntime"))       return 2;
            if (lower.Contains("ui.xaml"))                 return 3;
            if (lower.Contains("services.store"))          return 4;
            return 5;
        }

        // ── Checkbox state persistence (registry) ─────────────────────────────

        private const string RegAppKey = "Software\\MSStoreDownloader";

        private static void SaveCheckboxStates(
            bool incDeps, bool clipboard, bool installer,
            bool skipLic, bool indFolders, bool debug)
        {
            try
            {
                Microsoft.Win32.RegistryKey k =
                    Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegAppKey);
                if (k == null) return;
                k.SetValue("IncludeDeps",      incDeps    ? 1 : 0);
                k.SetValue("ClipboardMonitor", clipboard  ? 1 : 0);
                k.SetValue("CreateInstaller",  installer  ? 1 : 0);
                k.SetValue("AllowUnsigned",    skipLic    ? 1 : 0);
                k.SetValue("IndivFolders",     indFolders ? 1 : 0);
                k.SetValue("DebugLog",         debug      ? 1 : 0);
                k.Close();
            }
            catch { }
        }

        private static bool LoadCheckboxState(string name, bool defaultVal)
        {
            try
            {
                Microsoft.Win32.RegistryKey k =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegAppKey);
                if (k == null) return defaultVal;
                object v = k.GetValue(name);
                k.Close();
                if (v != null) return (int)v != 0;
            }
            catch { }
            return defaultVal;
        }

        private void SaveAllCheckboxStates()
        {
            SaveCheckboxStates(
                _chkIncludeDeps.Checked,
                _chkClipboardMonitor.Checked,
                _chkCreateInstaller.Checked,
                _chkSkipLicense.Checked,
                _chkIndividualFolders.Checked,
                _chkDebugLog.Checked);
        }

        private void LoadAllCheckboxStates()
        {
            _chkIncludeDeps.Checked      = LoadCheckboxState("IncludeDeps",      true);
            _chkClipboardMonitor.Checked = LoadCheckboxState("ClipboardMonitor", true);
            _chkCreateInstaller.Checked  = LoadCheckboxState("CreateInstaller",  false);
            _chkSkipLicense.Checked      = LoadCheckboxState("AllowUnsigned",    false);
            _chkIndividualFolders.Checked = LoadCheckboxState("IndivFolders",    false);
            _chkDebugLog.Checked         = LoadCheckboxState("DebugLog",         false);
            // Apply debug state to logger
            if (_logger != null) _logger.DebugEnabled = _chkDebugLog.Checked;
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveAllCheckboxStates();
            if (_downloadManager != null && _downloadManager.ActiveDownloadCount > 0)
            {
                DialogResult ans = MessageBox.Show(
                    _downloadManager.ActiveDownloadCount + " download(s) are still in progress.\n" +
                    "Cancel them and exit?",
                    "Active Downloads", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans == DialogResult.No) { e.Cancel = true; return; }
                _downloadManager.CancelAll();
            }
            if (_queryCts != null) _queryCts.Cancel();
            if (_clipboardTimer != null) { _clipboardTimer.Stop(); _clipboardTimer.Dispose(); }
        }

        // ── Clipboard monitor ──────────────────────────────────────────────────

        private void ClipboardTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                string text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text) || text == _lastClipboard) return;
                _lastClipboard = text;
                text = text.Trim();

                // Match Store URLs or bare product IDs
                bool isId = ProductIdRx.IsMatch(text) && !text.Contains(" ") && !text.Contains(".");
                bool isUrl = StoreUrlRx.IsMatch(text);
                if (!isId && !isUrl) return;

                // Bring window to front
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                this.Activate();
                this.BringToFront();

                // Fill URL field and trigger search automatically
                string current = (_txtUrl.ForeColor == PlaceholderColor) ? "" : _txtUrl.Text.Trim();
                if (string.Equals(current, text, StringComparison.OrdinalIgnoreCase)) return;

                _txtUrl.ForeColor = Color.FromArgb(220, 220, 220);
                _txtUrl.Text      = text;
                _txtProductId.ForeColor = PlaceholderColor;
                _txtProductId.Text      = "9NBLGGH4NNS1";
                _logger.Info("Clipboard: Store URL detected, running search...");

                // Trigger search as if the user clicked the button
                BtnSearch_Click(this, EventArgs.Empty);
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════════════════


        // DARK THEME RENDERER
        // ═══════════════════════════════════════════════════════════════════════

        private class DarkToolStripRenderer : ToolStripProfessionalRenderer
        {
            public DarkToolStripRenderer() : base(new DarkColorTable()) { }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            private readonly Color _bg  = Color.FromArgb(35, 35, 38);
            private readonly Color _hl  = Color.FromArgb(55, 55, 65);
            private readonly Color _pr  = Color.FromArgb(0, 90, 160);

            public override Color ToolStripGradientBegin        { get { return _bg; } }
            public override Color ToolStripGradientMiddle       { get { return _bg; } }
            public override Color ToolStripGradientEnd          { get { return _bg; } }
            public override Color MenuStripGradientBegin        { get { return _bg; } }
            public override Color MenuStripGradientEnd          { get { return _bg; } }
            public override Color ButtonSelectedHighlight       { get { return _hl; } }
            public override Color ButtonSelectedHighlightBorder { get { return _hl; } }
            public override Color ButtonPressedHighlight        { get { return _pr; } }
            public override Color ButtonCheckedHighlight        { get { return _pr; } }
            public override Color ButtonSelectedBorder          { get { return _hl; } }
            public override Color SeparatorDark                 { get { return Color.FromArgb(55, 55, 60); } }
            public override Color SeparatorLight                { get { return Color.FromArgb(60, 60, 65); } }
        }
    }
}
