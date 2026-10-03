using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FastDiskUsage
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Fast Disk Usage could not start.\r\n\r\n" + ex.ToString(),
                    "Fast Disk Usage - startup error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class DirectoryNode
    {
        internal string Name;
        internal string FullPath;
        internal DirectoryNode Parent;
        internal long DirectBytes;
        internal long TotalBytes;
        internal long DirectFiles;
        internal long TotalFiles;
        internal long TotalFolders;
        internal int ErrorCode;
        internal bool IsReparsePoint;
        internal readonly List<DirectoryNode> Children = new List<DirectoryNode>();
    }

    internal sealed class ScanProgress
    {
        internal string CurrentPath;
        internal long Directories;
        internal long Files;
        internal long Bytes;
        internal long Errors;
    }

    internal sealed class ScanError
    {
        internal string Path;
        internal string Operation;
        internal int Code;
        internal string Message;
    }

    internal sealed class ErrorSnapshot
    {
        internal readonly List<ScanError> Entries;
        internal readonly long TotalCount;
        internal readonly bool Truncated;

        internal ErrorSnapshot(List<ScanError> entries, long totalCount, bool truncated)
        {
            Entries = entries;
            TotalCount = totalCount;
            Truncated = truncated;
        }
    }

    internal sealed class ErrorStore
    {
        private const int MaxEntries = 2000;
        private readonly object _sync = new object();
        private readonly List<ScanError> _entries = new List<ScanError>(MaxEntries);
        private long _totalCount;

        internal void Clear()
        {
            lock (_sync)
            {
                _entries.Clear();
                _totalCount = 0;
            }
        }

        internal void Add(string path, string operation, int code)
        {
            string message;
            try
            {
                message = new Win32Exception(code).Message;
            }
            catch
            {
                message = "Windows error " + code.ToString();
            }

            lock (_sync)
            {
                _totalCount++;
                if (_entries.Count < MaxEntries)
                {
                    ScanError item = new ScanError();
                    item.Path = path;
                    item.Operation = operation;
                    item.Code = code;
                    item.Message = message;
                    _entries.Add(item);
                }
            }
        }

        internal ErrorSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new ErrorSnapshot(new List<ScanError>(_entries), _totalCount, _totalCount > _entries.Count);
            }
        }

        internal long TotalCount
        {
            get
            {
                lock (_sync)
                    return _totalCount;
            }
        }
    }

    internal static class NativeMethods
    {
        internal const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
        internal const int FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        internal const int FIND_FIRST_EX_LARGE_FETCH = 0x2;
        internal const int ERROR_NO_MORE_FILES = 18;
        internal const int ERROR_NOT_SUPPORTED = 50;
        internal const int ERROR_INVALID_PARAMETER = 87;
        internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        internal enum FINDEX_INFO_LEVELS
        {
            FindExInfoStandard = 0,
            FindExInfoBasic = 1
        }

        internal enum FINDEX_SEARCH_OPS
        {
            FindExSearchNameMatch = 0
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WIN32_FIND_DATA
        {
            internal int dwFileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            internal uint nFileSizeHigh;
            internal uint nFileSizeLow;
            internal uint dwReserved0;
            internal uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            internal string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindFirstFileEx(
            string lpFileName,
            FINDEX_INFO_LEVELS fInfoLevelId,
            out WIN32_FIND_DATA lpFindFileData,
            FINDEX_SEARCH_OPS fSearchOp,
            IntPtr lpSearchFilter,
            int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindNextFile(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindClose(IntPtr hFindFile);
    }

    internal sealed class ScanFrame
    {
        internal readonly DirectoryNode Node;
        internal IntPtr Handle = NativeMethods.INVALID_HANDLE_VALUE;
        internal NativeMethods.WIN32_FIND_DATA Data;
        internal bool Started;
        internal bool HasData;

        internal ScanFrame(DirectoryNode node)
        {
            Node = node;
        }
    }

    internal static class Scanner
    {
        internal static DirectoryNode Scan(string rootPath, Func<bool> isCancelled, Action<ScanProgress> progress, Action<string, string, int> errorHandler)
        {
            DirectoryNode root = new DirectoryNode();
            root.FullPath = NormalizeDisplayPath(rootPath);
            root.Name = root.FullPath;

            Stack<ScanFrame> stack = new Stack<ScanFrame>(64);
            stack.Push(new ScanFrame(root));

            long dirs = 0;
            long files = 0;
            long bytes = 0;
            long errors = 0;
            long entriesSinceReport = 0;
            Stopwatch reportWatch = Stopwatch.StartNew();

            try
            {
                while (stack.Count != 0)
                {
                    if (isCancelled())
                        throw new OperationCanceledException();

                    ScanFrame frame = stack.Peek();
                if (!frame.Started)
                {
                    frame.Started = true;
                    dirs++;

                    string pattern = AddSearchWildcard(ToNativePath(frame.Node.FullPath));
                    NativeMethods.WIN32_FIND_DATA data;
                    IntPtr handle = NativeMethods.FindFirstFileEx(
                        pattern,
                        NativeMethods.FINDEX_INFO_LEVELS.FindExInfoBasic,
                        out data,
                        NativeMethods.FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                        IntPtr.Zero,
                        NativeMethods.FIND_FIRST_EX_LARGE_FETCH);

                    if (handle == NativeMethods.INVALID_HANDLE_VALUE)
                    {
                        int firstError = Marshal.GetLastWin32Error();
                        if (firstError == NativeMethods.ERROR_INVALID_PARAMETER || firstError == NativeMethods.ERROR_NOT_SUPPORTED)
                        {
                            handle = NativeMethods.FindFirstFileEx(
                                pattern,
                                NativeMethods.FINDEX_INFO_LEVELS.FindExInfoBasic,
                                out data,
                                NativeMethods.FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                                IntPtr.Zero,
                                0);
                        }
                    }

                    if (handle == NativeMethods.INVALID_HANDLE_VALUE)
                    {
                        int finalError = Marshal.GetLastWin32Error();
                        frame.Node.ErrorCode = finalError;
                        errors++;
                        ReportError(errorHandler, frame.Node.FullPath, "Open directory", finalError);
                        stack.Pop();
                        FinalizeNode(frame.Node);
                        ReportMaybe(progress, reportWatch, ref entriesSinceReport, frame.Node.FullPath, dirs, files, bytes, errors, true);
                        continue;
                    }

                    frame.Handle = handle;
                    frame.Data = data;
                    frame.HasData = true;
                }

                if (!frame.HasData)
                {
                    if (frame.Handle != NativeMethods.INVALID_HANDLE_VALUE)
                    {
                        NativeMethods.FindClose(frame.Handle);
                        frame.Handle = NativeMethods.INVALID_HANDLE_VALUE;
                    }
                    stack.Pop();
                    FinalizeNode(frame.Node);
                    ReportMaybe(progress, reportWatch, ref entriesSinceReport, frame.Node.FullPath, dirs, files, bytes, errors, false);
                    continue;
                }

                NativeMethods.WIN32_FIND_DATA current = frame.Data;
                NativeMethods.WIN32_FIND_DATA next;
                if (NativeMethods.FindNextFile(frame.Handle, out next))
                {
                    frame.Data = next;
                    frame.HasData = true;
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    frame.HasData = false;
                    if (err != NativeMethods.ERROR_NO_MORE_FILES)
                    {
                        frame.Node.ErrorCode = err;
                        errors++;
                        ReportError(errorHandler, frame.Node.FullPath, "Enumerate directory", err);
                    }
                }

                string name = current.cFileName;
                if (name == "." || name == "..")
                    continue;

                entriesSinceReport++;
                bool isDirectory = (current.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0;
                bool isReparse = (current.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

                if (isDirectory)
                {
                    DirectoryNode child = new DirectoryNode();
                    child.Name = name;
                    child.FullPath = CombinePath(frame.Node.FullPath, name);
                    child.Parent = frame.Node;
                    child.IsReparsePoint = isReparse;
                    frame.Node.Children.Add(child);

                    if (!isReparse)
                    {
                        stack.Push(new ScanFrame(child));
                    }
                }
                else
                {
                    long size = ((long)current.nFileSizeHigh << 32) | current.nFileSizeLow;
                    frame.Node.DirectBytes += size;
                    frame.Node.DirectFiles++;
                    files++;
                    bytes += size;
                }

                    ReportMaybe(progress, reportWatch, ref entriesSinceReport, frame.Node.FullPath, dirs, files, bytes, errors, false);
                }
            }
            finally
            {
                while (stack.Count != 0)
                {
                    ScanFrame open = stack.Pop();
                    if (open.Handle != NativeMethods.INVALID_HANDLE_VALUE)
                    {
                        NativeMethods.FindClose(open.Handle);
                        open.Handle = NativeMethods.INVALID_HANDLE_VALUE;
                    }
                }
            }

            if (progress != null)
            {
                ScanProgress p = new ScanProgress();
                p.CurrentPath = root.FullPath;
                p.Directories = dirs;
                p.Files = files;
                p.Bytes = bytes;
                p.Errors = errors;
                progress(p);
            }
            return root;
        }

        private static void FinalizeNode(DirectoryNode node)
        {
            long totalBytes = node.DirectBytes;
            long totalFiles = node.DirectFiles;
            long totalFolders = 0;

            for (int i = 0; i < node.Children.Count; i++)
            {
                DirectoryNode child = node.Children[i];
                if (!child.IsReparsePoint)
                {
                    totalBytes += child.TotalBytes;
                    totalFiles += child.TotalFiles;
                    totalFolders += 1 + child.TotalFolders;
                }
            }

            node.TotalBytes = totalBytes;
            node.TotalFiles = totalFiles;
            node.TotalFolders = totalFolders;
        }

        private static void ReportError(Action<string, string, int> errorHandler, string path, string operation, int code)
        {
            if (errorHandler != null)
                errorHandler(path, operation, code);
        }

        private static void ReportMaybe(Action<ScanProgress> progress, Stopwatch watch, ref long entries, string path,
            long dirs, long files, long bytes, long errors, bool force)
        {
            if (progress == null)
                return;
            if (!force && entries < 2048 && watch.ElapsedMilliseconds < 150)
                return;

            entries = 0;
            watch.Restart();
            ScanProgress p = new ScanProgress();
            p.CurrentPath = path;
            p.Directories = dirs;
            p.Files = files;
            p.Bytes = bytes;
            p.Errors = errors;
            progress(p);
        }

        private static string NormalizeDisplayPath(string path)
        {
            string full = Path.GetFullPath(path.Trim());
            string root = Path.GetPathRoot(full);
            if (full.Length > root.Length)
                full = full.TrimEnd('\\');
            return full;
        }

        private static string CombinePath(string parent, string child)
        {
            if (parent.EndsWith("\\", StringComparison.Ordinal))
                return parent + child;
            return parent + "\\" + child;
        }

        private static string AddSearchWildcard(string path)
        {
            if (path.EndsWith("\\", StringComparison.Ordinal))
                return path + "*";
            return path + "\\*";
        }

        private static string ToNativePath(string path)
        {
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
                return path;
            if (path.StartsWith("\\\\", StringComparison.Ordinal))
                return "\\\\?\\UNC\\" + path.Substring(2);
            return "\\\\?\\" + path;
        }
    }

    internal sealed class ErrorDetailsForm : Form
    {
        private readonly Func<ErrorSnapshot> _snapshotProvider;
        private readonly ListView _list;
        private readonly Label _summary;

        internal ErrorDetailsForm(Func<ErrorSnapshot> snapshotProvider)
        {
            _snapshotProvider = snapshotProvider;
            Text = "Fast Disk Usage - scan errors";
            StartPosition = FormStartPosition.CenterParent;
            Width = 940;
            Height = 500;
            MinimumSize = new Size(650, 320);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            _summary = new Label();
            _summary.Dock = DockStyle.Top;
            _summary.Height = 30;
            _summary.Padding = new Padding(8, 7, 8, 4);

            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.HideSelection = false;
            _list.Columns.Add("Code", 70, HorizontalAlignment.Right);
            _list.Columns.Add("Operation", 125, HorizontalAlignment.Left);
            _list.Columns.Add("Message", 230, HorizontalAlignment.Left);
            _list.Columns.Add("Path", 470, HorizontalAlignment.Left);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 42;
            buttons.Padding = new Padding(6, 6, 6, 4);
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;

            Button refresh = new Button();
            refresh.Text = "Refresh";
            refresh.AutoSize = true;
            refresh.Click += delegate { RefreshErrors(); };

            Button copy = new Button();
            copy.Text = "Copy All";
            copy.AutoSize = true;
            copy.Click += CopyAllClick;

            Button save = new Button();
            save.Text = "Save Log...";
            save.AutoSize = true;
            save.Click += SaveLogClick;

            Button close = new Button();
            close.Text = "Close";
            close.AutoSize = true;
            close.Click += delegate { Close(); };

            buttons.Controls.Add(refresh);
            buttons.Controls.Add(copy);
            buttons.Controls.Add(save);
            buttons.Controls.Add(close);

            Controls.Add(_list);
            Controls.Add(_summary);
            Controls.Add(buttons);

            Shown += delegate { RefreshErrors(); };
        }

        internal void RefreshErrors()
        {
            ErrorSnapshot snapshot = _snapshotProvider == null ? null : _snapshotProvider();
            if (snapshot == null)
                return;

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                for (int i = 0; i < snapshot.Entries.Count; i++)
                {
                    ScanError error = snapshot.Entries[i];
                    ListViewItem item = new ListViewItem(error.Code.ToString());
                    item.SubItems.Add(error.Operation ?? String.Empty);
                    item.SubItems.Add(error.Message ?? String.Empty);
                    item.SubItems.Add(error.Path ?? String.Empty);
                    _list.Items.Add(item);
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            if (snapshot.Truncated)
            {
                _summary.Text = String.Format(
                    "{0:N0} errors occurred. Showing the first {1:N0} to keep memory use bounded.",
                    snapshot.TotalCount, snapshot.Entries.Count);
            }
            else
            {
                _summary.Text = String.Format("{0:N0} scan errors.", snapshot.TotalCount);
            }
        }

        private void CopyAllClick(object sender, EventArgs e)
        {
            try
            {
                Clipboard.SetText(BuildLogText());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Copy failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SaveLogClick(object sender, EventArgs e)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Save scan error log";
                dlg.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                dlg.FileName = "FastDiskUsage-errors.txt";
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                try
                {
                    File.WriteAllText(dlg.FileName, BuildLogText(), System.Text.Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private string BuildLogText()
        {
            ErrorSnapshot snapshot = _snapshotProvider == null ? null : _snapshotProvider();
            if (snapshot == null)
                return String.Empty;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Fast Disk Usage - scan errors");
            sb.AppendLine("Code\tOperation\tMessage\tPath");
            for (int i = 0; i < snapshot.Entries.Count; i++)
            {
                ScanError error = snapshot.Entries[i];
                sb.Append(error.Code).Append('\t')
                    .Append(error.Operation).Append('\t')
                    .Append(error.Message).Append('\t')
                    .AppendLine(error.Path);
            }
            if (snapshot.Truncated)
            {
                sb.AppendLine();
                sb.AppendFormat("NOTE: {0:N0} total errors occurred; only the first {1:N0} were retained.\r\n",
                    snapshot.TotalCount, snapshot.Entries.Count);
            }
            return sb.ToString();
        }
    }


    internal sealed class TreemapControl : Control
    {
        private sealed class TreemapCell
        {
            internal Rectangle Bounds;
            internal DirectoryNode Node;
            internal string Label;
            internal long Bytes;
        }

        private sealed class TreemapItem
        {
            internal DirectoryNode Node;
            internal string Label;
            internal long Bytes;
        }

        private static readonly Color[] Palette = new Color[]
        {
            Color.FromArgb(52, 120, 246), Color.FromArgb(24, 151, 111),
            Color.FromArgb(220, 102, 42), Color.FromArgb(150, 84, 196),
            Color.FromArgb(194, 58, 82), Color.FromArgb(33, 145, 177),
            Color.FromArgb(181, 139, 28), Color.FromArgb(88, 109, 185),
            Color.FromArgb(71, 145, 65), Color.FromArgb(199, 79, 146)
        };

        private readonly List<TreemapCell> _cells = new List<TreemapCell>();
        private readonly ToolTip _toolTip = new ToolTip();
        private DirectoryNode _root;
        private TreemapCell _hoverCell;
        private DirectoryNode _contextNode;
        private Size _layoutSize;

        internal event Action<DirectoryNode> NodeActivated;

        internal TreemapControl()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = SystemColors.Window;
            TabStop = true;
            _toolTip.AutoPopDelay = 10000;
            _toolTip.InitialDelay = 350;
            _toolTip.ReshowDelay = 100;
        }

        internal DirectoryNode Root
        {
            get { return _root; }
            set
            {
                if (Object.ReferenceEquals(_root, value))
                    return;
                _root = value;
                _hoverCell = null;
                _contextNode = null;
                _layoutSize = Size.Empty;
                _cells.Clear();
                _toolTip.SetToolTip(this, String.Empty);
                Invalidate();
            }
        }

        internal DirectoryNode ContextNode
        {
            get { return _contextNode; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Rectangle area = ClientRectangle;
            if (area.Width <= 2 || area.Height <= 2)
                return;

            if (_root == null)
            {
                TextRenderer.DrawText(e.Graphics, "Treemap appears after a scan completes.", Font,
                    area, SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            if (_layoutSize != ClientSize)
                BuildLayout();

            e.Graphics.Clear(SystemColors.Window);
            if (_cells.Count == 0)
            {
                TextRenderer.DrawText(e.Graphics, "No sized subfolders or direct files in this folder.", Font,
                    area, SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            for (int i = 0; i < _cells.Count; i++)
            {
                TreemapCell cell = _cells[i];
                if (cell.Bounds.Width <= 0 || cell.Bounds.Height <= 0)
                    continue;

                Color fill = ColorForCell(cell);
                using (SolidBrush brush = new SolidBrush(fill))
                    e.Graphics.FillRectangle(brush, cell.Bounds);
                e.Graphics.DrawRectangle(SystemPens.WindowFrame, cell.Bounds.X, cell.Bounds.Y,
                    Math.Max(0, cell.Bounds.Width - 1), Math.Max(0, cell.Bounds.Height - 1));

                if (cell.Bounds.Width >= 75 && cell.Bounds.Height >= 28)
                {
                    Rectangle textRect = Rectangle.Inflate(cell.Bounds, -4, -3);
                    if (textRect.Width > 4 && textRect.Height > 4)
                    {
                        Color textColor = (fill.R * 299 + fill.G * 587 + fill.B * 114) / 1000 < 145
                            ? Color.White : Color.Black;
                        string text = cell.Label + "\r\n" + FormatBytesLocal(cell.Bytes);
                        TextRenderer.DrawText(e.Graphics, text, Font, textRect, textColor,
                            TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    }
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            TreemapCell cell = HitTestCell(e.Location);
            if (!Object.ReferenceEquals(cell, _hoverCell))
            {
                _hoverCell = cell;
                if (cell == null)
                {
                    _toolTip.SetToolTip(this, String.Empty);
                }
                else
                {
                    string path = cell.Node == null ? _root.FullPath : cell.Node.FullPath;
                    string tip = cell.Label + "\r\n" + FormatBytesLocal(cell.Bytes) + "\r\n" + path;
                    _toolTip.SetToolTip(this, tip);
                }
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverCell = null;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            TreemapCell cell = HitTestCell(e.Location);
            _contextNode = cell == null ? null : cell.Node;
            base.OnMouseDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left)
                return;
            TreemapCell cell = HitTestCell(e.Location);
            if (cell != null && cell.Node != null && NodeActivated != null)
                NodeActivated(cell.Node);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _toolTip.Dispose();
            base.Dispose(disposing);
        }

        private void BuildLayout()
        {
            _cells.Clear();
            _layoutSize = ClientSize;
            if (_root == null || ClientSize.Width < 4 || ClientSize.Height < 4)
                return;

            List<TreemapItem> items = new List<TreemapItem>(_root.Children.Count + 1);
            for (int i = 0; i < _root.Children.Count; i++)
            {
                DirectoryNode child = _root.Children[i];
                if (child.TotalBytes <= 0)
                    continue;
                TreemapItem item = new TreemapItem();
                item.Node = child;
                item.Label = child.Name;
                item.Bytes = child.TotalBytes;
                items.Add(item);
            }

            if (_root.DirectBytes > 0)
            {
                TreemapItem direct = new TreemapItem();
                direct.Node = null;
                direct.Label = "[direct files]";
                direct.Bytes = _root.DirectBytes;
                items.Add(direct);
            }

            items.Sort(delegate(TreemapItem a, TreemapItem b)
            {
                int bySize = b.Bytes.CompareTo(a.Bytes);
                if (bySize != 0)
                    return bySize;
                return StringComparer.CurrentCultureIgnoreCase.Compare(a.Label, b.Label);
            });

            long total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Bytes > 0 && total <= Int64.MaxValue - items[i].Bytes)
                    total += items[i].Bytes;
                else if (items[i].Bytes > 0)
                    total = Int64.MaxValue;
            }
            if (total <= 0)
                return;

            RectangleF rect = new RectangleF(1, 1, Math.Max(1, ClientSize.Width - 2), Math.Max(1, ClientSize.Height - 2));
            LayoutRange(items, 0, items.Count, rect, total);
        }

        private void LayoutRange(List<TreemapItem> items, int start, int count, RectangleF rect, long total)
        {
            if (count <= 0 || total <= 0 || rect.Width < 1F || rect.Height < 1F)
                return;

            if (count == 1)
            {
                Rectangle bounds = Rectangle.Round(rect);
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    TreemapCell cell = new TreemapCell();
                    cell.Bounds = bounds;
                    cell.Node = items[start].Node;
                    cell.Label = items[start].Label;
                    cell.Bytes = items[start].Bytes;
                    _cells.Add(cell);
                }
                return;
            }

            long half = total / 2;
            long firstTotal = 0;
            int firstCount = 0;
            for (int i = 0; i < count - 1; i++)
            {
                long next = items[start + i].Bytes;
                if (firstCount > 0 && Math.Abs((double)half - firstTotal) <= Math.Abs((double)half - (firstTotal + next)))
                    break;
                firstTotal += next;
                firstCount++;
            }
            if (firstCount <= 0)
            {
                firstCount = 1;
                firstTotal = items[start].Bytes;
            }
            if (firstCount >= count)
            {
                firstCount = count - 1;
                firstTotal = 0;
                for (int i = 0; i < firstCount; i++)
                    firstTotal += items[start + i].Bytes;
            }

            long secondTotal = total - firstTotal;
            float ratio = total <= 0 ? 0.5F : (float)((double)firstTotal / (double)total);
            ratio = Math.Max(0.02F, Math.Min(0.98F, ratio));

            RectangleF firstRect;
            RectangleF secondRect;
            if (rect.Width >= rect.Height)
            {
                float firstWidth = rect.Width * ratio;
                firstRect = new RectangleF(rect.X, rect.Y, firstWidth, rect.Height);
                secondRect = new RectangleF(rect.X + firstWidth, rect.Y, rect.Width - firstWidth, rect.Height);
            }
            else
            {
                float firstHeight = rect.Height * ratio;
                firstRect = new RectangleF(rect.X, rect.Y, rect.Width, firstHeight);
                secondRect = new RectangleF(rect.X, rect.Y + firstHeight, rect.Width, rect.Height - firstHeight);
            }

            LayoutRange(items, start, firstCount, firstRect, firstTotal);
            LayoutRange(items, start + firstCount, count - firstCount, secondRect, secondTotal);
        }

        private TreemapCell HitTestCell(Point point)
        {
            if (_layoutSize != ClientSize)
                BuildLayout();
            for (int i = 0; i < _cells.Count; i++)
            {
                if (_cells[i].Bounds.Contains(point))
                    return _cells[i];
            }
            return null;
        }

        private static Color ColorForCell(TreemapCell cell)
        {
            if (cell.Node == null)
                return Color.FromArgb(112, 112, 112);

            int hash = 17;
            string text = cell.Node.FullPath ?? cell.Label ?? String.Empty;
            for (int i = 0; i < text.Length; i++)
                hash = unchecked(hash * 31 + text[i]);
            hash &= 0x7fffffff;

            return Palette[hash % Palette.Length];
        }

        private static string FormatBytesLocal(long value)
        {
            double size = value;
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB", "PB" };
            int u = 0;
            while (size >= 1024.0 && u < units.Length - 1)
            {
                size /= 1024.0;
                u++;
            }
            if (u == 0)
                return value.ToString("N0") + " B";
            return size.ToString(size >= 100 ? "0" : (size >= 10 ? "0.0" : "0.00")) + " " + units[u];
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly TextBox _pathBox;
        private readonly Button _browseButton;
        private readonly Button _scanButton;
        private readonly Button _cancelButton;
        private readonly Button _upButton;
        private readonly TreeView _tree;
        private readonly ListView _list;
        private readonly TreemapControl _treemap;
        private readonly StatusStrip _statusStrip;
        private readonly ToolStripStatusLabel _status;
        private readonly ToolStripStatusLabel _stats;
        private readonly ToolStripStatusLabel _errorsStatus;
        private readonly ToolStripProgressBar _progress;
        private readonly ContextMenuStrip _folderMenu;
        private readonly ToolStripMenuItem _openInExplorerItem;
        private readonly ErrorStore _errorStore = new ErrorStore();

        private volatile bool _cancelRequested;
        private DirectoryNode _root;
        private DirectoryNode _current;
        private Thread _scanThread;
        private Stopwatch _scanWatch;
        private ErrorDetailsForm _errorDetailsForm;
        private DirectoryNode _contextFolder;

        internal MainForm()
        {
            Text = "Fast Disk Usage";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 1060;
            Height = 700;
            MinimumSize = new Size(760, 480);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            TableLayoutPanel top = new TableLayoutPanel();
            top.Dock = DockStyle.Top;
            top.Height = 39;
            top.Padding = new Padding(6, 6, 6, 4);
            top.ColumnCount = 5;
            top.RowCount = 1;
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _pathBox = new TextBox();
            _pathBox.Dock = DockStyle.Fill;
            _pathBox.Margin = new Padding(0, 0, 6, 0);
            _pathBox.Text = Path.GetPathRoot(Environment.SystemDirectory);
            _pathBox.KeyDown += PathBoxKeyDown;

            _browseButton = new Button();
            _browseButton.Text = "Browse...";
            _browseButton.AutoSize = true;
            _browseButton.Click += BrowseClick;

            _scanButton = new Button();
            _scanButton.Text = "Scan";
            _scanButton.AutoSize = true;
            _scanButton.Click += ScanClick;

            _cancelButton = new Button();
            _cancelButton.Text = "Cancel";
            _cancelButton.AutoSize = true;
            _cancelButton.Enabled = false;
            _cancelButton.Click += delegate { _cancelRequested = true; _status.Text = "Cancelling..."; };

            _upButton = new Button();
            _upButton.Text = "Up";
            _upButton.AutoSize = true;
            _upButton.Enabled = false;
            _upButton.Click += UpClick;

            top.Controls.Add(_pathBox, 0, 0);
            top.Controls.Add(_browseButton, 1, 0);
            top.Controls.Add(_scanButton, 2, 0);
            top.Controls.Add(_cancelButton, 3, 0);
            top.Controls.Add(_upButton, 4, 0);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            // Do not set SplitterDistance or Panel1/Panel2 minimum sizes here.
            // WinForms validates all three against the SplitContainer's current
            // width, which is still tiny before the first real layout pass.

            _tree = new TreeView();
            _tree.Dock = DockStyle.Fill;
            _tree.HideSelection = false;
            _tree.BeforeExpand += TreeBeforeExpand;
            _tree.AfterSelect += TreeAfterSelect;
            _tree.NodeMouseClick += TreeNodeMouseClick;
            split.Panel1.Controls.Add(_tree);

            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.GridLines = false;
            _list.HideSelection = false;
            _list.Columns.Add("Folder", 330, HorizontalAlignment.Left);
            _list.Columns.Add("Size", 100, HorizontalAlignment.Right);
            _list.Columns.Add("%", 65, HorizontalAlignment.Right);
            _list.Columns.Add("Files", 90, HorizontalAlignment.Right);
            _list.Columns.Add("Folders", 80, HorizontalAlignment.Right);
            _list.Columns.Add("Status", 130, HorizontalAlignment.Left);
            _list.DoubleClick += ListDoubleClick;
            _list.MouseDown += ListMouseDown;

            TableLayoutPanel rightLayout = new TableLayoutPanel();
            rightLayout.Dock = DockStyle.Fill;
            rightLayout.Margin = new Padding(0);
            rightLayout.Padding = new Padding(0);
            rightLayout.ColumnCount = 1;
            rightLayout.RowCount = 2;
            rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 64F));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 36F));
            rightLayout.Controls.Add(_list, 0, 0);

            Panel treemapPanel = new Panel();
            treemapPanel.Dock = DockStyle.Fill;
            treemapPanel.Padding = new Padding(0, 0, 0, 0);

            Label treemapHeader = new Label();
            treemapHeader.Dock = DockStyle.Top;
            treemapHeader.Height = 24;
            treemapHeader.Padding = new Padding(5, 4, 0, 0);
            treemapHeader.Text = "Treemap - area = folder size; double-click to drill in";
            treemapHeader.BackColor = SystemColors.Control;

            _treemap = new TreemapControl();
            _treemap.Dock = DockStyle.Fill;
            _treemap.NodeActivated += delegate(DirectoryNode node) { NavigateTo(node); };

            treemapPanel.Controls.Add(_treemap);
            treemapPanel.Controls.Add(treemapHeader);
            rightLayout.Controls.Add(treemapPanel, 0, 1);
            split.Panel2.Controls.Add(rightLayout);

            _folderMenu = new ContextMenuStrip();
            _openInExplorerItem = new ToolStripMenuItem("Open in File Explorer");
            _openInExplorerItem.Click += OpenFolderInExplorerClick;
            _folderMenu.Items.Add(_openInExplorerItem);
            _folderMenu.Opening += FolderMenuOpening;
            _tree.ContextMenuStrip = _folderMenu;
            _list.ContextMenuStrip = _folderMenu;
            _treemap.ContextMenuStrip = _folderMenu;

            _statusStrip = new StatusStrip();
            _status = new ToolStripStatusLabel();
            _status.Spring = true;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Text = "Ready";
            _stats = new ToolStripStatusLabel();
            _errorsStatus = new ToolStripStatusLabel();
            _errorsStatus.Text = "Errors: 0";
            _errorsStatus.IsLink = true;
            _errorsStatus.Enabled = false;
            _errorsStatus.ToolTipText = "Click to view scan errors";
            _errorsStatus.Click += ErrorsStatusClick;
            _progress = new ToolStripProgressBar();
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 30;
            _progress.Visible = false;
            _progress.Width = 110;
            _statusStrip.Items.Add(_status);
            _statusStrip.Items.Add(_stats);
            _statusStrip.Items.Add(_errorsStatus);
            _statusStrip.Items.Add(_progress);

            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(_statusStrip);
            AcceptButton = _scanButton;

            Shown += delegate
            {
                // The real client width is known now. Keep generous room on both
                // sides without relying on Panel1MinSize/Panel2MinSize setters.
                int width = split.ClientSize.Width;
                if (width > 360)
                {
                    int desired = 330;
                    int minimumLeft = 140;
                    int minimumRight = 180;
                    int max = width - minimumRight - split.SplitterWidth;
                    if (max >= minimumLeft)
                        split.SplitterDistance = Math.Max(minimumLeft, Math.Min(desired, max));
                }
                _status.Text = "Ready - choose a folder and click Scan";
            };
        }

        private void PathBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && _scanButton.Enabled)
            {
                StartScan();
                e.SuppressKeyPress = true;
            }
        }

        private void BrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Choose a folder to scan";
                dlg.ShowNewFolderButton = false;
                if (Directory.Exists(_pathBox.Text))
                    dlg.SelectedPath = _pathBox.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _pathBox.Text = dlg.SelectedPath;
            }
        }

        private void ScanClick(object sender, EventArgs e)
        {
            StartScan();
        }

        private void StartScan()
        {
            if (_scanThread != null && _scanThread.IsAlive)
                return;

            string path = _pathBox.Text.Trim();
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Invalid path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, "Folder not found or not accessible:\r\n" + path, "Fast Disk Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _pathBox.Text = path;
            _cancelRequested = false;
            _root = null;
            _current = null;
            _tree.Nodes.Clear();
            _list.Items.Clear();
            _treemap.Root = null;
            _scanWatch = Stopwatch.StartNew();
            _errorStore.Clear();
            UpdateErrorStatus(0);
            if (_errorDetailsForm != null && !_errorDetailsForm.IsDisposed)
                _errorDetailsForm.RefreshErrors();
            SetScanningUi(true);
            _status.Text = "Scanning " + path;
            _stats.Text = String.Empty;

            string scanPath = path;
            _scanThread = new Thread(delegate()
            {
                try
                {
                    DirectoryNode result = Scanner.Scan(scanPath,
                        delegate { return _cancelRequested; },
                        ReportProgressFromWorker,
                        delegate(string errorPath, string operation, int code)
                        {
                            _errorStore.Add(errorPath, operation, code);
                        });
                    BeginInvoke((MethodInvoker)delegate { ScanCompleted(result, null); });
                }
                catch (OperationCanceledException)
                {
                    BeginInvoke((MethodInvoker)delegate { ScanCompleted(null, "Cancelled"); });
                }
                catch (Exception ex)
                {
                    string message = ex.Message;
                    BeginInvoke((MethodInvoker)delegate { ScanCompleted(null, message); });
                }
            });
            _scanThread.IsBackground = true;
            _scanThread.Name = "FastDiskUsage scanner";
            _scanThread.Start();
        }

        private void ReportProgressFromWorker(ScanProgress p)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!_progress.Visible)
                        return;
                    _status.Text = CompactPath(p.CurrentPath, 95);
                    _stats.Text = String.Format("{0:N0} dirs   {1:N0} files   {2}",
                        p.Directories, p.Files, FormatBytes(p.Bytes));
                    UpdateErrorStatus(p.Errors);
                });
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void ScanCompleted(DirectoryNode result, string error)
        {
            if (_scanWatch != null)
                _scanWatch.Stop();
            _scanThread = null;
            SetScanningUi(false);

            UpdateErrorStatus(_errorStore.TotalCount);

            if (result == null)
            {
                _status.Text = error == "Cancelled" ? "Cancelled" : "Scan failed: " + error;
                _stats.Text = String.Empty;
                return;
            }

            _root = result;
            _current = result;
            TreeNode rootNode = MakeTreeNode(result);
            _tree.Nodes.Add(rootNode);
            _tree.SelectedNode = rootNode;
            rootNode.Expand();
            ShowDirectory(result);

            string elapsed = _scanWatch == null ? "" : FormatElapsed(_scanWatch.Elapsed);
            _status.Text = "Complete: " + result.FullPath;
            _stats.Text = String.Format("{0}   {1:N0} files   {2:N0} folders   {3}",
                FormatBytes(result.TotalBytes), result.TotalFiles, result.TotalFolders, elapsed);
        }

        private void UpdateErrorStatus(long count)
        {
            _errorsStatus.Text = "Errors: " + count.ToString("N0");
            _errorsStatus.Enabled = count > 0;
        }

        private void ErrorsStatusClick(object sender, EventArgs e)
        {
            if (_errorStore.TotalCount == 0)
                return;

            if (_errorDetailsForm == null || _errorDetailsForm.IsDisposed)
            {
                _errorDetailsForm = new ErrorDetailsForm(delegate { return _errorStore.Snapshot(); });
                _errorDetailsForm.FormClosed += delegate { _errorDetailsForm = null; };
                _errorDetailsForm.Show(this);
            }
            else
            {
                _errorDetailsForm.RefreshErrors();
                if (_errorDetailsForm.WindowState == FormWindowState.Minimized)
                    _errorDetailsForm.WindowState = FormWindowState.Normal;
                _errorDetailsForm.BringToFront();
            }
        }

        private void SetScanningUi(bool scanning)
        {
            _browseButton.Enabled = !scanning;
            _scanButton.Enabled = !scanning;
            _pathBox.Enabled = !scanning;
            _cancelButton.Enabled = scanning;
            _upButton.Enabled = !scanning && _current != null && _current != _root;
            _progress.Visible = scanning;
        }

        private TreeNode MakeTreeNode(DirectoryNode node)
        {
            string text = node.Name + "  [" + FormatBytes(node.TotalBytes) + "]";
            TreeNode tn = new TreeNode(text);
            tn.Tag = node;
            if (node.Children.Count != 0)
                tn.Nodes.Add(new TreeNode("..."));
            return tn;
        }

        private void TreeBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            MaterializeTreeChildren(e.Node);
        }

        private void MaterializeTreeChildren(TreeNode treeNode)
        {
            if (treeNode.Nodes.Count == 1 && treeNode.Nodes[0].Tag == null)
            {
                DirectoryNode node = treeNode.Tag as DirectoryNode;
                treeNode.Nodes.Clear();
                if (node == null)
                    return;

                List<DirectoryNode> children = new List<DirectoryNode>(node.Children);
                children.Sort(CompareNodesBySize);
                for (int i = 0; i < children.Count; i++)
                    treeNode.Nodes.Add(MakeTreeNode(children[i]));
            }
        }

        private void TreeAfterSelect(object sender, TreeViewEventArgs e)
        {
            DirectoryNode node = e.Node.Tag as DirectoryNode;
            if (node != null)
                ShowDirectory(node);
        }

        private void ListDoubleClick(object sender, EventArgs e)
        {
            if (_list.SelectedItems.Count == 0)
                return;
            DirectoryNode node = _list.SelectedItems[0].Tag as DirectoryNode;
            if (node != null)
                NavigateTo(node);
        }

        private void TreeNodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.Node != null)
                _tree.SelectedNode = e.Node;
        }

        private void ListMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
                return;

            ListViewItem item = _list.GetItemAt(e.X, e.Y);
            if (item == null)
            {
                _list.SelectedItems.Clear();
                return;
            }

            item.Selected = true;
            item.Focused = true;
        }

        private void FolderMenuOpening(object sender, CancelEventArgs e)
        {
            _contextFolder = null;

            if (_folderMenu.SourceControl == _tree)
            {
                if (_tree.SelectedNode != null)
                    _contextFolder = _tree.SelectedNode.Tag as DirectoryNode;
            }
            else if (_folderMenu.SourceControl == _list)
            {
                if (_list.SelectedItems.Count != 0)
                    _contextFolder = _list.SelectedItems[0].Tag as DirectoryNode;
            }
            else if (_folderMenu.SourceControl == _treemap)
            {
                _contextFolder = _treemap.ContextNode;
            }

            if (_contextFolder == null)
            {
                e.Cancel = true;
                return;
            }

            _openInExplorerItem.Text = "Open in File Explorer";
        }

        private void OpenFolderInExplorerClick(object sender, EventArgs e)
        {
            DirectoryNode node = _contextFolder;
            if (node == null)
                return;

            try
            {
                if (!Directory.Exists(node.FullPath))
                {
                    MessageBox.Show(this,
                        "Folder no longer exists or is not accessible:\r\n" + node.FullPath,
                        "Fast Disk Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "explorer.exe";
                psi.Arguments = "\"" + node.FullPath + "\"";
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Could not open the folder in File Explorer.\r\n\r\n" + ex.Message,
                    "Fast Disk Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpClick(object sender, EventArgs e)
        {
            if (_current == null || _root == null || _current == _root)
                return;
            if (_current.Parent != null)
                NavigateTo(_current.Parent);
        }

        private void NavigateTo(DirectoryNode node)
        {
            if (_root == null || _tree.Nodes.Count == 0)
            {
                ShowDirectory(node);
                return;
            }

            List<DirectoryNode> path = new List<DirectoryNode>();
            DirectoryNode cursor = node;
            while (cursor != null)
            {
                path.Add(cursor);
                if (cursor == _root)
                    break;
                cursor = cursor.Parent;
            }

            if (path.Count == 0 || path[path.Count - 1] != _root)
            {
                ShowDirectory(node);
                return;
            }

            path.Reverse();
            TreeNode treeNode = _tree.Nodes[0];
            for (int i = 1; i < path.Count; i++)
            {
                MaterializeTreeChildren(treeNode);
                TreeNode next = null;
                for (int j = 0; j < treeNode.Nodes.Count; j++)
                {
                    if (Object.ReferenceEquals(treeNode.Nodes[j].Tag, path[i]))
                    {
                        next = treeNode.Nodes[j];
                        break;
                    }
                }
                if (next == null)
                {
                    ShowDirectory(node);
                    return;
                }
                treeNode.Expand();
                treeNode = next;
            }

            _tree.SelectedNode = treeNode;
            treeNode.EnsureVisible();
        }

        private void ShowDirectory(DirectoryNode node)
        {
            _current = node;
            _upButton.Enabled = _root != null && node != _root && !(_scanThread != null && _scanThread.IsAlive);

            List<DirectoryNode> children = new List<DirectoryNode>(node.Children);
            children.Sort(CompareNodesBySize);

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                for (int i = 0; i < children.Count; i++)
                {
                    DirectoryNode child = children[i];
                    string status = child.IsReparsePoint ? "reparse skipped" : (child.ErrorCode != 0 ? "partial / denied" : "");
                    AddListRow(child.Name, child.TotalBytes, node.TotalBytes, child.TotalFiles, child.TotalFolders, status, child);
                }

                if (node.DirectFiles != 0 || node.DirectBytes != 0)
                {
                    AddListRow("[files directly in this folder]", node.DirectBytes, node.TotalBytes, node.DirectFiles, 0, "", null);
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            _treemap.Root = node;
            _status.Text = node.FullPath;
            _stats.Text = String.Format("{0}   {1:N0} files   {2:N0} folders", FormatBytes(node.TotalBytes), node.TotalFiles, node.TotalFolders);
        }

        private void AddListRow(string name, long size, long parentSize, long files, long folders, string status, DirectoryNode tag)
        {
            ListViewItem item = new ListViewItem(name);
            item.SubItems.Add(FormatBytes(size));
            double pct = parentSize <= 0 ? 0.0 : (100.0 * size / parentSize);
            item.SubItems.Add(pct.ToString("0.0") + "%");
            item.SubItems.Add(files.ToString("N0"));
            item.SubItems.Add(folders.ToString("N0"));
            item.SubItems.Add(status);
            item.Tag = tag;
            _list.Items.Add(item);
        }

        private static int CompareNodesBySize(DirectoryNode a, DirectoryNode b)
        {
            int bySize = b.TotalBytes.CompareTo(a.TotalBytes);
            if (bySize != 0)
                return bySize;
            return StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        }

        private static string FormatBytes(long value)
        {
            double size = value;
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB", "PB" };
            int u = 0;
            while (size >= 1024.0 && u < units.Length - 1)
            {
                size /= 1024.0;
                u++;
            }
            if (u == 0)
                return value.ToString("N0") + " B";
            return size.ToString(size >= 100 ? "0" : (size >= 10 ? "0.0" : "0.00")) + " " + units[u];
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalMinutes >= 1)
                return String.Format("{0}:{1:00}.{2:0}", (int)elapsed.TotalMinutes, elapsed.Seconds, elapsed.Milliseconds / 100);
            return elapsed.TotalSeconds.ToString("0.0") + " s";
        }

        private static string CompactPath(string path, int max)
        {
            if (String.IsNullOrEmpty(path) || path.Length <= max)
                return path;
            int tail = max - 4;
            if (tail < 1)
                return path;
            return "...\\" + path.Substring(path.Length - tail);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cancelRequested = true;
            base.OnFormClosing(e);
        }
    }
}
