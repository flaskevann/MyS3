using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using EncryptionAndHashingLibrary;

namespace MyS3
{
    /*
     * Creates a S3 object key from:
     * 1) local MyS3 file path
     * 2) MD5 hash of file
     * 3) last modified date
     * 4) number of bytes in file before and after encryption (used to create an identical file after download)
     * 
     * This stops the need for adding metadata to each S3 object and doing S3 metadata requests
     * 
     * Example of output (using 'test image.jpg' from unit test):
     * 0cAXlfnaDyrRM1Y90Ic3mOFcloYG8b6kcx+4kJ8q8SG38uzwShqB6xTrosxANrNfatN0vJa[FSLASH]hLmtHFTiA9gKD8TExHLGbH4OXM1U[FSLASH]MO4__C88A4E2590D18BAA9B032A167CB2A0BD__199320__5249140711309970830
     */

    [Serializable]
    public class S3ObjectMetadata : IEquatable<S3ObjectMetadata>
    {
        public string OfflineFilePathInsideMyS3 { get { return offlineFilePathInsideMyS3; } }
        private string offlineFilePathInsideMyS3; // Beware: Always has the same type of slashes as used in the OS the user uses
        private string encryptedOfflinePathInsideMyS3AsSafeBase64;

        public string FileHash { get { return fileHash; } }
        private string fileHash;

        public long DecryptedSize { get { return decryptedSize; } }
        private long decryptedSize;

        public DateTime LastModifiedUTC { get { return lastModifiedUTC; } }
        private DateTime lastModifiedUTC;

        // --

        public S3ObjectMetadata(string offlineFilePathInsideMyS3, string fileHash, long decryptedSize, DateTime lastModifiedUTC, string encryptionPassword)
        {
            this.offlineFilePathInsideMyS3 = offlineFilePathInsideMyS3;
            this.fileHash = fileHash;
            this.decryptedSize = decryptedSize;
            this.lastModifiedUTC = lastModifiedUTC;

            encryptedOfflinePathInsideMyS3AsSafeBase64 =
                Tools.ToBase64SafeString(
                    AesEncryptionWrapper.EncryptWithGCM(
                        Encoding.UTF8.GetBytes(this.offlineFilePathInsideMyS3.Replace(@"/", @"\")), // Always and only \ used when file path as text is made into a S3 object key
                        EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
            ));
        }

        public S3ObjectMetadata(string s3ObjectKeyAsText, string encryptionPassword)
        {
            string[] pieces = s3ObjectKeyAsText.Split("__", StringSplitOptions.RemoveEmptyEntries); // get metadata text strings

            this.encryptedOfflinePathInsideMyS3AsSafeBase64 = pieces[0];
            this.offlineFilePathInsideMyS3 = Encoding.UTF8.GetString(
                AesEncryptionWrapper.DecryptForGCM(
                    Tools.FromBase64SafeString(this.encryptedOfflinePathInsideMyS3AsSafeBase64),
                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                )
            );
            if (!Tools.RunningOnWindows())
                this.offlineFilePathInsideMyS3 = this.offlineFilePathInsideMyS3.Replace(@"\", @"/"); // always / used when inside S3, so fix this if *nix

            this.fileHash = pieces[1];

            this.decryptedSize = long.Parse(pieces[2]);

            this.lastModifiedUTC = DateTime.FromBinary(long.Parse(pieces[3]));
        }

        public override string ToString() // This is the actual key used in S3!
        {                                 // It's given to each object with encrypted file data and it's never the same
            return
                encryptedOfflinePathInsideMyS3AsSafeBase64 + "__" +
                fileHash + "__" +
                decryptedSize + "__" +
                lastModifiedUTC.ToBinary();
        }

        public static bool IsValidS3ObjectKeyWithMetadata(string s3ObjectKeyAsText)
        {
            string[] pieces = s3ObjectKeyAsText.Split("__", StringSplitOptions.RemoveEmptyEntries);

            return
                pieces.Length == 4 && // should be 4 pieces of metadata
                pieces[1].Length == 32 && // MD5 hash should be 32 chars
                (pieces[2] + "").All(char.IsDigit) && (pieces[3] + "").All(char.IsDigit); // file size and last modifed time should only be digits
        }

        public bool Equals(S3ObjectMetadata other)
        {
            return other.ToString() == this.ToString();
        }
    }
}
