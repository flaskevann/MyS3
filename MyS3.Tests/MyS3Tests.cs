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

            string testFilePath = @"Images\Wallpapers\Nature\&¤#¤&&¤¤4456TRTGBFGBG ERUYH¤JH.jpg";

            DateTime lastModifiedUTC = DateTime.UtcNow
                .AddSeconds(new Random().Next(0, int.MaxValue));

            long decryptedSize = new Random().Next(1000, int.MaxValue);

            // ---

            S3ObjectMetadata testS3ObjectMetadata = new S3ObjectMetadata(testFilePath, lastModifiedUTC, decryptedSize, encryptionPassword);
            string testS3ObjectKeyWithMetadata = testS3ObjectMetadata.ToString();
            output.WriteLine("Generated S3 object key with metadata: " + testS3ObjectKeyWithMetadata);


            // ---

            S3ObjectMetadata reconstructedTestS3ObjectKey = new S3ObjectMetadata(testS3ObjectKeyWithMetadata, encryptionPassword);

            Assert.Equal(testFilePath, reconstructedTestS3ObjectKey.OfflineFilePathInsideMyS3);
            Assert.Equal(lastModifiedUTC, reconstructedTestS3ObjectKey.LastModifiedUTC);
            Assert.Equal(decryptedSize, reconstructedTestS3ObjectKey.DecryptedSize);
        }
    }
}