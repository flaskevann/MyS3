using System;
using System.IO;
using System.Linq;
using System.Data;
using System.Drawing;
using System.Threading;
using System.Diagnostics;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace MyS3.GUI
{
    public partial class MyS3Form : UserControl
    {
        private static readonly int DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH = 20;

        private static readonly int MAX_LIST_LENGTH = 4;

        private readonly int controlNameCounter;

        private IEnumerable<Control> GetAllControls(Control control)
        {
            var controls = control.Controls.Cast<Control>();
            return controls.SelectMany(ctrl => GetAllControls(ctrl)).Concat(controls);
        }

        // ---

        public MyS3Form(int controlNameCounter)
        {
            InitializeComponent();

            // Number controls to make unique
            this.controlNameCounter = controlNameCounter;
            this.Name += controlNameCounter;
            foreach (Control control in GetAllControls(this))
                control.Name += controlNameCounter;
        }

        // ---

        public MyS3Runner MyS3
        {
            set
            {
                myS3 = value;
                myS3.VerboseLogFunc = (string content) => UpdateConsoleControl(content);
            }
        }
        private MyS3Runner myS3;

        // ---

        public new void Show()
        {
            ReadyControls();
            StartUpdatingControls();

            base.Show();
        }

        private void ReadyControls()
        {
            foreach (Control control in GetAllControls(this))
            {
                // Tabs
                if (control.Name.StartsWith("mys3GroupBox"))
                    control.Text = "Monitoring '" + myS3.MyS3Path;
                else if (control.Name.StartsWith("overviewTabs"))
                    ((TabControl)control).SelectedIndex = 0; // Selects Files tab
                else if (control.Name.StartsWith("filesTab"))
                    control.Text = "Files";
                else if (control.Name.StartsWith("uploadsTab"))
                    control.Text = "Uploads";
                else if (control.Name.StartsWith("downloadsTab"))
                    control.Text = "Downloads";
                else if (control.Name.StartsWith("restoresTab"))
                    control.Text = "Restores";

                // Lists
                else if (control.Name.StartsWith("uploadsList") && !control.Name.StartsWith("uploadsListTitleLabel"))
                    ((ListBox)control).Items.Clear();
                else if (control.Name.StartsWith("downloadsList") && !control.Name.StartsWith("downloadsListTitleLabel"))
                    ((ListBox)control).Items.Clear();
                else if (control.Name.StartsWith("restoreDownloadsList") && !control.Name.StartsWith("restoreDownloadsListTitleLabel"))
                    ((ListBox)control).Items.Clear();
            }

            // Pause buttons
            
            Button pauseDownloadsButton = Controls.Find("pauseDownloadsButton" + controlNameCounter, true).FirstOrDefault() as Button;
            pauseDownloadsButton.Text = "Pause downloads";
            pauseDownloadsButton.Click += (sender, args) => myS3.PauseDownloads(!myS3.DownloadsPaused);

            Button pauseUploadsButton = Controls.Find("pauseUploadsButton" + controlNameCounter, true).FirstOrDefault() as Button;
            pauseUploadsButton.Text = "Pause uploads";
            pauseUploadsButton.Click += (sender, args) => myS3.PauseUploads(!myS3.UploadsPaused);
        }

        public void StartUpdatingControls()
        {
            StartUpdatingSizeUsedControl(); // show number of indexed files and size
            StartUpdatingFilesControl(); // refresh file tree and view when change
            StartUpdatingTransferControls(); // update controls for downloads, uploads and restores
        }

        private void StartUpdatingSizeUsedControl()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                long numberOfFilesLastChange = -1;
                long totalFileSizeLastChange = -1;

                while (!myS3.Stopping)
                {
                    try
                    {
                        // MyS3 file info
                        long numberOfFiles = myS3.NumberOfMyS3Files;
                        long totalFileSize = myS3.GetTotalFileSize();
                        if (numberOfFiles != -1 && totalFileSize != -1)
                        {
                            // Update
                            if (numberOfFiles != numberOfFilesLastChange || totalFileSize != totalFileSizeLastChange)
                            {
                                numberOfFilesLastChange = numberOfFiles;
                                totalFileSizeLastChange = totalFileSize;

                                UpdateSizeUsedControl(numberOfFiles, totalFileSize);
                            }
                        }
                    }
                    catch (Exception) { } // Stops disposed exceptions I can't get rid off

                    Thread.Sleep(250);
                }
            }));
        }

        private delegate void TwoLongParametersDelegate(long number1, long number2);

        private void UpdateSizeUsedControl(long numberOfFiles, long totalSize)
        {
            GroupBox groupBox = Controls.Find("mys3GroupBox" + controlNameCounter, true).FirstOrDefault() as GroupBox;

            if (groupBox == null) return;

            if (groupBox.InvokeRequired)
            {
                groupBox.Invoke(new TwoLongParametersDelegate(UpdateSizeUsedControl), numberOfFiles, totalSize);
            }
            else
            {
                groupBox.Text = "Monitoring '" + myS3.MyS3Path + "'  " +
                    "(" + numberOfFiles + " " + (numberOfFiles == 1 ? "file" : "files") + " ⬌ " +
                        Tools.GetByteSizeAsText(totalSize) + ")";
            }
        }

        // ---

        private void StartUpdatingFilesControl()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                long numberOfFilesLastChange = 0;
                long totalFileSizeLastChange = 0;

                while (!myS3.Stopping)
                {
                    try
                    {
                        // File info
                        long numberOfFiles = 0;
                        long totalFileSize = 0;
                        foreach (string offlineFilePath in Directory.GetFiles(myS3.MyS3Path, "*", SearchOption.AllDirectories))
                        {
                            numberOfFiles++;
                            totalFileSize += (new FileInfo(offlineFilePath)).Length;
                        }

                        // Update
                        if (numberOfFiles != numberOfFilesLastChange || totalFileSize != totalFileSizeLastChange)
                        {
                            numberOfFilesLastChange = numberOfFiles;
                            totalFileSizeLastChange = totalFileSize;

                            UpdateFileViewControl();
                        }
                    }
                    catch (Exception) { } // Stops disposed exceptions I can't get rid off

                    Thread.Sleep(1000);
                }
            }));
        }

        private void UpdateFileViewControl()
        {
            TreeView treeView = Controls.Find("filesTree" + controlNameCounter, true).FirstOrDefault() as TreeView;

            if (treeView == null) return;

            if (treeView.InvokeRequired)
            {
                treeView.Invoke(new NoParametersDelegate(UpdateFileViewControl));
            }
            else
            {
                DirectoryInfo directory = new DirectoryInfo(myS3.MyS3Path);
                if (directory.Exists)
                {
                    // Parent node = MyS3
                    string imageKey = (myS3.MyS3Path + MyS3Runner.RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH) == (directory.FullName + @"\") ? "redfolder" : "folder";
                    TreeNode rootNode = new TreeNode(directory.Name)
                    {
                        Tag = directory,
                        ImageKey = imageKey
                    };

                    // Get child nodes
                    GetDirectories(directory.GetDirectories(), rootNode);

                    treeView.Nodes.Clear();
                    treeView.Nodes.Add(rootNode);
                }
            }
        }

        private void GetDirectories(DirectoryInfo[] childDirectories, TreeNode parentNode)
        {
            foreach (DirectoryInfo directory in childDirectories)
            {
                // Ignore folders
                if (!(myS3.MyS3Path + MyS3Runner.RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH).StartsWith(directory.FullName) &&
                    MyS3Runner.IGNORED_DIRECTORIES_NAMES.Contains(
                        directory.FullName.Replace(myS3.MyS3Path, "").Split(new char[] { @"\"[0] })[0])) continue;

                // Child node to given parent node
                string imageKey = (myS3.MyS3Path + MyS3Runner.RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH) == (directory.FullName + @"\") ? "redfolder" : "folder";
                TreeNode node = new TreeNode(directory.Name, 0, 0)
                {
                    Tag = directory,
                    ImageKey = imageKey
                };

                // Get child nodes to child above
                DirectoryInfo[] childChildDirectories = directory.GetDirectories();
                if (childChildDirectories.Length != 0)
                    GetDirectories(childChildDirectories, node);

                parentNode.Nodes.Add(node);
            }
        }

        private void OpenRestoreDirectory()
        {
            string restoreDirectoryPath = myS3.MyS3Path + MyS3Runner.RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH;

            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "explorer";
            info.Arguments = "\"" + restoreDirectoryPath + "\"";
            Process.Start(info);
        }

        TreeNode selectedNode;

        private void filesTree_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            selectedNode = e.Node;
            DirectoryInfo nodeDirectoryInfo = (DirectoryInfo)e.Node.Tag;

            ListView filesView = Controls.Find("filesView" + controlNameCounter, true).FirstOrDefault() as ListView;
            filesView.Items.Clear();

            // List directories
            foreach (DirectoryInfo directory in nodeDirectoryInfo.GetDirectories())
            {
                // Ignore folders
                if (!(myS3.MyS3Path + MyS3Runner.RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH).StartsWith(directory.FullName) &&
                    MyS3Runner.IGNORED_DIRECTORIES_NAMES.Contains(
                        directory.FullName.Replace(myS3.MyS3Path, "").Split(new char[] { @"\"[0] })[0])) continue;

                int imageIndex = (myS3.MyS3Path + MyS3Runner.RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH) == (directory.FullName + @"\") ? 2 : 0;
                ListViewItem item = new ListViewItem(directory.Name, imageIndex);
                filesView.Items.Add(item);

                ListViewItem.ListViewSubItem[] subItems = new ListViewItem.ListViewSubItem[]
                {
                    new ListViewItem.ListViewSubItem(item, "Folder"),
                    new ListViewItem.ListViewSubItem(item, directory.LastWriteTime.ToShortDateString() + " " + directory.LastWriteTime.ToLongTimeString())
                };
                item.SubItems.AddRange(subItems);
            }

            // List files
            foreach (FileInfo file in nodeDirectoryInfo.GetFiles())
            {
                // Ignore file extensions
                if (MyS3Runner.IGNORED_FILE_EXTENSIONS.Contains(file.Extension)) continue;

                ListViewItem item = new ListViewItem(file.Name, 1);
                filesView.Items.Add(item);

                ListViewItem.ListViewSubItem[] subItems = new ListViewItem.ListViewSubItem[]
                {
                    new ListViewItem.ListViewSubItem(item, "File"),
                    new ListViewItem.ListViewSubItem(item, file.LastWriteTime.ToShortDateString() + " " + file.LastWriteTime.ToLongTimeString())
                };
                item.SubItems.AddRange(subItems);
            }
        }

        private void filesTree_AfterSelected(System.Object sender, System.Windows.Forms.TreeViewEventArgs e)
        {
            if (selectedNode != null)
            {
                DirectoryInfo selectedDirectory = (DirectoryInfo)selectedNode.Tag;

                if ((selectedDirectory.FullName + @"\") == (myS3.MyS3Path + MyS3Runner.RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH))
                {
                    selectedNode.SelectedImageKey = "redfolder";
                }
            }
        }

        private void filesTree_DoubleClick(object sender, EventArgs e)
        {
            TreeView treeView = (TreeView)sender;
            if (treeView.SelectedNode != null)
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = "explorer";
                info.Arguments = "\"" + ((DirectoryInfo)selectedNode.Tag).FullName + "\"";
                Process.Start(info);
            }
        }

        private void filesTree_MouseUp(object sender, MouseEventArgs e)
        {
            if (selectedNode != null)
            {
                DirectoryInfo selectedDirectory = (DirectoryInfo)selectedNode.Tag;

                if (selectedDirectory.FullName == myS3.MyS3Path) // MyS3 root folder
                {
                    TreeView filesTree = (TreeView)sender;

                    // Restore files options
                    if (e.Button == MouseButtons.Right)
                    {
                        ContextMenuStrip contextMenu = new ContextMenuStrip();
                        contextMenu.Enabled = myS3.RestoreDownloadList.Count == 0;

                        //

                        ToolStripMenuItem restoreRemovedFilesMenuItem = new ToolStripMenuItem();
                        restoreRemovedFilesMenuItem.Text = "Restore removed files";
                        contextMenu.Items.Add(restoreRemovedFilesMenuItem);

                        ToolStripMenuItem restoreRemovedFilesLastHourMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last hour",
                        };
                        restoreRemovedFilesLastHourMenuItem.Click += (sender, args) => myS3.RestoreFiles(DateTime.Now.AddHours(-1), true);
                        restoreRemovedFilesMenuItem.DropDownItems.Add(restoreRemovedFilesLastHourMenuItem);

                        ToolStripMenuItem restoreRemovedFilesLast3HoursMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last 3 hours"
                        };
                        restoreRemovedFilesLast3HoursMenuItem.Click += (sender, args) => myS3.RestoreFiles(DateTime.Now.AddHours(-3), true);
                        restoreRemovedFilesMenuItem.DropDownItems.Add(restoreRemovedFilesLast3HoursMenuItem);

                        ToolStripMenuItem restoreRemovedFilesLast24HoursMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last 24 hours"
                        };
                        restoreRemovedFilesLast24HoursMenuItem.Click += (sender, args) => myS3.RestoreFiles(DateTime.Now.AddDays(-1), true);
                        restoreRemovedFilesMenuItem.DropDownItems.Add(restoreRemovedFilesLast24HoursMenuItem);

                        restoreRemovedFilesMenuItem.DropDownItems.Add(new ToolStripSeparator());

                        ToolStripMenuItem restoreRemovedFilesLast2DaysMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last 2 days"
                        };
                        restoreRemovedFilesLast2DaysMenuItem.Click += (sender, args) => myS3.RestoreFiles(DateTime.Now.AddDays(-2), true);
                        restoreRemovedFilesMenuItem.DropDownItems.Add(restoreRemovedFilesLast2DaysMenuItem);

                        ToolStripMenuItem restoreRemovedFilesLastWeekMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last week"
                        };
                        restoreRemovedFilesLastWeekMenuItem.Click += (sender, args) => myS3.RestoreFiles(DateTime.Now.AddDays(-7), true);
                        restoreRemovedFilesMenuItem.DropDownItems.Add(restoreRemovedFilesLastWeekMenuItem);

                        ToolStripMenuItem recoverLastMonthMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last month"
                        };
                        recoverLastMonthMenuItem.Click += (sender, args) => myS3.RestoreFiles(DateTime.Now.AddMonths(-1), true);
                        restoreRemovedFilesMenuItem.DropDownItems.Add(recoverLastMonthMenuItem);

                        ToolStripMenuItem recoverLastSixMonthsMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last six months"
                        };
                        recoverLastSixMonthsMenuItem.Click += (sender, args) => myS3.RestoreFiles(DateTime.Now.AddMonths(-6), true);
                        restoreRemovedFilesMenuItem.DropDownItems.Add(recoverLastSixMonthsMenuItem);

                        //
                        contextMenu.Items.Add(new ToolStripSeparator());

                        ToolStripMenuItem restoreFileVersionsMenuItem = new ToolStripMenuItem();
                        restoreFileVersionsMenuItem.Text = "Restore file versions";
                        contextMenu.Items.Add(restoreFileVersionsMenuItem);

                        ToolStripMenuItem restoreFileVersionsLastHourMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last hour",
                        };
                        restoreFileVersionsLastHourMenuItem.Click += (sender, args) => { OpenRestoreDirectory(); myS3.RestoreFiles(DateTime.Now.AddHours(-1), false); };
                        restoreFileVersionsMenuItem.DropDownItems.Add(restoreFileVersionsLastHourMenuItem);

                        ToolStripMenuItem restoreFileVersionsLast3HoursMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last 3 hours"
                        };
                        restoreFileVersionsLast3HoursMenuItem.Click += (sender, args) => { OpenRestoreDirectory(); myS3.RestoreFiles(DateTime.Now.AddHours(-3), false); };
                        restoreFileVersionsMenuItem.DropDownItems.Add(restoreFileVersionsLast3HoursMenuItem);

                        ToolStripMenuItem restoreFileVersionsLast24HoursMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last 24 hours"
                        };
                        restoreFileVersionsLast24HoursMenuItem.Click += (sender, args) => { OpenRestoreDirectory(); myS3.RestoreFiles(DateTime.Now.AddDays(-1), false); };
                        restoreFileVersionsMenuItem.DropDownItems.Add(restoreFileVersionsLast24HoursMenuItem);

                        restoreFileVersionsMenuItem.DropDownItems.Add(new ToolStripSeparator());

                        ToolStripMenuItem restoreFileVersionsLast2DaysMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last 2 days"
                        };
                        restoreFileVersionsLast2DaysMenuItem.Click += (sender, args) => { OpenRestoreDirectory(); myS3.RestoreFiles(DateTime.Now.AddDays(-2), false); };
                        restoreFileVersionsMenuItem.DropDownItems.Add(restoreFileVersionsLast2DaysMenuItem);

                        ToolStripMenuItem restoreFileVersionsLastWeekMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last week"
                        };
                        restoreFileVersionsLastWeekMenuItem.Click += (sender, args) => { OpenRestoreDirectory(); myS3.RestoreFiles(DateTime.Now.AddDays(-7), false); };
                        restoreFileVersionsMenuItem.DropDownItems.Add(restoreFileVersionsLastWeekMenuItem);

                        ToolStripMenuItem restoreFileVersionsLastMonthMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last month"
                        };
                        restoreFileVersionsLastMonthMenuItem.Click += (sender, args) => { OpenRestoreDirectory(); myS3.RestoreFiles(DateTime.Now.AddMonths(-1), false); };
                        restoreFileVersionsMenuItem.DropDownItems.Add(recoverLastMonthMenuItem);

                        ToolStripMenuItem restoreFileVersionsLastSixMonthMenuItem = new ToolStripMenuItem
                        {
                            Text = "Last six months"
                        };
                        restoreFileVersionsLastSixMonthMenuItem.Click += (sender, args) => { OpenRestoreDirectory(); myS3.RestoreFiles(DateTime.Now.AddMonths(-6), false); };
                        restoreFileVersionsMenuItem.DropDownItems.Add(restoreFileVersionsLastSixMonthMenuItem);

                        //

                        contextMenu.Show(MousePosition);
                    }
                }
            }
        }

        private void filesView_DoubleClick(object sender, EventArgs e)
        {
            ListView filesView = (ListView)sender;
            if (filesView.SelectedItems.Count == 1)
            {
                string selectedFileOrDirectory = filesView.SelectedItems[0].Text;
                string fileOrDirectoryPath = (((DirectoryInfo)selectedNode.Tag).FullName + @"\" + selectedFileOrDirectory).Replace(@"\\", @"\");

                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = "explorer";
                info.Arguments = "\"" + fileOrDirectoryPath + "\"";
                Process.Start(info);
            }
        }

        // ---

        private void StartUpdatingTransferControls()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                while (!myS3.Stopping)
                {
                    try { UpdateTransferControls(); } catch (Exception) { } // Stops disposed exceptions I can't get rid off

                    Thread.Sleep(250);
                }
            }));
        }

        private void UpdateTransferControls()
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new NoParametersDelegate(UpdateTransferControls));
            }
            else
            {
                // Get lists
                ImmutableList<string> downloadList = myS3.DownloadList;
                ImmutableList<string> namedDownloadList = myS3.NamedDownloadList;
                ImmutableList<string> uploadList = myS3.UploadList;
                ImmutableList<string> restoreDownloadList = myS3.RestoreDownloadList;
                ImmutableList<string> namedRestoreDownloadList = myS3.NamedRestoreDownloadList;
                if (downloadList == null || namedDownloadList == null || 
                    uploadList == null ||
                    restoreDownloadList == null || namedRestoreDownloadList == null) return;

                // ---

                // Update tab content
                foreach (Control control in GetAllControls(this))
                {
                    // Tabs
                    if (control.Name.StartsWith("downloadsTab"))
                    {
                        string tabText = "Downloads";
                        if (!myS3.IsComparingS3AndMyS3 && downloadList.Count > 0) tabText += " (" + downloadList.Count + ")";

                        if (control.Text != tabText) // stop constant flashing
                            control.Text = tabText;
                    }
                    else if (control.Name.StartsWith("uploadsTab"))
                    {
                        string tabText = "Uploads";
                        if (!myS3.IsComparingS3AndMyS3 && uploadList.Count > 0) tabText += " (" + uploadList.Count + ")";

                        if (control.Text != tabText) // stop constant flashing
                            control.Text = tabText;
                    }
                    else if (control.Name.StartsWith("restoresTab"))
                    {
                        string tabText = "Restores";
                        if (!myS3.IsComparingS3AndMyS3 && restoreDownloadList.Count > 0) tabText += " (" + restoreDownloadList.Count + ")";

                        if (control.Text != tabText) // stop constant flashing
                            control.Text = tabText;
                    }

                    // Tables - active downloads and uploads and restores
                    else if (control.Name.StartsWith("downloadSpeedLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && namedDownloadList.Count > 0;

                        if (namedDownloadList.Count > 0)
                        {
                            if (myS3.DownloadSpeed > 0)
                                control.Text = "Last average download speed: " + Tools.GetByteSizeAsText((long)myS3.DownloadSpeed) + "/s";
                            else
                                control.Text = "Waiting for first download to finish before calculating speed ..";
                        }
                    }
                    else if (control.Name.StartsWith("downloadFileLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && namedDownloadList.Count > 0;

                        if (namedDownloadList.Count > 0)
                        {
                            string labelText = "";

                            string downloadDirectory = Path.GetDirectoryName(namedDownloadList[0]);
                            string downloadFile = Path.GetFileNameWithoutExtension(namedDownloadList[0]);
                            string downloadFileExtension = Path.GetExtension(namedDownloadList[0]);

                            if (downloadDirectory.Length > DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH)
                                downloadDirectory = downloadDirectory.Substring(0, DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH) + @"....";
                            if (downloadFile.Length > DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH)
                                downloadFile = downloadFile.Substring(0, DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH) + "...";
                            downloadFile += downloadFileExtension;

                            labelText = downloadDirectory + @"\" + downloadFile;
                            if (labelText.StartsWith(@"\")) labelText = labelText.Substring(1);

                            control.Text = labelText;
                        }
                    }
                    else if (control.Name.StartsWith("downloadProgress"))
                    {
                        ProgressBar progressBar = (ProgressBar)control;
                        progressBar.Visible = !myS3.IsComparingS3AndMyS3 && namedDownloadList.Count > 0;
                        progressBar.Value = (int)Math.Round(myS3.DownloadPercent);
                    }
                    else if (control.Name.StartsWith("downloadSizeLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && namedDownloadList.Count > 0 && myS3.DownloadSize > 0;

                        if (namedDownloadList.Count > 0 && myS3.DownloadSize > 0)
                            control.Text = Tools.GetByteSizeAsText((long)myS3.DownloadSize);
                    }
                    else if (control.Name.StartsWith("downloadPercentLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && namedDownloadList.Count > 0 && myS3.DownloadPercent > 0;

                        if (namedDownloadList.Count > 0 && myS3.DownloadPercent > 0)
                            control.Text = Math.Round(myS3.DownloadPercent) + " %";
                    }
                    else if (control.Name.StartsWith("uploadSpeedLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && uploadList.Count > 0;

                        if (uploadList.Count > 0)
                        {
                            if (myS3.UploadSpeed > 0)
                                control.Text = "Last average upload speed: " + Tools.GetByteSizeAsText((long)myS3.UploadSpeed) + "/s";
                            else
                                control.Text = "Waiting for first upload to finish before calculating speed ..";
                        }
                    }
                    else if (control.Name.StartsWith("uploadFileLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && uploadList.Count > 0;

                        if (uploadList.Count > 0)
                        {
                            string labelText = "";

                            string uploadDirectory = Path.GetDirectoryName(uploadList[0]);
                            string uploadFile = Path.GetFileNameWithoutExtension(uploadList[0]);
                            string uploadFileExtension = Path.GetExtension(uploadList[0]);

                            if (uploadDirectory.Length > DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH)
                                uploadDirectory = uploadDirectory.Substring(0, DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH) + "....";
                            if (uploadFile.Length > DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH)
                                uploadFile = uploadFile.Substring(0, DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH) + "...";
                            uploadFile += uploadFileExtension;

                            labelText = uploadDirectory + @"\" + uploadFile;
                            if (labelText.StartsWith(@"\")) labelText = labelText.Substring(1);

                            control.Text = labelText;
                        }
                    }
                    else if (control.Name.StartsWith("uploadProgress"))
                    {
                        ProgressBar progressBar = (ProgressBar)control;
                        progressBar.Visible = !myS3.IsComparingS3AndMyS3 && uploadList.Count > 0;
                        progressBar.Value = (int)Math.Round(myS3.UploadPercent);
                    }
                    else if (control.Name.StartsWith("uploadSizeLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && uploadList.Count > 0 && myS3.UploadSize > 0;

                        if (uploadList.Count > 0 && myS3.UploadSize > 0)
                            control.Text = Tools.GetByteSizeAsText((long)myS3.UploadSize);
                    }
                    else if (control.Name.StartsWith("uploadPercentLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && uploadList.Count > 0 && myS3.UploadPercent > 0;

                        if (uploadList.Count > 0 && myS3.UploadPercent > 0)
                            control.Text = Math.Round(myS3.UploadPercent) + " %";
                    }
                    else if (control.Name.StartsWith("restoreDownloadSpeedLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && namedRestoreDownloadList.Count > 0;

                        if (namedRestoreDownloadList.Count > 0)
                        {
                            if (myS3.RestoreDownloadSpeed > 0)
                                control.Text = "Last average download speed: " + Tools.GetByteSizeAsText((long)myS3.RestoreDownloadSpeed) + "/s";
                            else
                                control.Text = "Waiting for first download to finish before calculating speed ..";
                        }
                    }
                    else if (control.Name.StartsWith("restoreDownloadFileLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && namedRestoreDownloadList.Count > 0;

                        if (namedRestoreDownloadList.Count > 0)
                        {
                            string labelText = "";

                            string restoreDownloadDirectory = Path.GetDirectoryName(namedRestoreDownloadList[0]);
                            string restoreDownloadFile = Path.GetFileNameWithoutExtension(namedRestoreDownloadList[0]);
                            string restoreDownloadFileExtension = Path.GetExtension(namedRestoreDownloadList[0]);

                            if (restoreDownloadDirectory.Length > DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH)
                                restoreDownloadDirectory = restoreDownloadDirectory.Substring(0, DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH) + @"....";
                            if (restoreDownloadFile.Length > DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH)
                                restoreDownloadFile = restoreDownloadFile.Substring(0, DOWNLOAD_AND_UPLOAD_FILE_MAX_TEXT_LENGTH) + "...";
                            restoreDownloadFile += restoreDownloadFileExtension;

                            labelText = restoreDownloadDirectory + @"\" + restoreDownloadFile;
                            if (labelText.StartsWith(@"\")) labelText = labelText.Substring(1);

                            control.Text = labelText;
                        }
                    }
                    else if (control.Name.StartsWith("restoreDownloadProgress"))
                    {
                        ProgressBar progressBar = (ProgressBar)control;
                        progressBar.Visible = !myS3.IsComparingS3AndMyS3 && namedRestoreDownloadList.Count > 0;
                        progressBar.Value = (int)Math.Round(myS3.RestoreDownloadPercent);
                    }
                    else if (control.Name.StartsWith("restoreDownloadSizeLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && namedRestoreDownloadList.Count > 0 && myS3.RestoreDownloadSize > 0;

                        if (restoreDownloadList.Count > 0 && myS3.RestoreDownloadSize > 0)
                            control.Text = Tools.GetByteSizeAsText((long)myS3.RestoreDownloadSize);
                    }
                    else if (control.Name.StartsWith("restoreDownloadPercentLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && namedRestoreDownloadList.Count > 0 && myS3.RestoreDownloadPercent > 0;

                        if (namedRestoreDownloadList.Count > 0 && myS3.RestoreDownloadPercent > 0)
                            control.Text = Math.Round(myS3.RestoreDownloadPercent) + " %";
                    }

                    // Lists
                    else if (control.Name.StartsWith("downloadsListTitleLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && downloadList.Count > 0;
                    }
                    else if (control.Name.StartsWith("downloadsList"))
                    {
                        ListBox listBox = (ListBox)control;
                        while (listBox.Items.Count > downloadList.Count)
                            listBox.Items.RemoveAt(listBox.Items.Count - 1);
                        for (int s = 0; s < downloadList.Count && s < MAX_LIST_LENGTH; s++)
                        {
                            string path = downloadList[s];

                            if (listBox.Items.Count > s)
                                listBox.Items[s] = path;
                            else
                                listBox.Items.Add(path);
                        }
                        if (listBox.Items.Count == MAX_LIST_LENGTH) listBox.Items.Add("...");

                        listBox.Visible = !myS3.IsComparingS3AndMyS3 && downloadList.Count > 0;
                    }
                    else if (control.Name.StartsWith("uploadsListTitleLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && uploadList.Count > 0;
                    }
                    else if (control.Name.StartsWith("uploadsList"))
                    {
                        ListBox listBox = (ListBox)control;
                        while (listBox.Items.Count > uploadList.Count)
                            listBox.Items.RemoveAt(listBox.Items.Count - 1);
                        for (int s = 0; s < uploadList.Count && s < MAX_LIST_LENGTH; s++)
                        {
                            string path = uploadList[s];

                            if (listBox.Items.Count > s)
                                listBox.Items[s] = path;
                            else
                                listBox.Items.Add(path);
                        }
                        if (listBox.Items.Count == MAX_LIST_LENGTH) listBox.Items.Add("...");

                        listBox.Visible = !myS3.IsComparingS3AndMyS3 && uploadList.Count > 0;
                    }
                    else if (control.Name.StartsWith("restoreDownloadsListTitleLabel"))
                    {
                        control.Visible = !myS3.IsComparingS3AndMyS3 && restoreDownloadList.Count > 0;
                    }
                    else if (control.Name.StartsWith("restoreDownloadsList"))
                    {
                        ListBox listBox = (ListBox)control;
                        while (listBox.Items.Count > restoreDownloadList.Count)
                            listBox.Items.RemoveAt(listBox.Items.Count - 1);
                        for (int s = 0; s < restoreDownloadList.Count && s < MAX_LIST_LENGTH; s++)
                        {
                            string path = restoreDownloadList[s];

                            if (listBox.Items.Count > s)
                                listBox.Items[s] = path;
                            else
                                listBox.Items.Add(path);
                        }
                        if (listBox.Items.Count == MAX_LIST_LENGTH) listBox.Items.Add("...");

                        listBox.Visible = !myS3.IsComparingS3AndMyS3 && restoreDownloadList.Count > 0;
                    }

                    // Pausing
                    else if (control.Name.StartsWith("pauseDownloadsButton"))
                    {
                        control.Text = myS3.DownloadsPaused ? "Continue downloads" : "Pause downloads";
                    }
                    else if (control.Name.StartsWith("pauseUploadsButton"))
                    {
                        control.Text = myS3.UploadsPaused ? "Continue uploads" : "Pause uploads";
                    }

                    // Status
                    else if (control.Name.StartsWith("pauseLabel"))
                    {
                        if (myS3.DownloadsPaused && myS3.UploadsPaused)
                        {
                            control.Text = "Downloads and uploads paused";
                            control.ForeColor = Color.Red;

                            if (myS3.WrongEncryptionPassword) control.Text += " - wrong encryption/decryption password";
                        }
                        else if (myS3.DownloadsPaused)
                        {
                            control.Text = "Downloads paused";
                            control.ForeColor = Color.Red;
                        }
                        else if (myS3.UploadsPaused)
                        {
                            control.Text = "Uploads paused";
                            control.ForeColor = Color.Red;
                        }
                        else if (myS3.IsIndexingMyS3Files)
                        {
                            control.Text = "Indexing MyS3 files (" + myS3.NumberOfMyS3Files + ")";
                            control.ForeColor = Color.DarkOrange;
                        }
                        else if (myS3.IsIndexingS3Objects)
                        {
                            control.Text = "Indexing S3 objects (" + myS3.NumberOfIndexedS3Objects + " %)";
                            control.ForeColor = Color.DarkOrange;
                        }
                        else if (myS3.IsComparingS3AndMyS3)
                        {
                            control.Text = "Comparing S3 and MyS3 (" + myS3.ComparisonPercent + " %)";
                            control.ForeColor = Color.DarkOrange;
                        }
                        else if (downloadList.Count > 0 || uploadList.Count > 0)
                        {
                            control.Text = "Syncing";
                            control.ForeColor = Color.DarkOrange;
                        }
                        else
                        {
                            control.Text = "Inactive";
                            control.ForeColor = Color.DarkGreen;
                        }
                    }
                }
            }
        }
        private delegate void NoParametersDelegate();

        // ---

        private void UpdateConsoleControl(string content)
        {
            TextBox consoleBox = Controls.Find("consoleBox" + controlNameCounter, true).FirstOrDefault() as TextBox;

            if (consoleBox == null || consoleBox.IsDisposed) return;

            if (consoleBox.InvokeRequired)
                consoleBox.Invoke(new StringParameterDelegate(UpdateConsoleControl), content);
            else
                consoleBox.AppendText(DateTime.Now.ToLongTimeString() + ": " + content + "\r\n");
        }

        private delegate void StringParameterDelegate(string text);

        private void consoleBox_TextChanged(object sender, EventArgs e)
        {
            TextBox consoleBox = (TextBox) sender;

            if (consoleBox.Lines.Length > 1000)
            {
                List<string> trimmedList = consoleBox.Lines.ToList<string>();
                trimmedList.RemoveRange(0, 20);
                trimmedList.Insert(0, "<earlier output removed>");

                consoleBox.Text = "";
                consoleBox.AppendText(string.Join("\r\n", trimmedList));
            }
        }

        private void consoleBox_MouseUp(object sender, MouseEventArgs e)
        {
            TextBox consoleBox = (TextBox) sender;

            if (e.Button == MouseButtons.Right)
            {
                ContextMenuStrip contextMenu = new ContextMenuStrip();
                contextMenu.Items.Add("Clear", null, (sender, args) => consoleBox.Clear());
                contextMenu.Show(MousePosition);
            }
        }

        // ---

        public new void Dispose()
        {
            Dispose(true);
        }

        protected new void Dispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                myS3.VerboseLogFunc = null;
                myS3.Dispose();

                Thread.Sleep(1000);

                components.Dispose();
                base.Dispose(true);
            }
        }
    }
}
