using System;
using System.Data;
using System.Text;
using System.Drawing;
using System.Windows.Forms;
using System.ComponentModel;
using System.Collections.Generic;
using System.Threading;

namespace MyS3.GUI
{
    public partial class InfoBox : Form
    {
        public InfoBox(string title, string content)
        {
            InitializeComponent();

            this.Text = title;
            label.Text = content;
        }

        private void okButton_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}
