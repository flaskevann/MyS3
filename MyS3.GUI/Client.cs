using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace MyS3.GUI
{
    public class Client : ApplicationContext
    {
        public static new MainForm MainForm { get { return mainForm; } }
        private static MainForm mainForm;

        private static MyS3SetupForm newSetupForm;
        private static MyS3SetupForm editSetupForm;

        private static NotifyIcon systray;

        public static readonly DateTime Startup;

        static Client()
        {
            Startup = DateTime.Now;

            // Systray's context menu
            ContextMenuStrip strip = new ContextMenuStrip();
            strip.Items.Add("Hide / Show", null, ShowMainFormOrHideEverything);
            strip.Items.Add("Quit", null, ShowMainFormAndQuit);

            // Systray icon
            systray = new NotifyIcon()
            {
                Icon = Icon.ExtractAssociatedIcon(
                    Assembly.GetExecutingAssembly().Location
                ),
                ContextMenuStrip = strip,
                Visible = true
            };

            // Show main form
            mainForm = new MainForm();
            mainForm.Show();
        }

        // ---

        public static void NewSetup()
        {
            if (newSetupForm == null || newSetupForm.IsDisposed)
                newSetupForm = new MyS3SetupForm();
            newSetupForm.Show();
        }

        public static void EditSetup(string bucket)
        {
            if (editSetupForm == null || editSetupForm.IsDisposed)
                editSetupForm = new MyS3SetupForm();
            editSetupForm.Show(bucket);
        }

        // ---

        // Systray

        public static void ShowMainFormOrHideEverything(object sender, EventArgs e)
        {
            if (mainForm.Visible)
            {
                if (newSetupForm != null) newSetupForm.Hide();
                if (editSetupForm != null) editSetupForm.Hide();

                mainForm.Hide();
            }
            else
            {
                mainForm.Show();
            }
        }

        public static void ShowMainFormAndQuit(object sender, EventArgs e)
        {
            // First show again
            if (!mainForm.Visible)
                mainForm.Show();

            // Then ask to quit
            mainForm.quitMenuItem_Click(sender, e);
        }

        public static void Quit(object sender, EventArgs e)
        {
            mainForm.Close();

            if (newSetupForm != null && !newSetupForm.IsDisposed)
                newSetupForm.Close();
            if (editSetupForm != null && !editSetupForm.IsDisposed)
                editSetupForm.Close();

            systray.Visible = false;

            Application.Exit();
            Environment.Exit(Environment.ExitCode);
        }
    }
}
