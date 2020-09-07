using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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

        public static string GetByteSizeAsText(long s)
        {
            string text;

            double size = s;
            if (size < 1024) text = ReplaceCommaAndAddTrailingZero(Math.Round(size, 2)+"") + " B"; // <1 KB
            else if (size < 1024 * 1024) text = ReplaceCommaAndAddTrailingZero(Math.Round(size / 1024, 2)+"") + " KB"; // <1 MB
            else if (size < 1024 * 1024 * 1024) text = ReplaceCommaAndAddTrailingZero(Math.Round(size / (1024 * 1024), 2)+"") + " MB"; // <1 GB
            else text = ReplaceCommaAndAddTrailingZero(Math.Round(size / (1024 * 1024 * 1024), 2)+"") + " MB"; // <1 TB

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
                string testFilename = DateTime.Now.Ticks + "________________________";
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
            if (File.GetLastWriteTime(filePath).AddSeconds(1) >= DateTime.Now)
                return true;

            // Can file be opened = everything OK (DOES NOT WORK ON *NIX !)
            FileStream stream = null;
            var file = new FileInfo(filePath);
            try
            {
                stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return true;
            }
            finally
            {
                stream?.Close();
            }

            return false;
        }

        public static void Log(string content)
        {
            Console.WriteLine(DateTime.Now.ToLocalTime() + ": " + content);
        }

        public static void Log(string filePath, string content)
        {
            File.AppendAllText(filePath, DateTime.Now.ToLocalTime() + ": " + content + "\n");
        }

        public static bool RunningOnWindows()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }
    }
}
