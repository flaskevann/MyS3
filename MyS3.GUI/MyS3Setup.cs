using System;
using System.Collections.Generic;
using System.Text;

namespace MyS3.GUI
{
    [Serializable]
    public class MyS3Setup
    {
        public string Bucket;
        public string Region;

        public string AwsAccessKeyID;
        public string AwsSecretAccessKey;

        public string MyS3Path;
        public string EncryptionPassword;
        public bool SharedBucket;

        public bool InUseNow;

        public override bool Equals(Object obj)
        {
            if ((obj == null) || !this.GetType().Equals(obj.GetType()))
            {
                return false;
            }
            else
            {
                MyS3Setup otherSetup = (MyS3Setup)obj;

                return
                    otherSetup.Bucket == this.Bucket &&
                    otherSetup.Region == this.Region &&
                    otherSetup.AwsAccessKeyID == this.AwsAccessKeyID &&
                    otherSetup.AwsSecretAccessKey == this.AwsSecretAccessKey &&
                    otherSetup.MyS3Path == this.MyS3Path &&
                    otherSetup.EncryptionPassword == this.EncryptionPassword &&
                    otherSetup.SharedBucket == this.SharedBucket &&
                    otherSetup.InUseNow == this.InUseNow;
            }
        }

        public override int GetHashCode()
        {
            return
                (Bucket + Region +
                 AwsAccessKeyID + AwsSecretAccessKey +
                 MyS3Path + EncryptionPassword + SharedBucket + InUseNow).GetHashCode();
        }
    }
}