using System;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;

namespace MyS3.GUI
{
    public static class Program
    {
        private static Mutex mutex;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool createdNew;
            mutex = new Mutex(true, "MyS3Mutex", out createdNew);

            if (createdNew)
                Application.Run(new Client());
            else
                (new InfoBox("MyS3", "Please use first instance instead of starting another.")).ShowDialog();
        }
    }
}