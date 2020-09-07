namespace MyS3.GUI
{
    partial class MyS3SetupForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MyS3SetupForm));
            this.okSetupButton = new System.Windows.Forms.Button();
            this.cancelSetupButton = new System.Windows.Forms.Button();
            this.myS3Group = new System.Windows.Forms.GroupBox();
            this.multipleClientsBox = new System.Windows.Forms.CheckBox();
            this.inUseBox = new System.Windows.Forms.CheckBox();
            this.resetMyS3PathButton = new System.Windows.Forms.Button();
            this.changeMyS3PathButton = new System.Windows.Forms.Button();
            this.myS3PathLabel = new System.Windows.Forms.Label();
            this.label10 = new System.Windows.Forms.Label();
            this.label9 = new System.Windows.Forms.Label();
            this.confirmEncryptionPassword2Box = new System.Windows.Forms.TextBox();
            this.label8 = new System.Windows.Forms.Label();
            this.confirmEncryptionPassword1Box = new System.Windows.Forms.TextBox();
            this.label7 = new System.Windows.Forms.Label();
            this.label6 = new System.Windows.Forms.Label();
            this.encryptionPasswordBox = new System.Windows.Forms.TextBox();
            this.label5 = new System.Windows.Forms.Label();
            this.removeSetupButton = new System.Windows.Forms.Button();
            this.awsGroup = new System.Windows.Forms.GroupBox();
            this.testButton = new System.Windows.Forms.Button();
            this.iamGroup = new System.Windows.Forms.GroupBox();
            this.awsSecretAccessKeyBox = new System.Windows.Forms.TextBox();
            this.label4 = new System.Windows.Forms.Label();
            this.awsAccessKeyIDBox = new System.Windows.Forms.TextBox();
            this.label3 = new System.Windows.Forms.Label();
            this.s3Group = new System.Windows.Forms.GroupBox();
            this.regionBox = new System.Windows.Forms.ComboBox();
            this.label2 = new System.Windows.Forms.Label();
            this.bucketBox = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.myS3Group.SuspendLayout();
            this.awsGroup.SuspendLayout();
            this.iamGroup.SuspendLayout();
            this.s3Group.SuspendLayout();
            this.SuspendLayout();
            // 
            // okSetupButton
            // 
            this.okSetupButton.Enabled = false;
            this.okSetupButton.Location = new System.Drawing.Point(114, 643);
            this.okSetupButton.Margin = new System.Windows.Forms.Padding(4);
            this.okSetupButton.Name = "okSetupButton";
            this.okSetupButton.Size = new System.Drawing.Size(88, 26);
            this.okSetupButton.TabIndex = 16;
            this.okSetupButton.Text = "OK";
            this.okSetupButton.UseVisualStyleBackColor = true;
            this.okSetupButton.Click += new System.EventHandler(this.okSetupButton_Click);
            // 
            // cancelSetupButton
            // 
            this.cancelSetupButton.Location = new System.Drawing.Point(208, 643);
            this.cancelSetupButton.Margin = new System.Windows.Forms.Padding(4);
            this.cancelSetupButton.Name = "cancelSetupButton";
            this.cancelSetupButton.Size = new System.Drawing.Size(88, 26);
            this.cancelSetupButton.TabIndex = 17;
            this.cancelSetupButton.Text = "Cancel";
            this.cancelSetupButton.UseVisualStyleBackColor = true;
            this.cancelSetupButton.Click += new System.EventHandler(this.cancelSetupButton_Click);
            // 
            // myS3Group
            // 
            this.myS3Group.Controls.Add(this.multipleClientsBox);
            this.myS3Group.Controls.Add(this.inUseBox);
            this.myS3Group.Controls.Add(this.resetMyS3PathButton);
            this.myS3Group.Controls.Add(this.changeMyS3PathButton);
            this.myS3Group.Controls.Add(this.myS3PathLabel);
            this.myS3Group.Controls.Add(this.label10);
            this.myS3Group.Controls.Add(this.label9);
            this.myS3Group.Controls.Add(this.confirmEncryptionPassword2Box);
            this.myS3Group.Controls.Add(this.label8);
            this.myS3Group.Controls.Add(this.confirmEncryptionPassword1Box);
            this.myS3Group.Controls.Add(this.label7);
            this.myS3Group.Controls.Add(this.label6);
            this.myS3Group.Controls.Add(this.encryptionPasswordBox);
            this.myS3Group.Controls.Add(this.label5);
            this.myS3Group.Enabled = false;
            this.myS3Group.Location = new System.Drawing.Point(12, 325);
            this.myS3Group.Margin = new System.Windows.Forms.Padding(4);
            this.myS3Group.Name = "myS3Group";
            this.myS3Group.Padding = new System.Windows.Forms.Padding(4);
            this.myS3Group.Size = new System.Drawing.Size(485, 310);
            this.myS3Group.TabIndex = 9;
            this.myS3Group.TabStop = false;
            this.myS3Group.Text = "MyS3";
            // 
            // multipleClientsBox
            // 
            this.multipleClientsBox.AutoSize = true;
            this.multipleClientsBox.Location = new System.Drawing.Point(114, 274);
            this.multipleClientsBox.Name = "multipleClientsBox";
            this.multipleClientsBox.Size = new System.Drawing.Size(164, 19);
            this.multipleClientsBox.TabIndex = 101;
            this.multipleClientsBox.Text = "For use by multiple clients";
            this.multipleClientsBox.UseVisualStyleBackColor = true;
            this.multipleClientsBox.CheckedChanged += new System.EventHandler(this.checkBox1_CheckedChanged);
            // 
            // inUseBox
            // 
            this.inUseBox.AutoSize = true;
            this.inUseBox.Location = new System.Drawing.Point(292, 274);
            this.inUseBox.Name = "inUseBox";
            this.inUseBox.Size = new System.Drawing.Size(71, 19);
            this.inUseBox.TabIndex = 15;
            this.inUseBox.Text = "Use now";
            this.inUseBox.UseVisualStyleBackColor = true;
            this.inUseBox.CheckedChanged += new System.EventHandler(this.inUseBox_CheckedChanged);
            // 
            // resetMyS3PathButton
            // 
            this.resetMyS3PathButton.Location = new System.Drawing.Point(256, 57);
            this.resetMyS3PathButton.Margin = new System.Windows.Forms.Padding(4);
            this.resetMyS3PathButton.Name = "resetMyS3PathButton";
            this.resetMyS3PathButton.Size = new System.Drawing.Size(76, 26);
            this.resetMyS3PathButton.TabIndex = 11;
            this.resetMyS3PathButton.Text = "Reset";
            this.resetMyS3PathButton.UseVisualStyleBackColor = true;
            this.resetMyS3PathButton.Click += new System.EventHandler(this.resetMyS3PathButton_Click);
            // 
            // changeMyS3PathButton
            // 
            this.changeMyS3PathButton.Location = new System.Drawing.Point(172, 57);
            this.changeMyS3PathButton.Margin = new System.Windows.Forms.Padding(4);
            this.changeMyS3PathButton.Name = "changeMyS3PathButton";
            this.changeMyS3PathButton.Size = new System.Drawing.Size(76, 26);
            this.changeMyS3PathButton.TabIndex = 10;
            this.changeMyS3PathButton.Text = "Change";
            this.changeMyS3PathButton.UseVisualStyleBackColor = true;
            this.changeMyS3PathButton.Click += new System.EventHandler(this.changeMyS3PathButton_Click);
            // 
            // myS3PathLabel
            // 
            this.myS3PathLabel.ForeColor = System.Drawing.Color.Green;
            this.myS3PathLabel.Location = new System.Drawing.Point(172, 20);
            this.myS3PathLabel.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.myS3PathLabel.Name = "myS3PathLabel";
            this.myS3PathLabel.Size = new System.Drawing.Size(243, 33);
            this.myS3PathLabel.TabIndex = 100;
            this.myS3PathLabel.Text = "C:\\Users\\dreamy\\Documents\\MyS3";
            this.myS3PathLabel.TextChanged += new System.EventHandler(this.myS3PathLabel_TextChanged);
            // 
            // label10
            // 
            this.label10.AutoSize = true;
            this.label10.Location = new System.Drawing.Point(89, 20);
            this.label10.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label10.Name = "label10";
            this.label10.Size = new System.Drawing.Size(75, 15);
            this.label10.TabIndex = 100;
            this.label10.Text = "Local folder :";
            // 
            // label9
            // 
            this.label9.ForeColor = System.Drawing.Color.Firebrick;
            this.label9.Location = new System.Drawing.Point(172, 221);
            this.label9.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label9.Name = "label9";
            this.label9.Size = new System.Drawing.Size(254, 30);
            this.label9.TabIndex = 100;
            this.label9.Text = "    Note :  A lost key = undecryptable S3 data             So write it down and k" +
    "eep it safe!";
            this.label9.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // confirmEncryptionPassword2Box
            // 
            this.confirmEncryptionPassword2Box.Location = new System.Drawing.Point(172, 194);
            this.confirmEncryptionPassword2Box.Margin = new System.Windows.Forms.Padding(4);
            this.confirmEncryptionPassword2Box.MaxLength = 1000;
            this.confirmEncryptionPassword2Box.Name = "confirmEncryptionPassword2Box";
            this.confirmEncryptionPassword2Box.Size = new System.Drawing.Size(254, 23);
            this.confirmEncryptionPassword2Box.TabIndex = 14;
            this.confirmEncryptionPassword2Box.UseSystemPasswordChar = true;
            this.confirmEncryptionPassword2Box.TextChanged += new System.EventHandler(this.confirmEncryptionPassword2Box_TextChanged);
            // 
            // label8
            // 
            this.label8.AutoSize = true;
            this.label8.Location = new System.Drawing.Point(70, 197);
            this.label8.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label8.Name = "label8";
            this.label8.Size = new System.Drawing.Size(94, 15);
            this.label8.TabIndex = 100;
            this.label8.Text = "Confirm key #2 :";
            // 
            // confirmEncryptionPassword1Box
            // 
            this.confirmEncryptionPassword1Box.Location = new System.Drawing.Point(172, 163);
            this.confirmEncryptionPassword1Box.Margin = new System.Windows.Forms.Padding(4);
            this.confirmEncryptionPassword1Box.MaxLength = 1000;
            this.confirmEncryptionPassword1Box.Name = "confirmEncryptionPassword1Box";
            this.confirmEncryptionPassword1Box.Size = new System.Drawing.Size(254, 23);
            this.confirmEncryptionPassword1Box.TabIndex = 13;
            this.confirmEncryptionPassword1Box.UseSystemPasswordChar = true;
            this.confirmEncryptionPassword1Box.TextChanged += new System.EventHandler(this.confirmEncryptionPassword1Box_TextChanged);
            // 
            // label7
            // 
            this.label7.AutoSize = true;
            this.label7.Location = new System.Drawing.Point(70, 166);
            this.label7.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label7.Name = "label7";
            this.label7.Size = new System.Drawing.Size(94, 15);
            this.label7.TabIndex = 100;
            this.label7.Text = "Confirm key #1 :";
            // 
            // label6
            // 
            this.label6.ForeColor = System.Drawing.SystemColors.ControlDarkDark;
            this.label6.Location = new System.Drawing.Point(172, 135);
            this.label6.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(254, 15);
            this.label6.TabIndex = 100;
            this.label6.Text = "Preferably a sentence to get a strong password";
            this.label6.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // encryptionPasswordBox
            // 
            this.encryptionPasswordBox.Location = new System.Drawing.Point(172, 108);
            this.encryptionPasswordBox.Margin = new System.Windows.Forms.Padding(4);
            this.encryptionPasswordBox.MaxLength = 1000;
            this.encryptionPasswordBox.Name = "encryptionPasswordBox";
            this.encryptionPasswordBox.Size = new System.Drawing.Size(254, 23);
            this.encryptionPasswordBox.TabIndex = 12;
            this.encryptionPasswordBox.UseSystemPasswordChar = true;
            this.encryptionPasswordBox.TextChanged += new System.EventHandler(this.encryptionPasswordBox_TextChanged);
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(52, 111);
            this.label5.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(112, 15);
            this.label5.TabIndex = 100;
            this.label5.Text = "File encryption key :";
            // 
            // removeSetupButton
            // 
            this.removeSetupButton.Enabled = false;
            this.removeSetupButton.Location = new System.Drawing.Point(304, 643);
            this.removeSetupButton.Margin = new System.Windows.Forms.Padding(4);
            this.removeSetupButton.Name = "removeSetupButton";
            this.removeSetupButton.Size = new System.Drawing.Size(88, 26);
            this.removeSetupButton.TabIndex = 18;
            this.removeSetupButton.Text = "Remove";
            this.removeSetupButton.UseVisualStyleBackColor = true;
            this.removeSetupButton.Click += new System.EventHandler(this.removeSetupButton_Click);
            // 
            // awsGroup
            // 
            this.awsGroup.Controls.Add(this.testButton);
            this.awsGroup.Controls.Add(this.iamGroup);
            this.awsGroup.Controls.Add(this.s3Group);
            this.awsGroup.Location = new System.Drawing.Point(12, 12);
            this.awsGroup.Name = "awsGroup";
            this.awsGroup.Size = new System.Drawing.Size(487, 306);
            this.awsGroup.TabIndex = 1;
            this.awsGroup.TabStop = false;
            this.awsGroup.Text = "Amazon Web Services (AWS)";
            // 
            // testButton
            // 
            this.testButton.Enabled = false;
            this.testButton.Location = new System.Drawing.Point(172, 258);
            this.testButton.Name = "testButton";
            this.testButton.Size = new System.Drawing.Size(160, 23);
            this.testButton.TabIndex = 8;
            this.testButton.Text = "Test bucket access";
            this.testButton.UseVisualStyleBackColor = true;
            this.testButton.Click += new System.EventHandler(this.testButton_Click);
            // 
            // iamGroup
            // 
            this.iamGroup.Controls.Add(this.awsSecretAccessKeyBox);
            this.iamGroup.Controls.Add(this.label4);
            this.iamGroup.Controls.Add(this.awsAccessKeyIDBox);
            this.iamGroup.Controls.Add(this.label3);
            this.iamGroup.Location = new System.Drawing.Point(14, 131);
            this.iamGroup.Margin = new System.Windows.Forms.Padding(4);
            this.iamGroup.Name = "iamGroup";
            this.iamGroup.Padding = new System.Windows.Forms.Padding(4);
            this.iamGroup.Size = new System.Drawing.Size(459, 110);
            this.iamGroup.TabIndex = 5;
            this.iamGroup.TabStop = false;
            this.iamGroup.Text = "Identity and Access Management (IAM)";
            // 
            // awsSecretAccessKeyBox
            // 
            this.awsSecretAccessKeyBox.Location = new System.Drawing.Point(158, 70);
            this.awsSecretAccessKeyBox.Margin = new System.Windows.Forms.Padding(4);
            this.awsSecretAccessKeyBox.Name = "awsSecretAccessKeyBox";
            this.awsSecretAccessKeyBox.Size = new System.Drawing.Size(254, 23);
            this.awsSecretAccessKeyBox.TabIndex = 7;
            this.awsSecretAccessKeyBox.UseSystemPasswordChar = true;
            this.awsSecretAccessKeyBox.TextChanged += new System.EventHandler(this.awsSecretAccessKey_TextChanged);
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(17, 73);
            this.label4.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(133, 15);
            this.label4.TabIndex = 100;
            this.label4.Text = "AWS Secret Access Key :";
            // 
            // awsAccessKeyIDBox
            // 
            this.awsAccessKeyIDBox.Location = new System.Drawing.Point(158, 30);
            this.awsAccessKeyIDBox.Margin = new System.Windows.Forms.Padding(4);
            this.awsAccessKeyIDBox.Name = "awsAccessKeyIDBox";
            this.awsAccessKeyIDBox.Size = new System.Drawing.Size(254, 23);
            this.awsAccessKeyIDBox.TabIndex = 6;
            this.awsAccessKeyIDBox.TextChanged += new System.EventHandler(this.awsAccessKeyID_TextChanged);
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(38, 33);
            this.label3.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(112, 15);
            this.label3.TabIndex = 100;
            this.label3.Text = "AWS Access Key ID :";
            // 
            // s3Group
            // 
            this.s3Group.Controls.Add(this.regionBox);
            this.s3Group.Controls.Add(this.label2);
            this.s3Group.Controls.Add(this.bucketBox);
            this.s3Group.Controls.Add(this.label1);
            this.s3Group.Location = new System.Drawing.Point(14, 23);
            this.s3Group.Margin = new System.Windows.Forms.Padding(4);
            this.s3Group.Name = "s3Group";
            this.s3Group.Padding = new System.Windows.Forms.Padding(4);
            this.s3Group.Size = new System.Drawing.Size(459, 100);
            this.s3Group.TabIndex = 2;
            this.s3Group.TabStop = false;
            this.s3Group.Text = "S3 bucket";
            // 
            // regionBox
            // 
            this.regionBox.FormattingEnabled = true;
            this.regionBox.Items.AddRange(new object[] {
            "Africa (Cape Town) / af-south-1",
            "Asia Pacific (Hong Kong) / ap-east-1",
            "Asia Pacific (Mumbai) / ap-south-1",
            "Asia Pacific (Osaka-Local) / ap-northeast-3",
            "Asia Pacific (Seoul) / ap-northeast-2",
            "Asia Pacific (Singapore) / ap-southeast-1",
            "Asia Pacific (Sydney) / ap-southeast-2",
            "Asia Pacific (Tokyo) / ap-northeast-1",
            "Canada (Central) / ca-central-1",
            "China (Beijing) / cn-north-1",
            "China (Ningxia) / cn-northwest-1",
            "Europe (Frankfurt) / eu-central-1",
            "Europe (Ireland) / eu-west-1",
            "Europe (London) / eu-west-2",
            "Europe (Milan) / eu-south-1",
            "Europe (Paris) / eu-west-3",
            "Europe (Stockholm) / eu-north-1",
            "Middle East (Bahrain) / me-south-1",
            "South America (São Paulo) / sa-east-1",
            "US East (N. Virginia) / us-east-1",
            "US East (Ohio) / us-east-2",
            "US West (N. California) / us-west-1",
            "US West (Oregon) / us-west-2"});
            this.regionBox.Location = new System.Drawing.Point(158, 55);
            this.regionBox.Margin = new System.Windows.Forms.Padding(4);
            this.regionBox.Name = "regionBox";
            this.regionBox.Size = new System.Drawing.Size(254, 23);
            this.regionBox.TabIndex = 4;
            this.regionBox.DropDown += new System.EventHandler(this.regionBox_DropDown);
            this.regionBox.DropDownClosed += new System.EventHandler(this.regionBox_DropDownClosed);
            this.regionBox.TextChanged += new System.EventHandler(this.regionBox_TextChanged);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(100, 58);
            this.label2.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(50, 15);
            this.label2.TabIndex = 100;
            this.label2.Text = "Region :";
            // 
            // bucketBox
            // 
            this.bucketBox.Location = new System.Drawing.Point(158, 22);
            this.bucketBox.Margin = new System.Windows.Forms.Padding(4);
            this.bucketBox.Name = "bucketBox";
            this.bucketBox.Size = new System.Drawing.Size(254, 23);
            this.bucketBox.TabIndex = 3;
            this.bucketBox.TextChanged += new System.EventHandler(this.bucketBox_TextChanged);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(105, 25);
            this.label1.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(45, 15);
            this.label1.TabIndex = 100;
            this.label1.Text = "Name :";
            // 
            // MyS3SetupForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(512, 690);
            this.Controls.Add(this.awsGroup);
            this.Controls.Add(this.removeSetupButton);
            this.Controls.Add(this.myS3Group);
            this.Controls.Add(this.cancelSetupButton);
            this.Controls.Add(this.okSetupButton);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Margin = new System.Windows.Forms.Padding(4);
            this.MaximizeBox = false;
            this.Name = "MyS3SetupForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Setup";
            this.myS3Group.ResumeLayout(false);
            this.myS3Group.PerformLayout();
            this.awsGroup.ResumeLayout(false);
            this.iamGroup.ResumeLayout(false);
            this.iamGroup.PerformLayout();
            this.s3Group.ResumeLayout(false);
            this.s3Group.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion
        private System.Windows.Forms.Button okSetupButton;
        private System.Windows.Forms.Button cancelSetupButton;
        private System.Windows.Forms.GroupBox myS3Group;
        private System.Windows.Forms.TextBox confirmEncryptionPassword2Box;
        private System.Windows.Forms.Label label8;
        private System.Windows.Forms.TextBox confirmEncryptionPassword1Box;
        private System.Windows.Forms.Label label7;
        private System.Windows.Forms.TextBox encryptionPasswordBox;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.Label label9;
        private System.Windows.Forms.Button changeMyS3PathButton;
        private System.Windows.Forms.Label myS3PathLabel;
        private System.Windows.Forms.Label label10;
        private System.Windows.Forms.Button removeSetupButton;
        private System.Windows.Forms.Button resetMyS3PathButton;
        private System.Windows.Forms.CheckBox inUseBox;
        private System.Windows.Forms.Label label6;
        private System.Windows.Forms.GroupBox awsGroup;
        private System.Windows.Forms.Button testButton;
        private System.Windows.Forms.GroupBox iamGroup;
        private System.Windows.Forms.TextBox awsSecretAccessKeyBox;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.TextBox awsAccessKeyIDBox;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.GroupBox s3Group;
        private System.Windows.Forms.ComboBox regionBox;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.TextBox bucketBox;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.CheckBox multipleClientsBox;
    }
}