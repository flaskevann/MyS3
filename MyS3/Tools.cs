using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;

using EncryptionAndHashingLibrary;

namespace MyS3
{
    public class Tools
    {
        public static string ReplaceCommaAndAddTrailingZero(string number)
        {
            if (number.Contains(",") && number.IndexOf(",") == number.Length - 2) number += "0"; // trailing zero if .? and not .??
            number = number.Replace(",", ".");

            return number;
        }

        // ---

        public static string GetByteSizeAsText(long s)
        {
            string text;

            double size = s;
            if (size < 1024) text = ReplaceCommaAndAddTrailingZero(Math.Round(size, 2) + "") + " B"; // <1 KB
            else if (size < 1024.0 * 1024) text = ReplaceCommaAndAddTrailingZero(Math.Round(size / 1024, 2) + "") + " KB"; // <1 MB
            else if (size < 1024.0 * 1024 * 1024) text = ReplaceCommaAndAddTrailingZero(Math.Round(size / (1024.0 * 1024), 2) + "") + " MB"; // <1 GB
            else if (size < 1024.0 * 1024 * 1024 * 1024) text = ReplaceCommaAndAddTrailingZero(Math.Round(size / (1024.0 * 1024 * 1024), 2) + "") + " GB"; // <1 TB
            else text = ReplaceCommaAndAddTrailingZero(Math.Round(size / (1024.0 * 1024 * 1024 * 1024), 2) + "") + " TB";

            return text;
        }

        public static string GetFileSizeAsText(string path)
        {
            return GetByteSizeAsText(new FileInfo(path).Length);
        }

        public static bool CanWriteToDirectory(string directoryPath)
        {
            try
            {
                string testFilename = DateTime.Now.Ticks + ".________________________";
                string testPath = directoryPath + @"\" + testFilename;
                File.WriteAllText(testPath, "test");
                File.Delete(testPath);

                return true;
            }
            catch (Exception) {}

            return false;
        }

        public static bool IsFileLocked(string filePath)
        {
            // Check last write time
            if (File.GetLastWriteTime(filePath).AddMilliseconds(5) >= DateTime.Now)
                return true;

            // Can file be opened = everything OK (Not reliable on *nix !)
            try
            {
                FileInfo file = new FileInfo(filePath);
                using (FileStream stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }
            }
            catch (Exception)
            {
                return true;
            }

            return false;
        }

        // ---

        private static string SETTINGS_DIRECTORY_PATH
        {
            get
            {
                return
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) +
                    (RunningOnWindows() ? @"\MyS3\" : @"/MyS3/");
            }
        }

        public static bool SettingsFileExists(string filename)
        {
            return File.Exists(SETTINGS_DIRECTORY_PATH + filename);
        }

        public static void WriteSettingsFile(string filename, byte[] data) // always overwrites
        {
            string path = SETTINGS_DIRECTORY_PATH + filename;

            // Create necessary directories
            string directories = RunningOnWindows() ?
                path.Substring(0, path.LastIndexOf(@"\") + 1) :
                path.Substring(0, path.LastIndexOf(@"/") + 1);
            if (!Directory.Exists(directories)) Directory.CreateDirectory(directories);

            // Encrypt and write to file
            byte[] encryptedData = AesEncryptionWrapper.EncryptWithGCM(
                data,
                EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, "MyS3")
            );
            File.WriteAllBytes(path, encryptedData);
        }

        public static byte[] ReadSettingsFile(string filename)
        {
            string path = SETTINGS_DIRECTORY_PATH + filename;

            // Read from file and decrypt
            byte[] encryptedData = File.ReadAllBytes(path);
            byte[] data = AesEncryptionWrapper.DecryptForGCM(
                encryptedData,
                EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, "MyS3")
            );

            return data;
        }

        // ---

        public static bool RunningOnWindows()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        // ---

        public static void Log(string content)
        {
            Console.WriteLine(DateTime.Now.ToLocalTime() + ": " + content);
        }

        public static void Log(string filePath, string content)
        {
            File.AppendAllText(filePath, DateTime.Now.ToLocalTime() + ": " + content + "\n");
        }

        // ---

        public static bool HasInternet()
        {
            try
            {
                using (var client = new WebClient())
                using (client.OpenRead("https://google.com/"))

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
