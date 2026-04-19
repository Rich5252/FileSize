using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;



namespace FileSize
{
    public partial class Form1 : Form
    {
        // A thread-safe bucket to hold updates until the UI is ready
        private ConcurrentQueue<ScanUpdate> _updateBucket = new();
        private System.Windows.Forms.Timer _uiUpdateTimer;
        private CancellationTokenSource _cts;

        public Form1()
        {
            InitializeComponent();

            // Initialize a timer to flush the bucket every 150ms
            _uiUpdateTimer = new System.Windows.Forms.Timer();
            _uiUpdateTimer.Interval = 150;
            _uiUpdateTimer.Tick += FlushUpdateBucket;

            EnableDoubleBuffering();        //for treeview control to prevent flickering
        }

        private Dictionary<string, TreeNode> _pathMap = new();

        private void FlushUpdateBucket(object sender, EventArgs e)
        {
            if (_updateBucket.IsEmpty) return;
            _uiUpdateTimer.Stop();

            HashSet<TreeNode> dirtyParents = new HashSet<TreeNode>();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            treeView1.BeginUpdate();
            try
            {
                int count = 0;
                while (_updateBucket.TryDequeue(out var update) && count < 5000 && watch.ElapsedMilliseconds < 30)
                {
                    count++;
                    TreeNode currentNode = null;

                    // 1. Try to find the node directly (Works for Root and existing folders)
                    if (_pathMap.TryGetValue(update.FullPath, out currentNode))
                    {
                        currentNode.Tag = update.Size;
                    }
                    // 2. If it's a new subfolder, try to find the parent to create it
                    else if (!string.IsNullOrEmpty(update.ParentPath) && _pathMap.TryGetValue(update.ParentPath, out TreeNode parentNode))
                    {
                        currentNode = new TreeNode(update.ItemName) { Name = update.FullPath, Tag = update.Size };
                        parentNode.Nodes.Add(currentNode);
                        _pathMap[update.FullPath] = currentNode;
                        dirtyParents.Add(parentNode);
                    }

                    // 3. Always refresh the text if we have a node (either old or newly created)
                    if (currentNode != null)
                    {
                        currentNode.Text = $"{update.ItemName} - [{FormatSize(update.Size)}]";

                        if (currentNode.Parent != null)
                            dirtyParents.Add(currentNode.Parent);
                    }
                }

                // Sort parents that had children change
                foreach (var parent in dirtyParents)
                {
                    SortFolderNodes(parent);
                }
            }
            finally
            {
                treeView1.EndUpdate();
                if (!_updateBucket.IsEmpty) _uiUpdateTimer.Start();
            }
        }

        private void SortFolderNodes(TreeNode parent)
        {
            if (parent.Nodes.Count < 2) return;

            // Get the current order
            var currentNodes = parent.Nodes.Cast<TreeNode>().ToList();

            // Determine what the sorted order SHOULD be
            var sortedNodes = currentNodes
                .OrderByDescending(n => n.Tag is long l ? l : 0L)
                .ToList();

            // CRITICAL: Only update the UI if the order actually changed
            // This saves massive amounts of rendering time
            if (!currentNodes.SequenceEqual(sortedNodes))
            {
                parent.Nodes.Clear();
                parent.Nodes.AddRange(sortedNodes.ToArray());
            }
        }



        // Simple class to pass data back to the UI
        public class ScanUpdate
        {
            public string ParentPath { get; set; } = ""; // Use path as the key
            public string ItemName { get; set; } = "";
            public string FullPath { get; set; } = "";
            public long Size { get; set; }
            public bool IsFolder { get; set; }
        }


        private DirectoryInfo rootDir;
        private async void btnScan_Click(object sender, EventArgs e)
        {
            // 1. If a scan is already running, stop it first!
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
            }

            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog() == DialogResult.OK)
            {
                // 2. Initialize a fresh token for this specific scan
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;

                // Reset UI and Data
                _updateBucket.Clear();
                _pathMap.Clear();
                treeView1.Nodes.Clear();
                listViewFiles.Items.Clear(); // Clear your file pane too

                rootDir = new DirectoryInfo(fbd.SelectedPath);
                var rootNode = new TreeNode(rootDir.Name)
                {
                    Name = rootDir.FullName, // This is the key for the dictionary
                    Tag = 0L
                };

                rootNode.Nodes.Add("Loading..."); // Add the trigger for the expand event
                treeView1.Nodes.Add(rootNode);
                _pathMap[rootDir.FullName] = rootNode;

                // Reset counters and start timers
                nFolder = 0;
                nFile = 0;
                _uiUpdateTimer.Start(); // The bucket flusher
                timer1.Start();         // Your stats timer

                try
                {
                    // 3. Pass the token into the background task
                    await Task.Run(() => SafeDynamicScan(rootDir, token), token);

                    // Success! 
                    timer1.Stop();
                    statusStrip1.Items[0].Text = $"Folders: {nFolder} | Files: {nFile}";
                    // Give the bucket one final flush to catch the last items
                    FlushUpdateBucket(null, null);
                    _uiUpdateTimer.Stop();
                }
                catch (OperationCanceledException)
                {
                    // This happens if the user hits "Stop"
                    this.Text = "Scan Cancelled";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error: {ex.Message}");
                }
            }
        }

        private int nFolder = 0;
        private int nFile = 0;

        // Use this to store the "Source of Truth" for folder sizes and children
        private ConcurrentDictionary<string, long> _folderSizes = new();
        private ConcurrentDictionary<string, List<string>> _folderStructure = new();

        private long SafeDynamicScan(DirectoryInfo dir, CancellationToken token)
        {
            if (token.IsCancellationRequested) return 0;
            long currentDirSize = 0;

            try
            {
                var subDirs = new List<string>();
                foreach (var file in dir.GetFiles())
                {
                    if (token.IsCancellationRequested) return 0;
                    currentDirSize += file.Length;
                    Interlocked.Increment(ref nFile);
                }

                foreach (var subDir in dir.GetDirectories())
                {
                    if ((subDir.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;
                    subDirs.Add(subDir.FullName);
                    nFolder++;
                }

                // CRITICAL: Save the structure NOW so the UI can expand this folder 
                // while the sizes are still being calculated in the background.
                _folderStructure[dir.FullName] = subDirs;

                // Now do the heavy lifting
                foreach (var subPath in subDirs)
                {
                    currentDirSize += SafeDynamicScan(new DirectoryInfo(subPath), token);
                }

                // Save the final size
                _folderSizes[dir.FullName] = currentDirSize;

                if (dir.FullName == rootDir.FullName)
                {
                    this.Invoke(() => { treeView1.Nodes[0].Text = $"{dir.Name} - [{FormatSize(currentDirSize)}]"; });
                }
            }
            catch (UnauthorizedAccessException) { }
            return currentDirSize;
        }


        private string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = (decimal)bytes;
            while (number >= 1024)
            {
                number /= 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            treeView1.Width = this.ClientSize.Width / 3;
            treeView1.Height = this.statusStrip1.Top - 10 - treeView1.Top;
            listViewFiles.Left = treeView1.Right + 10;
            listViewFiles.Width = this.ClientSize.Width - treeView1.Width - 30;
            listViewFiles.Height = treeView1.Height;
            listViewFiles.Columns[0].Width = listViewFiles.Width / 3 * 2;
            listViewFiles.Columns[1].Width = listViewFiles.Width / 6;
            listViewFiles.Columns[2].Width = listViewFiles.Width / 6;
        }

        private void EnableDoubleBuffering()
        {
            // This tells Windows to paint the TreeView in memory before showing it on screen,
            // which eliminates the "white flash" and flickering during fast updates.
            typeof(System.Windows.Forms.TreeView).InvokeMember("DoubleBuffered",
                BindingFlags.SetProperty | BindingFlags.Instance | BindingFlags.NonPublic,
                null, treeView1, new object[] { true });
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            timer1.Stop();
            statusStrip1.Items[0].Text = $"Folders: {nFolder} | Files: {nFile}";
            timer1.Start();
        }


        private void treeView1_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            TreeNode parentNode = e.Node;

            // 1. Check if we have a dummy node
            if (parentNode.Nodes.Count == 1 && parentNode.Nodes[0].Text == "Loading...")
            {
                parentNode.Nodes.Clear();
                string path = parentNode.Name;

                // 2. Pull children from our background cache
                if (_folderStructure.TryGetValue(path, out List<string> children))
                {
                    foreach (var childPath in children)
                    {
                        var di = new DirectoryInfo(childPath);
                        long size = _folderSizes.GetValueOrDefault(childPath, 0);

                        TreeNode childNode = new TreeNode($"{di.Name} - [{FormatSize(size)}]")
                        {
                            Name = childPath,
                            Tag = size
                        };

                        // 3. Add a dummy if this child has sub-folders according to our cache
                        if (_folderStructure.ContainsKey(childPath) && _folderStructure[childPath].Count > 0)
                        {
                            childNode.Nodes.Add("Loading...");
                        }

                        parentNode.Nodes.Add(childNode);
                    }

                    // 4. Sort only these new visible nodes
                    SortFolderNodes(parentNode);
                }
            }
        }

        private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            string path = e.Node.Name; // We stored the FullPath here
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            listViewFiles.Items.Clear();
            listViewFiles.BeginUpdate();

            try
            {
                DirectoryInfo di = new DirectoryInfo(path);

                // Get files and sort them descending by size
                var files = di.GetFiles().OrderByDescending(f => f.Length);

                foreach (var file in files)
                {
                    var item = new ListViewItem(file.Name);
                    item.Name = file.FullName; // Store full path for context menu actions
                    item.SubItems.Add(FormatSize(file.Length));
                    item.SubItems.Add(file.Extension.ToUpper().Replace(".", "") + " File");

                    listViewFiles.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                listViewFiles.Items.Add(new ListViewItem($"Access Denied: {ex.Message}"));
            }
            finally
            {
                listViewFiles.EndUpdate();
            }
        }

        private void treeView1_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            // Ensure the node clicked is actually selected before the menu pops up
            if (e.Button == MouseButtons.Right)
            {
                treeView1.SelectedNode = e.Node;
            }
        }

        private void openInFileExplorerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string path = "";

            // Check if the TreeView or ListView is focused
            if (treeView1.Focused)
                path = treeView1.SelectedNode?.Name;
            else if (listViewFiles.Focused && listViewFiles.SelectedItems.Count > 0)
                path = listViewFiles.SelectedItems[0].Name; // Ensure you set .Name when adding items to ListView!

            if (!string.IsNullOrEmpty(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
        }

    }
}
