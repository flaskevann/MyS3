using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;

using Xunit;
using Xunit.Abstractions;

namespace MyS3.Tests
{
    public class MyS3Tests
    {
        private ITestOutputHelper output;

        public MyS3Tests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void Base64SafeStringTest()
        {
            bool done = false;

            List<string> directoryPaths = Directory.GetDirectories(
                (Tools.RunningOnWindows() ? @"C:\" : "/")
            ).ToList<string>();

            for (int f = 0; f < directoryPaths.Count; f++)
            {
                string directoryPath = directoryPaths[f];

                // Add more directories
                try
                {
                    directoryPaths.AddRange(Directory.GetDirectories(directoryPath).ToList<string>());
                }
                catch (Exception) { }

                // Add more files
                try
                {
                    string[] filePaths = Directory.GetFiles(directoryPath);

                    foreach (string filePath in filePaths)
                    {
                        byte[] filePathAsBytes = Encoding.UTF8.GetBytes(filePath);

                        string filePathAsBase64 = Convert.ToBase64String(filePathAsBytes);
                        string filePathAsSafeBase64 = Tools.ToBase64SafeString(filePathAsBytes);

                        if (filePathAsBase64.Contains("/") || filePathAsBase64.Contains(@"\") || filePathAsBase64.Contains("+"))
                        {
                            Assert.Equal(filePath, Encoding.UTF8.GetString(Tools.FromBase64SafeString(filePathAsSafeBase64)));
                            output.WriteLine(filePath + " ==> " + filePathAsSafeBase64);

                            done = true;
                            break;
                        }
                    }
                }
                catch (Exception) { }

                if (done) break;
            }
        }

        [Fact]
        public void S3ObjectKeyWithMetadataTest()
        {
            string encryptionPassword = "This is a test encryption password!";

            string testFilePath = Path.GetFullPath("../../../test image.jpg");
            byte[] testFileData = File.ReadAllBytes(testFilePath);
            string fileHash = Tools.DataToHash(testFileData);
            DateTime lastModifiedUTC = File.GetLastWriteTimeUtc(testFilePath);

            // ---

            S3ObjectMetadata testS3ObjectMetadata = new S3ObjectMetadata(testFilePath, fileHash, testFileData.Length, lastModifiedUTC, encryptionPassword);
            string testS3ObjectKeyWithMetadata = testS3ObjectMetadata.ToString();

            output.WriteLine("Generated S3 object key with metadata:");
            output.WriteLine(testS3ObjectKeyWithMetadata);

            Assert.True(S3ObjectMetadata.IsValidS3ObjectKeyWithMetadata(testS3ObjectKeyWithMetadata));


            // ---

            S3ObjectMetadata reconstructedTestS3ObjectKey = new S3ObjectMetadata(testS3ObjectKeyWithMetadata, encryptionPassword);

            Assert.Equal(testFilePath, reconstructedTestS3ObjectKey.OfflineFilePathInsideMyS3);
            Assert.Equal(fileHash, reconstructedTestS3ObjectKey.FileHash);
            Assert.Equal(
                lastModifiedUTC.ToLongDateString() + " " + lastModifiedUTC.ToLongTimeString(),
                reconstructedTestS3ObjectKey.LastModifiedUTC.ToLongDateString() + " " + reconstructedTestS3ObjectKey.LastModifiedUTC.ToLongTimeString()
            );
            Assert.Equal(testFileData.Length, reconstructedTestS3ObjectKey.DecryptedSize);
        }
    }
}