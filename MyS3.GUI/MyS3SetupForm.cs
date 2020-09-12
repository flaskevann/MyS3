using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

using Amazon;

namespace MyS3.GUI
{
    public partial class MyS3SetupForm : Form
    {
        private readonly Color DEFAULT_CONTROL_BACKGROUND_COLOR;

        private MyS3Setup editedSetup;

        public MyS3SetupForm()
        {
            InitializeComponent();

            DEFAULT_CONTROL_BACKGROUND_COLOR = bucketBox.BackColor;
        }

        public new void Show()
        {
            base.Show();

            myS3PathLabel.Text = Environment.ExpandEnvironmentVariables(
                MyS3Runner.DEFAULT_RELATIVE_LOCAL_MYS3_DIRECTORY_PATH);
        }

        public void Show(string bucket)
        {
            base.Show();

            editedSetup = SetupStore.Entries[bucket];

            bucketBox.Text = editedSetup.Bucket;

            foreach (string completeRegionName in regionBox.Items)
                if (completeRegionName.Contains(editedSetup.Region)) // e.g. "Europe (Stockholm) / eu-north-1" contains "eu-north-1"
                {
                    regionBox.Text = completeRegionName;

                    break;
                }

            awsAccessKeyIDBox.Text = editedSetup.AwsAccessKeyID;
            awsSecretAccessKeyBox.Text = editedSetup.AwsSecretAccessKey;

            myS3PathLabel.Text = editedSetup.MyS3Path;

            encryptionPasswordBox.Text = editedSetup.EncryptionPassword;
            confirmEncryptionPassword1Box.Text = editedSetup.EncryptionPassword;
            confirmEncryptionPassword2Box.Text = editedSetup.EncryptionPassword;
            multipleClientsBox.Checked = editedSetup.SharedBucket;
            inUseBox.Checked = editedSetup.InUseNow;

            // Process form
            CheckAWSSetup();
            myS3Group.Enabled = true; // skip having to test AWS values
            CheckMyS3Setup();
        }

        // ---

        private void bucketBox_TextChanged(object sender, EventArgs e)
        {
            /* 
             * Bucket names must be between 3 and 63 characters long. 
             * Bucket names can consist only of lowercase letters, numbers, dots (.), and hyphens (-). 
             * Bucket names must begin and end with a letter or number.
             */

            if (bucketBox.TextLength == 0)
            {
                bucketBox.BackColor = DEFAULT_CONTROL_BACKGROUND_COLOR;
            }
            else if (bucketBox.TextLength >= 3 && bucketBox.TextLength <= 63 &&                                                         // length
                    ((bucketBox.Text.First<char>() + "").Any(char.IsLower) || (bucketBox.Text.First<char>() + "").Any(char.IsDigit)) && // first char is lower letter or number
                    ((bucketBox.Text.Last<char>() + "").Any(char.IsLower) || (bucketBox.Text.Last<char>() + "").Any(char.IsDigit)) &&   // last char is lower letter or number
                    !bucketBox.Text.Any(char.IsSymbol) &&                                                                               // no symbols
                    !bucketBox.Text.Any(char.IsWhiteSpace))                                                                             // no whitespace
            {
                bucketBox.BackColor = Color.LightGreen;
            }
            else
            {
                bucketBox.BackColor = Color.LightPink;
            }

            CheckAWSSetup();
        }

        private void regionBox_TextChanged(object sender, EventArgs e)
        {
            if (regionBox.Text.Length == 0)
            {
                regionBox.BackColor = DEFAULT_CONTROL_BACKGROUND_COLOR;
            }
            else
            {
                regionBox.BackColor = Color.LightPink;
                foreach (string completeRegionName in regionBox.Items)
                {
                    if (completeRegionName == regionBox.Text)
                    {
                        regionBox.BackColor = Color.LightGreen;

                        break;
                    }
                }
            }

            CheckAWSSetup();
        }

        private void regionBox_DropDown(object sender, EventArgs e)
        {
            regionBox.BackColor = DEFAULT_CONTROL_BACKGROUND_COLOR;
        }

        private void regionBox_DropDownClosed(object sender, EventArgs e)
        {
            regionBox_TextChanged(sender, e);
        }

        private void awsAccessKeyID_TextChanged(object sender, EventArgs e)
        {
            if (awsAccessKeyIDBox.TextLength == 0)
            {
                awsAccessKeyIDBox.BackColor = DEFAULT_CONTROL_BACKGROUND_COLOR;
            }
            else if (awsAccessKeyIDBox.TextLength == 20 &&
                     awsAccessKeyIDBox.Text.StartsWith("AKIA") &&
                     !awsAccessKeyIDBox.Text.Any(char.IsWhiteSpace))
            {
                awsAccessKeyIDBox.BackColor = Color.LightGreen;
            }
            else
            {
                awsAccessKeyIDBox.BackColor = Color.LightPink;
            }

            CheckAWSSetup();
        }

        private void awsSecretAccessKey_TextChanged(object sender, EventArgs e)
        {
            if (awsSecretAccessKeyBox.TextLength == 0)
            {
                awsSecretAccessKeyBox.BackColor = DEFAULT_CONTROL_BACKGROUND_COLOR;
            }
            else if (awsSecretAccessKeyBox.TextLength == 40 &&
                    !awsSecretAccessKeyBox.Text.Any(char.IsWhiteSpace))
            {
                awsSecretAccessKeyBox.BackColor = Color.LightGreen;
            }
            else
            {
                awsSecretAccessKeyBox.BackColor = Color.LightPink;
            }

            CheckAWSSetup();
        }

        private void CheckAWSSetup()
        {
            myS3Group.Enabled = false;

            bool ok =
                bucketBox.BackColor == Color.LightGreen && // has ok bucket name
                regionBox.BackColor == Color.LightGreen && // selected region
                awsAccessKeyIDBox.BackColor == Color.LightGreen && // ok aws key id
                awsSecretAccessKeyBox.BackColor == Color.LightGreen; // ok aws key

            testButton.Enabled = ok;

            CheckMyS3Setup();
        }

        private delegate void NoParametersDelegate();

        private void EnableTestButton()
        {
            if (testButton.InvokeRequired)
                testButton.Invoke(new NoParametersDelegate(EnableTestButton));
            else
                testButton.Enabled = true;

        }

        private void EnableMyS3Box()
        {
            if (myS3Group.InvokeRequired)
                myS3Group.Invoke(new NoParametersDelegate(EnableMyS3Box));
            else
                myS3Group.Enabled = true;
        }

        private void testButton_Click(object sender, EventArgs e)
        {
            string region = regionBox.Text;
            region = region.Substring(region.LastIndexOf("/") + 2);

            S3Wrapper s3 = new S3Wrapper(
                bucketBox.Text, RegionEndpoint.GetBySystemName(region),
                awsAccessKeyIDBox.Text, awsSecretAccessKeyBox.Text);

            testButton.Enabled = false;

            // Run tests
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                s3.RunTests();
                while (!s3.TestsRun) Thread.Sleep(10);

                EnableTestButton();

                if (s3.TestsResult == null)
                {
                    EnableMyS3Box();

                    (new InfoBox("Test Results", "Every test succeeded so please proceed!")).ShowDialog();
                }
                else
                {
                    (new InfoBox("Test Results", "Encountered a problem: " + s3.TestsResult)).ShowDialog();
                }
            }));
        }

        // ---

        private void myS3PathLabel_TextChanged(object sender, EventArgs e)
        {
            if (editedSetup == null ||
                Tools.CanWriteToDirectory(
                    (myS3PathLabel.Text+@"\").Replace(@"\\", @"\")))
            {
                myS3PathLabel.ForeColor = Color.Green;
            }
            else
            {
                myS3PathLabel.Text = "Problem accessing previous selected folder";
                myS3PathLabel.ForeColor = Color.Firebrick;
            }
        }

        private void changeMyS3PathButton_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog folderBrowser = new FolderBrowserDialog();
            folderBrowser.Description = "Select or create MyS3 folder";
            folderBrowser.SelectedPath = myS3PathLabel.Text;
            folderBrowser.ShowNewFolderButton = true;
            folderBrowser.ShowDialog();

            myS3PathLabel.Text = folderBrowser.SelectedPath;

            CheckMyS3Setup();
        }

        private void resetMyS3PathButton_Click(object sender, EventArgs e)
        {
            myS3PathLabel.Text = Environment.ExpandEnvironmentVariables(
                MyS3Runner.DEFAULT_RELATIVE_LOCAL_MYS3_DIRECTORY_PATH);

            CheckMyS3Setup();
        }

        private void encryptionPasswordBox_TextChanged(object sender, EventArgs e)
        {
            if (encryptionPasswordBox.TextLength >= 16 ||
                    (encryptionPasswordBox.TextLength >= 8 &&
                     encryptionPasswordBox.Text.Any(char.IsUpper) && encryptionPasswordBox.Text.Any(char.IsLower) &&
                     encryptionPasswordBox.Text.Any(char.IsNumber)))
            {
                encryptionPasswordBox.BackColor = Color.LightGreen;
            }
            else if (encryptionPasswordBox.TextLength >= 8 ||
                       (encryptionPasswordBox.TextLength >= 6 &&
                        (encryptionPasswordBox.Text.Any(char.IsUpper) || encryptionPasswordBox.Text.Any(char.IsLower)) &&
                        encryptionPasswordBox.Text.Any(char.IsNumber)))
            {
                encryptionPasswordBox.BackColor = Color.Yellow;
            }
            else if (encryptionPasswordBox.TextLength > 0 && encryptionPasswordBox.TextLength < 8)
            {
                encryptionPasswordBox.BackColor = Color.LightPink;
            }
            else
            {
                encryptionPasswordBox.BackColor = DEFAULT_CONTROL_BACKGROUND_COLOR;
            }

            confirmEncryptionPassword1Box_TextChanged(null, null);
            confirmEncryptionPassword2Box_TextChanged(null, null);

            CheckMyS3Setup();
        }

        private void confirmEncryptionPassword1Box_TextChanged(object sender, EventArgs e)
        {
            if (confirmEncryptionPassword1Box.Text == encryptionPasswordBox.Text)
                confirmEncryptionPassword1Box.BackColor = Color.LightGreen;
            else
                confirmEncryptionPassword1Box.BackColor = DEFAULT_CONTROL_BACKGROUND_COLOR;

            CheckMyS3Setup();
        }

        private void confirmEncryptionPassword2Box_TextChanged(object sender, EventArgs e)
        {
            if (confirmEncryptionPassword2Box.Text == encryptionPasswordBox.Text)
                confirmEncryptionPassword2Box.BackColor = Color.LightGreen;
            else
                confirmEncryptionPassword2Box.BackColor = DEFAULT_CONTROL_BACKGROUND_COLOR;

            CheckMyS3Setup();
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            CheckMyS3Setup();
        }

        private void inUseBox_CheckedChanged(object sender, EventArgs e)
        {
            CheckMyS3Setup();
        }

        // ---

        private bool NewOrEditedSetup()
        {
            string region = regionBox.Text;
            if (region.Contains("/")) // if not set and ready yet
                region = region.Substring(region.LastIndexOf("/") + 2);

            MyS3Setup setup = new MyS3Setup()
            {
                Bucket = bucketBox.Text,
                Region = region,

                AwsAccessKeyID = awsAccessKeyIDBox.Text,
                AwsSecretAccessKey = awsSecretAccessKeyBox.Text,

                MyS3Path = myS3PathLabel.Text.Replace("'", ""),
                EncryptionPassword = encryptionPasswordBox.Text,
                SharedBucket = multipleClientsBox.Checked,

                InUseNow = inUseBox.Checked
            };

            return editedSetup == null || !setup.Equals(editedSetup);
        }

        private void CheckMyS3Setup()
        {

            bool goodMyS3Settings =
                myS3PathLabel.ForeColor != Color.Firebrick && // ok local file path
                encryptionPasswordBox.BackColor != DEFAULT_CONTROL_BACKGROUND_COLOR && // password set
                encryptionPasswordBox.Text == confirmEncryptionPassword1Box.Text && // password confirmed
                confirmEncryptionPassword1Box.Text == confirmEncryptionPassword2Box.Text; // " twice

            okSetupButton.Enabled = goodMyS3Settings && NewOrEditedSetup();
            removeSetupButton.Enabled = goodMyS3Settings && SetupStore.Entries.ContainsKey(bucketBox.Text);
        }

        private void okSetupButton_Click(object sender, EventArgs e)
        {
            awsGroup.Enabled = false;
            myS3Group.Enabled = false;

            string region = regionBox.Text;
            if (region.Contains("/")) // if not set and ready yet
                region = region.Substring(region.LastIndexOf("/") + 2);

            MyS3Setup setup = new MyS3Setup()
            {
                Bucket = bucketBox.Text,
                Region = region,

                AwsAccessKeyID = awsAccessKeyIDBox.Text,
                AwsSecretAccessKey = awsSecretAccessKeyBox.Text,

                MyS3Path = (myS3PathLabel.Text.Replace("'", "") + @"\").Replace(@"\\", @"\"), // needs a last \ in folder path
                EncryptionPassword = encryptionPasswordBox.Text,

                SharedBucket = multipleClientsBox.Checked,
                InUseNow = inUseBox.Checked
            };

            SetupStore.Add(setup);
            if (editedSetup.Bucket != setup.Bucket)
                SetupStore.Remove(editedSetup.Bucket);

            Client.MainForm.CreateEditSetupMenuItems();
            Client.MainForm.UseMyS3Setups();
            Close();
        }

        private void cancelSetupButton_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void removeSetupButton_Click(object sender, EventArgs e)
        {
            DialogResult dialogResult = new ConfirmBox(
                "Confirm",
                "Are you sure you want to remove this MyS3 setup?\n(S3 data is not removed and setup can be added back later)").ShowDialog();

            if (dialogResult == DialogResult.Yes)
            {
                SetupStore.Remove(
                    SetupStore.Entries[bucketBox.Text]
                );

                Client.MainForm.CreateEditSetupMenuItems();
                Client.MainForm.UseMyS3Setups();
                Close();
            }
        }
    }
}