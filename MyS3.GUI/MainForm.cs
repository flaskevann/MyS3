using System;
using System.Diagnostics;
using System.Windows.Forms;
using System.Collections.Generic;

namespace MyS3.GUI
{
    public partial class MainForm : Form
    {
        public static readonly string USER_MANUAL_URL = "https://github.com/flaskevann/MyS3/blob/master/Docs/UserManual.pdf";

        private List<MyS3Runner> myS3s = new List<MyS3Runner>();

        private long GetDownloadedBytes()
        {
            long downloaded = 0;
            foreach (MyS3Runner MyS3Runner in myS3s)
                downloaded += MyS3Runner.Downloaded;
            return downloaded;
        }

        private long GetUploadedBytes()
        {
            long downloaded = 0;
            foreach (MyS3Runner MyS3Runner in myS3s)
                downloaded += MyS3Runner.Uploaded;
            return downloaded;
        }

        // ---

        public MainForm()
        {
            InitializeComponent();

            CreateEditSetupMenuItems();
            UseMyS3Setups();
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

        public void UseMyS3Setups()
        {
            // Show or hide instructions and MyS3 tabs
            noSetupsLabel.Visible = SetupStore.Entries.Count == 0;
            mys3Tabs.Visible = SetupStore.Entries.Count > 0;

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
                    if (pauseMenuItem.Checked) myS3.Pause(true);
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
            bool paused = pauseMenuItem.Checked;

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
            info += "MyS3.GUI version " + typeof(MainForm).Assembly.GetName().Version;
            info += "\r\n";
            info += "MyS3 version " + typeof(MyS3Runner).Assembly.GetName().Version;
            info += "\r\n";
            info += "Developer: Ove Bakken";
            info += "\r\n";
            info += "License: MIT";

            (new InfoBox("About", info)).ShowDialog();
        }

        // ---

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