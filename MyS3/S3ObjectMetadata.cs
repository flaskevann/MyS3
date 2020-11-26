using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;

using EncryptionAndHashingLibrary;

namespace MyS3
{
    /*
     * Creates a S3 object key from:
     * 1) local MyS3 file path
     * 2) last modified date
     * and
     * 3) correct file size when file decrypted
     * 
     * This stops the need for adding metadata to each S3 object
     * 
     * Example of output:
     * 0AzALzU9d9taHefeDjruCnee6pmkOhPqw9EFEUY3FfSxooYGGfi2T8qQynk5ahXme6LGoFMVp7XxBCCPJ3MS3+TfiKhoLIfaSB+jLmBqAU1INk3m503hZPIMxxw=__5253572544000199471__1615133015
     */

    [Serializable]
    public class S3ObjectMetadata : IEquatable<S3ObjectMetadata>
    {
        public string OfflineFilePathInsideMyS3 { get { return offlineFilePathInsideMyS3; } }
        private string offlineFilePathInsideMyS3; // Beware: Always has the same type of slashes as used in the OS the user uses

        private string encryptedOfflinePathInsideMyS3AsSafeBase64;

        public DateTime LastModifiedUTC { get { return lastModifiedUTC; } }
        private DateTime lastModifiedUTC;

        public long DecryptedSize { get { return decryptedSize; } }
        private long decryptedSize;

        // --

        public S3ObjectMetadata(string offlineFilePathInsideMyS3, DateTime lastModifiedUTC, long decryptedSize, string encryptionPassword)
        {
            this.offlineFilePathInsideMyS3 = offlineFilePathInsideMyS3;
            this.lastModifiedUTC = lastModifiedUTC;
            this.decryptedSize = decryptedSize;

            encryptedOfflinePathInsideMyS3AsSafeBase64 =
                Tools.ToBase64SafeString(
                    AesEncryptionWrapper.EncryptWithGCM(
                        Encoding.UTF8.GetBytes(this.offlineFilePathInsideMyS3.Replace(@"/", @"\")), // Always and only \ used when made into S3 object key
                        EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
            ));
        }

        public S3ObjectMetadata(string s3ObjectKeyAsText, string encryptionPassword)
        {
            string[] pieces = s3ObjectKeyAsText.Split("__", StringSplitOptions.RemoveEmptyEntries);

            this.encryptedOfflinePathInsideMyS3AsSafeBase64 = pieces[0];
            this.offlineFilePathInsideMyS3 = Encoding.UTF8.GetString(
                AesEncryptionWrapper.DecryptForGCM(
                    Tools.FromBase64SafeString(this.encryptedOfflinePathInsideMyS3AsSafeBase64),
                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                )
            );
            if (!Tools.RunningOnWindows())
                this.offlineFilePathInsideMyS3 = this.offlineFilePathInsideMyS3.Replace(@"\", @"/");

            this.lastModifiedUTC = DateTime.FromBinary(long.Parse(pieces[1]));
            this.decryptedSize = long.Parse(pieces[2]);
        }

        public override string ToString() // This is the actual key used in S3!
        {                                 // It's given to each object with encrypted file data and it's never the same
            return
                encryptedOfflinePathInsideMyS3AsSafeBase64 + "__" +
                lastModifiedUTC.ToBinary() + "__" +
                decryptedSize;
        }

        public static bool IsValidS3ObjectKeyWithMetadata(string s3ObjectKeyAsText)
        {
            string[] pieces = s3ObjectKeyAsText.Split("__", StringSplitOptions.RemoveEmptyEntries);

            return pieces.Length == 3 && (pieces[1] + "").All(char.IsDigit) && (pieces[2] + "").All(char.IsDigit);
        }

        public bool Equals([AllowNull] S3ObjectMetadata other)
        {
            return other.ToString() == this.ToString();
        }
    }
}
