using System.Collections.Concurrent;
using System.Reflection;



namespace FileSize
{
    public partial class Form1 : Form
    {
        // A thread-safe bucket to hold updates until the UI is ready
        private ConcurrentQueue<ScanUpdate> _updateBucket = new();
        private System.Windows.Forms.Timer _uiUpdateTimer;

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

            // Track which parents need sorting so we only do it ONCE per parent
            HashSet<TreeNode> dirtyParents = new HashSet<TreeNode>();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            treeView1.BeginUpdate();
            try
            {
                // Process for a max of 100ms per tick or 5000 items
                int count = 0;
                while (_updateBucket.TryDequeue(out var update) && count < 10000 && watch.ElapsedMilliseconds < 500)
                {
                    count++;
                    if (!_pathMap.TryGetValue(update.ParentPath, out TreeNode parentNode)) continue;

                    if (!_pathMap.TryGetValue(update.FullPath, out TreeNode currentNode))
                    {
                        currentNode = new TreeNode(update.ItemName) { Name = update.FullPath, Tag = 0L };
                        parentNode.Nodes.Add(currentNode);
                        _pathMap[update.FullPath] = currentNode;
                    }

                    // Update Tag and Text
                    currentNode.Tag = update.Size;
                    currentNode.Text = update.IsFolder
                        ? $"{update.ItemName} - [{FormatSize(update.Size)}]"
                        : $"{update.ItemName} ({FormatSize(update.Size)})";

                    dirtyParents.Add(parentNode);
                }

                // NOW sort each unique parent once
                foreach (var parent in dirtyParents)
                {
                    SortFolderNodes(parent);
                }
            }
            finally
            {
                treeView1.EndUpdate();
                if (!_updateBucket.IsEmpty) _uiUpdateTimer.Start();
                // If the scan is finished and bucket is empty, don't restart the timer.
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

        private async void btnScan_Click(object sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog() == DialogResult.OK)
            {
                treeView1.Nodes.Clear();
                _pathMap.Clear();
                var rootDir = new DirectoryInfo(fbd.SelectedPath);
                var rootNode = new TreeNode(rootDir.Name) { Name = rootDir.FullName };
                treeView1.Nodes.Add(rootNode);
                _pathMap[rootDir.FullName] = rootNode; // Add the root to the map!

                _uiUpdateTimer.Start();
                nFolder = 0;
                nFile = 0;
                timer1.Start();
                await Task.Run(() => SafeDynamicScan(rootDir));
            }
        }

        private int nFolder = 0;
        private int nFile = 0;

        private long SafeDynamicScan(DirectoryInfo dir)
        {
            long currentDirSize = 0;

            try
            {
                foreach (var file in dir.GetFiles())
                {
                    currentDirSize += file.Length;
                    _updateBucket.Enqueue(new ScanUpdate
                    {
                        ParentPath = dir.FullName,
                        ItemName = file.Name,
                        FullPath = file.FullName,
                        Size = file.Length,
                        IsFolder = false
                    });
                    //Thread.Sleep(10); // Simulate delay for testing UI responsiveness
                    nFile++;
                }

                foreach (var subDir in dir.GetDirectories())
                {
                    if ((subDir.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;

                    // Initial folder discovery
                    _updateBucket.Enqueue(new ScanUpdate
                    {
                        ParentPath = dir.FullName,
                        ItemName = subDir.Name,
                        FullPath = subDir.FullName,
                        IsFolder = true
                    });


                    long subSize = SafeDynamicScan(subDir);
                    currentDirSize += subSize;

                    // Size update for folder
                    _updateBucket.Enqueue(new ScanUpdate
                    {
                        ParentPath = dir.FullName,
                        ItemName = subDir.Name,
                        FullPath = subDir.FullName,
                        Size = subSize,
                        IsFolder = true
                    });
                    //Thread.Sleep(10);               // Simulate delay for testing UI responsiveness
                    nFolder++;
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
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            treeView1.Width = this.ClientSize.Width - 20;
            treeView1.Height = this.ClientSize.Height - 60;
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
    }
}
