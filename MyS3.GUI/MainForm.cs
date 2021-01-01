using System;
using System.Threading;
using System.Diagnostics;
using System.Windows.Forms;
using System.Collections.Generic;

namespace MyS3.GUI
{
    public partial class MainForm : Form
    {
        public static readonly string USER_MANUAL_URL = "https://github.com/flaskevann/MyS3/blob/master/Docs/UserManual.pdf";

        private List<MyS3Runner> myS3s = new List<MyS3Runner>();

        // ---

        private long GetDownloadedBytes()
        {
            long downloaded = 0;
            foreach (MyS3Runner MyS3Runner in myS3s)
                downloaded += MyS3Runner.DownloadedTotalBytes + MyS3Runner.RestoreDownloadedTotalBytes;
            return downloaded;
        }

        private long GetUploadedBytes()
        {
            long downloaded = 0;
            foreach (MyS3Runner MyS3Runner in myS3s)
                downloaded += MyS3Runner.UploadedTotalBytes;
            return downloaded;
        }

        // ---

        private void UpdateStatus(string content)
        {
            if (statusStrip.InvokeRequired)
                statusStrip.Invoke(new StringParameterDelegate(UpdateStatus), content);
            else
                statusLabel.Text = content;
        }

        private delegate void StringParameterDelegate(string text);

        // ---

        public MainForm()
        {
            InitializeComponent();

            // Get pause status
            pauseMenuItem.Checked = Properties.Settings.Default.Pause;

            StartInternetChecker();
            CreateEditSetupMenuItems();

            TriggerUseMyS3Setups();
        }

        private void StartInternetChecker()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                bool hasInternet = true;

                while (!base.IsDisposed)
                {
                    bool hasInternetNew = Tools.HasInternet();

                    // Internet access changed
                    if (hasInternetNew != hasInternet)
                    {
                        hasInternet = hasInternetNew;

                        if (hasInternet)
                        {
                            UpdateStatus("Status: Ready");

                            if (myS3s.Count == 0)
                                TriggerUseMyS3Setups();
                            else
                                foreach (MyS3Runner mys3 in myS3s)
                                    mys3.Pause(Properties.Settings.Default.Pause);
                        }
                        else
                        {
                            UpdateStatus("Status: No network connection");

                            foreach (MyS3Runner mys3 in myS3s)
                                mys3.Pause(true);
                        }
                    }

                    // Pause until next check
                    Thread.Sleep(10 * 1000);
                }
            }));
        }

        public void CreateEditSetupMenuItems()
        {
            // Find old menu items
            List<ToolStripMenuItem> editMenuItems = new List<ToolStripMenuItem>();
            foreach (ToolStripItem menuItem in fileMenuItem.DropDownItems)
            {
                // Skip separator
                if (menuItem.GetType() == typeof(ToolStripSeparator)) continue;

                // Add edit items
                ToolStripMenuItem menuItemButton = (ToolStripMenuItem) menuItem;
                if (menuItemButton.Text.StartsWith("Edit "))
                    editMenuItems.Add(menuItemButton);
            }

            // Remove the old menu items
            foreach (ToolStripMenuItem editMenuItemButton in editMenuItems)
                fileMenuItem.DropDownItems.Remove(editMenuItemButton);

            // Create new menu items
            List<ToolStripMenuItem> newEditMenuItemButtons = new List<ToolStripMenuItem>();
            foreach (KeyValuePair<string, MyS3Setup> kvp in SetupStore.Entries) // kvp.Key = bucket name string, kvp.Value = Setup object
                newEditMenuItemButtons.Add(new ToolStripMenuItem("Edit '" + kvp.Key + "'" + " ..", null, editSetupMenuItem_Click));
            foreach (ToolStripMenuItem editMenuItem in newEditMenuItemButtons)
                fileMenuItem.DropDownItems.Insert(1, editMenuItem);
        }

        private void TriggerUseMyS3Setups()
        {
            if (this.InvokeRequired)
                this.Invoke(new VoidParameterDelegate(TriggerUseMyS3Setups));
            else
                UseMyS3Setups();
        }

        private delegate void VoidParameterDelegate();

        public void UseMyS3Setups()
        {
            // Show or hide instructions and MyS3 tabs
            noSetupsLabel.Visible = SetupStore.Entries.Count == 0;

            // ---

            // Remove old or test overview controls
            foreach (TabPage tabPage in mys3Tabs.TabPages)
            {
                if (tabPage.Controls.Count > 0)
                {
                    MyS3Form myS3Form = tabPage.Controls[0] as MyS3Form;
                    myS3Form.Dispose();
                }
                tabPage.Dispose();
            }
            mys3Tabs.TabPages.Clear();
            myS3s.Clear();
            mys3Tabs.Visible = SetupStore.Entries.Count > 0;

            // ---

            // Setup controls for each MyS3 setup
            int setupCounter = 0;
            bool brokenSetup = false;
            foreach (KeyValuePair<string, MyS3Setup> kvp in SetupStore.Entries)
            {
                MyS3Setup setup = kvp.Value;

                // Skip deactivated setup
                if (!setup.InUseNow) continue;

                // ---

                // New MyS3 instance
                MyS3Runner myS3 = new MyS3Runner(
                    setup.Bucket, setup.Region,
                    setup.AwsAccessKeyID, setup.AwsSecretAccessKey,
                    setup.MyS3Path,
                    setup.EncryptionPassword,
                    setup.SharedBucket,
                    null, Tools.Log); // set verbose log function from MyS3Form when ready

                // Controls for the MyS3 instance
                MyS3Form myS3Form = new MyS3Form(setupCounter+1);
                myS3Form.MyS3 = myS3; // Assign the MyS3 instance to the controls

                // Run instance
                try
                {
                    myS3.Setup();
                    if (Properties.Settings.Default.Pause) myS3.Pause(true);

                    myS3.Start();

                    myS3s.Add(myS3);
                }
                catch (Exception ex)
                {
                    statusLabel.Text = "Status: Problem with setup '" + setup.Bucket + "'. " + ex.Message;
                    brokenSetup = true;
                    break; // skip rest of the setups
                }

                // Show controls
                TabPage newMyS3Tab = new TabPage()
                {
                    Text = setup.Bucket,
                    Name = "mys3Tab" + (setupCounter + 1)
                };
                newMyS3Tab.Controls.Add(myS3Form);
                mys3Tabs.Controls.Add(newMyS3Tab);
                myS3Form.Show();

                // ---

                setupCounter++;
            }

            if (!brokenSetup) statusLabel.Text = "Status: Using " + setupCounter + "/" + SetupStore.Entries.Count + " MyS3 setups";
        }

        // ---

        private void newSetupMenuItem_Click(object sender, EventArgs e)
        {
            Client.NewSetup();
        }

        private void editSetupMenuItem_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem editSetupMenuItem = (ToolStripMenuItem) sender;
            string editSetupBucket = editSetupMenuItem.Text.Replace("Edit ", "").Replace("'", "").Replace(" ..", "");


            Client.EditSetup(editSetupBucket);
        }

        private void hideMenuItem_Click(object sender, EventArgs e)
        {
            Hide();
        }

        public void quitMenuItem_Click(object sender, EventArgs e)
        {
            DialogResult dialogResult = new ConfirmBox("Confirm", "Are you sure you want to quit?").ShowDialog();
            if (dialogResult == DialogResult.Yes) Client.Quit(sender, e);
        }

        // ---

        private void pauseMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.Pause = pauseMenuItem.Checked;
            Properties.Settings.Default.Save();
            bool paused = Properties.Settings.Default.Pause;

            // Pause or continue
            foreach (MyS3Runner myS3 in myS3s)
                myS3.Pause(paused);

            statusLabel.Text = "Status: " +
                (paused ?
                    "All MyS3 downloads and uploads paused" :
                    "All MyS3 downloads and uploads set to continue");
        }

        // ---

        private void userGuideMenuItem_Click(object sender, EventArgs e)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = USER_MANUAL_URL,
                UseShellExecute = true
            };
            Process.Start(psi);
        }

        private void statisticsMenuItem_Click(object sender, EventArgs e)
        {
            TimeSpan uptime = DateTime.Now - Client.Startup;

            string info = "";
            info += "S3 buckets: " + SetupStore.Entries.Count;
            info += "\r\n";
            info += "Uploaded: " + Tools.GetByteSizeAsText(GetUploadedBytes());
            info += "\r\n";
            info += "Downloaded: " + Tools.GetByteSizeAsText(GetDownloadedBytes());
            info += "\r\n";
            info += "\r\n";
            info += "Uptime: " + uptime.Days + "d " + uptime.Hours + "h " + uptime.Minutes+"m " + uptime.Seconds+"s";
            info += "\r\n";
            info += "Startup: " + Client.Startup.ToShortDateString();

            (new InfoBox("Statistics", info)).ShowDialog();
        }

        private void aboutMenuItem_Click(object sender, EventArgs e)
        {
            string info = "";
            info += "Name: MyS3";
            info += "\r\n";
            info += "Encryption: AES-128 GCM";
            info += "\r\n";
            info += "MyS3 version: " + typeof(MyS3Runner).Assembly.GetName().Version;
            info += "\r\n";
            info += "MyS3 GUI version: " + typeof(MainForm).Assembly.GetName().Version;
            info += "\r\n";
            info += "Developer: Ove Bakken";
            info += "\r\n";
            info += "License: MIT";

            (new InfoBox("About", info)).ShowDialog();
        }

        // ---

        public new void Close()
        {
            foreach (MyS3Runner runner in myS3s) runner.Stop();

            base.Close();
        }

        private void OverviewForm_Closing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;

            Hide();
        }

        // ---

        public new void Dispose()
        {
            Dispose(true);
        }

        private new void Dispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                components.Dispose();
                base.Dispose(true);
            }
        }
    }
}