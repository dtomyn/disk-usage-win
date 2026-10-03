using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace FastDiskUsage
{
    internal static class Tests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [STAThread]
        private static int Main()
        {
            string fixture = Path.Combine(Path.GetTempPath(), "FastDiskUsage-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(fixture, "child", "nested"));
                File.WriteAllBytes(Path.Combine(fixture, "root.bin"), new byte[5]);
                File.WriteAllBytes(Path.Combine(fixture, "child", "nested", "nested.bin"), new byte[7]);
                for (int i = 0; i < 350; i++)
                    File.WriteAllBytes(Path.Combine(fixture, "child", i.ToString() + ".bin"), new byte[3]);

                TestLiveResults(fixture);
                TestCoalescedResults(fixture);
                TestCancellation(fixture);
                TestConcurrentUpdates(fixture);
                TestCollapsedTreeGrowth();
                TestEmptyDirectory(Path.Combine(fixture, "empty"));
                TestDeniedDirectory(Path.Combine(fixture, "missing"));
                Console.WriteLine("PASS: live snapshots, navigation, completion, coalescing, cancellation, concurrent UI updates, collapsed tree growth, empty and inaccessible folders.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                if (Directory.Exists(fixture))
                    Directory.Delete(fixture, true);
            }
        }

        private static void TestLiveResults(string path)
        {
            using (MainForm form = new MainForm())
            {
                form.ShowInTaskbar = false;
                form.Show();
                List<ScanProgress> reports = new List<ScanProgress>();
                DirectoryNode selectedFolder = null;
                DirectoryUpdate firstRoot = null;
                long firstBytes = 0;
                long previousBytes = 0;
                DirectoryNode result = Scanner.Scan(path,
                    delegate { Thread.Sleep(4); return false; },
                    delegate(ScanProgress progress)
                    {
                        reports.Add(progress);
                        DirectoryUpdate root = FindRoot(progress);
                        Assert(root.TotalBytes == progress.Bytes, "Live root bytes match scanned bytes");
                        Assert(root.TotalFiles == progress.Files, "Live root files match scanned files");
                        Assert(root.TotalBytes >= previousBytes, "Live totals do not decrease");
                        previousBytes = root.TotalBytes;
                        Call(form, "ReportProgressFromWorker", progress);
                        Call(form, "RefreshScanResults", false);
                        DirectoryNode displayRoot = Field<DirectoryNode>(form, "_root");
                        Assert(!Object.ReferenceEquals(displayRoot, root.Source), "UI does not read worker nodes");
                        Assert(displayRoot.TotalBytes == progress.Bytes, "UI shows live totals");
                        Assert(Field<TreeView>(form, "_tree").Nodes.Count == 1, "Root appears during scanning");
                        if (!root.IsComplete && selectedFolder == null)
                        {
                            Assert(progress.Bytes > 0 && progress.Bytes < 1062, "Partial results arrive before completion");
                            firstRoot = root;
                            firstBytes = root.TotalBytes;
                            selectedFolder = displayRoot.Children[0];
                            Call(form, "NavigateTo", selectedFolder);
                            Assert(Field<Button>(form, "_upButton").Enabled, "Up works during scanning");
                        }
                    }, FailOnError);

                Assert(reports.Count >= 2 && selectedFolder != null, "At least one live and one final report");
                Assert(firstRoot.TotalBytes == firstBytes && !firstRoot.IsComplete, "Published snapshots stay unchanged");
                Assert(result.TotalBytes == 1062 && result.TotalFiles == 352 && result.TotalFolders == 2, "Final scanner totals");
                Assert(result.IsComplete && result.Children[0].IsComplete, "Completed directory flags");
                Call(form, "ScanCompleted", result, null);
                Assert(Object.ReferenceEquals(Field<DirectoryNode>(form, "_current"), selectedFolder), "Completion preserves navigation");
                Assert(Field<DirectoryNode>(form, "_root").TotalBytes == 1062, "Final UI totals");
                Assert(Field<TreemapControl>(form, "_treemap").Root == selectedFolder, "Treemap follows selected folder");
            }
        }

        private static void TestCoalescedResults(string path)
        {
            using (MainForm form = new MainForm())
            {
                form.ShowInTaskbar = false;
                form.Show();
                DirectoryNode result = Scanner.Scan(path,
                    delegate { Thread.Sleep(2); return false; },
                    delegate(ScanProgress progress) { Call(form, "ReportProgressFromWorker", progress); },
                    FailOnError);
                Call(form, "RefreshScanResults", false);
                DirectoryNode root = Field<DirectoryNode>(form, "_root");
                Assert(root.TotalBytes == result.TotalBytes && root.TotalFiles == result.TotalFiles, "Coalesced totals");
                Assert(root.Children.Count == 1 && root.Children[0].Children.Count == 1, "No missing or duplicate children after coalescing");
                Call(form, "NavigateTo", root.Children[0]);
                ListView list = Field<ListView>(form, "_list");
                list.Items[0].Selected = true;
                object selected = list.Items[0].Tag;
                Call(form, "ShowDirectory", root.Children[0]);
                Assert(list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag == selected, "Refresh preserves list selection");
            }
        }

        private static void TestCancellation(string path)
        {
            bool cancel = false;
            using (MainForm form = new MainForm())
            {
                try
                {
                    Scanner.Scan(path, delegate { Thread.Sleep(2); return cancel; },
                        delegate(ScanProgress progress)
                        {
                            Call(form, "ReportProgressFromWorker", progress);
                            cancel = true;
                        }, FailOnError);
                    throw new Exception("Expected cancellation");
                }
                catch (OperationCanceledException)
                {
                    Call(form, "ScanCompleted", null, "Cancelled");
                    DirectoryNode root = Field<DirectoryNode>(form, "_root");
                    Assert(root != null && root.TotalBytes > 0 && !root.IsComplete, "Cancellation retains provisional results");
                    Assert(Field<ToolStripStatusLabel>(form, "_status").Text == "Cancelled - partial results", "Cancellation is explicit");
                }
            }
        }

        private static void TestEmptyDirectory(string path)
        {
            Directory.CreateDirectory(path);
            DirectoryNode result = Scanner.Scan(path, delegate { return false; },
                delegate(ScanProgress progress)
                {
                    Assert(FindRoot(progress).IsComplete, "Empty directory completion snapshot");
                }, FailOnError);
            Assert(result.TotalBytes == 0 && result.TotalFiles == 0 && result.TotalFolders == 0, "Empty directory totals");
        }

        private static void TestConcurrentUpdates(string path)
        {
            using (MainForm form = new MainForm())
            {
                form.ShowInTaskbar = false;
                form.Show();
                Call(form, "SetScanningUi", true);
                Field<System.Windows.Forms.Timer>(form, "_resultsTimer").Start();
                Exception failure = null;
                Thread worker = new Thread(delegate()
                {
                    try
                    {
                        DirectoryNode result = Scanner.Scan(path,
                            delegate { Thread.Sleep(2); return false; },
                            delegate(ScanProgress progress) { Call(form, "ReportProgressFromWorker", progress); },
                            FailOnError);
                        form.BeginInvoke((MethodInvoker)delegate { Call(form, "ScanCompleted", result, null); });
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                });
                worker.IsBackground = true;
                worker.Start();
                Stopwatch watch = Stopwatch.StartNew();
                bool sawLiveResults = false;
                while (worker.IsAlive || Field<ToolStripProgressBar>(form, "_progress").Visible)
                {
                    Application.DoEvents();
                    DirectoryNode root = Field<DirectoryNode>(form, "_root");
                    if (root != null && !root.IsComplete && root.TotalBytes > 0)
                        sawLiveResults = true;
                    if (failure != null || watch.ElapsedMilliseconds > 30000)
                        break;
                    Thread.Sleep(5);
                }
                worker.Join();
                if (failure != null)
                    throw new Exception("Concurrent scan failed", failure);
                Assert(sawLiveResults, "Timer displays partial results while worker is still scanning");
                Assert(!Field<ToolStripProgressBar>(form, "_progress").Visible, "Concurrent scan completes");
                Assert(Field<DirectoryNode>(form, "_root").TotalBytes == 1062, "Concurrent final totals");
            }
        }

        private static void TestCollapsedTreeGrowth()
        {
            using (MainForm form = new MainForm())
            {
                form.ShowInTaskbar = false;
                form.Show();
                DirectoryNode root = new DirectoryNode();
                root.Name = "root";
                root.FullPath = "C:\\root";
                DirectoryNode first = new DirectoryNode();
                first.Name = "first";
                first.FullPath = "C:\\root\\first";
                first.Parent = root;
                ScanProgress progress = new ScanProgress();
                progress.CurrentPath = root.FullPath;
                progress.Updates = new List<DirectoryUpdate>();
                progress.Updates.Add(new DirectoryUpdate(root));
                progress.Updates.Add(new DirectoryUpdate(first));
                Call(form, "ReportProgressFromWorker", progress);
                Call(form, "RefreshScanResults", false);
                TreeNode treeRoot = Field<TreeView>(form, "_tree").Nodes[0];
                Assert(treeRoot.IsExpanded && treeRoot.Nodes.Count == 1, "Initial tree is materialized");
                treeRoot.Collapse();
                DirectoryNode second = new DirectoryNode();
                second.Name = "second";
                second.FullPath = "C:\\root\\second";
                second.Parent = root;
                progress.Updates = new List<DirectoryUpdate>();
                progress.Updates.Add(new DirectoryUpdate(root));
                progress.Updates.Add(new DirectoryUpdate(second));
                Call(form, "ReportProgressFromWorker", progress);
                Call(form, "RefreshScanResults", false);
                treeRoot.Expand();
                Assert(treeRoot.Nodes.Count == 2, "Collapsed materialized folders retain newly discovered children");
            }
        }

        private static void TestDeniedDirectory(string path)
        {
            int errors = 0;
            DirectoryNode result = Scanner.Scan(path, delegate { return false; },
                delegate(ScanProgress progress) { Assert(progress.Errors == 1, "Error count in snapshot"); },
                delegate(string errorPath, string operation, int code) { errors++; });
            Assert(errors == 1 && result.ErrorCode != 0 && result.IsComplete, "Inaccessible directory error retained");
        }

        private static DirectoryUpdate FindRoot(ScanProgress progress)
        {
            foreach (DirectoryUpdate update in progress.Updates)
                if (update.Parent == null)
                    return update;
            throw new Exception("Missing root snapshot");
        }

        private static void FailOnError(string path, string operation, int code)
        {
            throw new Exception(operation + " failed for " + path + ": " + code);
        }

        private static T Field<T>(MainForm form, string name)
        {
            return (T)typeof(MainForm).GetField(name, PrivateInstance).GetValue(form);
        }

        private static void Call(MainForm form, string name, params object[] args)
        {
            typeof(MainForm).GetMethod(name, PrivateInstance).Invoke(form, args);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new Exception("FAIL: " + message);
        }
    }
}
