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
    public partial class ConfirmBox : Form
    {
        public ConfirmBox(string title, string content)
        {
            InitializeComponent();

            this.Text = title;
            label.Text = content;
        }

        private DialogResult dialogResult = DialogResult.No;

        public new DialogResult ShowDialog()
        {
            base.ShowDialog();

            return dialogResult;
        }

        private void yesButton_Click(object sender, EventArgs e)
        {
            dialogResult = DialogResult.Yes;
            Close();
        }

        private void noButton_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}
