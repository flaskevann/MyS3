using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Collections.Immutable;

using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

using EncryptionAndHashingLibrary;
using System.ComponentModel;

namespace MyS3
{
    public class MyS3Runner : IDisposable
    {
        public static string DEFAULT_RELATIVE_LOCAL_MYS3_DIRECTORY_PATH
        {
            get
            {
                return Tools.RunningOnWindows() ?
                    @"%userprofile%\Documents\MyS3\" :
                    @"%HOME%/Documents/MyS3/";
            }
        }

        public static string RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH
        {
            get
            {
                return Tools.RunningOnWindows() ? @"MyS3 temp\" : @"MyS3 temp/";
            }
        }

        public static string RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH
        {
            get
            {
                return Tools.RunningOnWindows() ? @"MyS3 logs\" : @"MyS3 logs/";
            }
        }

        public static string RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH
        {
            get
            {
                return Tools.RunningOnWindows()? @"MyS3 restored files\" : @"MyS3 restored files/";
            }
        }

        public static readonly string[] IGNORED_DIRECTORIES_NAMES = new string[]
        {
            RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH.Replace(@"\", "").Replace(@"/", ""),
            RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH.Replace(@"\", "").Replace(@"/", ""),
            RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH.Replace(@"\", "").Replace(@"/", "")
        };

        public static readonly string[] IGNORED_FILE_EXTENSIONS = new string[]
        {
            ".db",
            ".ini"
        };

        // ---

        public readonly string Bucket;
        private readonly string region;

        private readonly string awsAccessKeyID;
        private readonly string awsSecretAccessKey;

        public string MyS3Path { get { return myS3Path; } }
        private string myS3Path;

        private readonly string encryptionPassword;

        private readonly bool sharedBucketWithMoreComparisons;

        public Action<string> VerboseLogFunc { get { return verboseLogFunc; } set { verboseLogFunc = value; } }
        private Action<string> verboseLogFunc;
        public Action<string, string> ErrorLogFunc { get { return errorLogFunc; } set { errorLogFunc = value; } }
        private Action<string, string> errorLogFunc;

        // ---

        private FileMonitor fileMonitor;
        private S3Wrapper s3;

        private Dictionary<string, string> myS3Files = new Dictionary<string, string>(); // MyS3 file paths ==> S3 object paths

        public ImmutableList<string> UploadQueue
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(uploadQueue, 10, ref acquiredLock);
                    if (acquiredLock)
                        return uploadQueue.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(uploadQueue);
                }
            }
        }
        private List<string> changedQueue = new List<string>(); // MyS3 file paths
        private List<string> uploadQueue = new List<string>(); // MyS3 file paths
        private List<string> uploadedList = new List<string>(); // MyS3 file paths

        public ImmutableList<string> DownloadQueue
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(downloadQueue, 10, ref acquiredLock);
                    if (acquiredLock)
                        return downloadQueue.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(downloadQueue);
                }
            }
        }
        private List<string> downloadQueue = new List<string>(); // S3 object paths
        private List<string> downloadedList = new List<string>(); // S3 object paths

        public ImmutableList<string> NamedDownloadQueue
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(namedDownloadQueue, 10, ref acquiredLock);
                    if (acquiredLock)
                        return namedDownloadQueue.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(namedDownloadQueue);
                }
            }
        }
        private List<string> namedDownloadQueue = new List<string>(); // MyS3 file paths
        private Dictionary<string, string> renameQueue = new Dictionary<string, string>(); // MyS3 new file paths ==> old file paths
        private List<string> renamedList = new List<string>(); // MyS3 new file paths

        public ImmutableList<string> RemoveQueue
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(removeQueue, 10, ref acquiredLock);
                    if (acquiredLock)
                        return removeQueue.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(removeQueue);
                }
            }
        }
        private List<string> removeQueue = new List<string>(); // MyS3 file paths

        public ImmutableList<string> RestoreDownloadQueue
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(restoreDownloadQueue, 10, ref acquiredLock);
                    if (acquiredLock)
                        return restoreDownloadQueue.Keys.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(restoreDownloadQueue);
                }
            }
        }
        private Dictionary<string, List<string>> restoreDownloadQueue = new Dictionary<string, List<string>>(); // s3 file paths ==> versionIds

        public ImmutableList<string> NamedRestoreDownloadQueue
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(namedRestoreDownloadQueue, 10, ref acquiredLock);
                    if (acquiredLock)
                        return namedRestoreDownloadQueue.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(namedRestoreDownloadQueue);
                }
            }
        }
        private List<string> namedRestoreDownloadQueue = new List<string>(); // MyS3 file paths

        private static readonly int PAUSE_BETWEEN_EACH_S3_AND_MYS3_COMPARISON_CHECK = 50;

        private static readonly int PAUSE_BETWEEN_EACH_S3_AND_MYS3_COMPARISON_WHEN_SHARED_BUCKET_IN_SECONDS = 60; // 1 minute between each comparison check

        private static readonly int PAUSE_BETWEEN_EACH_S3_OPERATION = 1;
        private static readonly int INACTIVE_PAUSE = 1000;

        public MyS3Runner(string bucket, string region,
            string awsAccessKeyID, string awsSecretAccessKey,
            string myS3Path, // null allowed = use default path
            string encryptionPassword,
            bool sharedBucketWithMoreComparisons,
            Action<string> verboseLogFunc, Action<string, string> errorLogFunc)
        {       // content                          path, content

            this.Bucket = bucket;
            this.region = region;

            this.awsAccessKeyID = awsAccessKeyID;
            this.awsSecretAccessKey = awsSecretAccessKey;

            this.myS3Path = myS3Path;
            this.encryptionPassword = encryptionPassword;

            this.sharedBucketWithMoreComparisons = sharedBucketWithMoreComparisons;

            this.verboseLogFunc = verboseLogFunc;
            this.errorLogFunc = errorLogFunc;
        }

        public void Setup() // call before Start()
        {
            // Setup main directory
            if (myS3Path == null) myS3Path = Environment.ExpandEnvironmentVariables(DEFAULT_RELATIVE_LOCAL_MYS3_DIRECTORY_PATH);
            if (!Directory.Exists(myS3Path))
            {
                Directory.CreateDirectory(myS3Path);

                if (verboseLogFunc != null)
                    verboseLogFunc("Created MyS3 folder \"" + myS3Path + "\"");
            }
            if (!Directory.Exists(myS3Path))
                throw new DirectoryNotFoundException(
                    "Aborting. Unable to use path \"" + myS3Path + "\" for MyS3");

            // Setup necessary directories
            foreach (string directory in IGNORED_DIRECTORIES_NAMES)
            {
                if (!Directory.Exists(myS3Path + directory))
                {
                    Directory.CreateDirectory(myS3Path + directory);

                    if (RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH.StartsWith(directory))
                        File.SetAttributes(myS3Path + directory,
                            File.GetAttributes(myS3Path + directory) | FileAttributes.Hidden);
                }
                if (!Directory.Exists(myS3Path + directory))
                    throw new DirectoryNotFoundException(
                        "Aborting. Unable to use path \"" + (myS3Path + directory) + "\" for MyS3");
            }

            // Clean up work directory
            try
            {
                foreach (string path in Directory.GetFiles(myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH))
                    File.Delete(path);
            }
            catch (Exception) { }
                        
            if (verboseLogFunc != null)
                verboseLogFunc("MyS3 setup at \"" + myS3Path + "\"");
        }

        public void Start() // call after Setup()
        {
            // Setup s3 interface
            RegionEndpoint endpoint = RegionEndpoint.GetBySystemName(region);
            s3 = new S3Wrapper(Bucket, endpoint, awsAccessKeyID, awsSecretAccessKey);
            if (verboseLogFunc != null)
                verboseLogFunc("Using bucket \"" + Bucket + "\" in region \"" + endpoint.DisplayName + "\"");
            if (s3.GetVersioningAsync().VersioningConfig.Status != VersionStatus.Enabled &&
                s3.SetVersioningAsync().HttpStatusCode != HttpStatusCode.OK)
                throw new Exception("Unable to set versioning on bucket and saving removed or overwritten files");

            // Clean up
            int numberOfFiles = s3.CleanUpRemovedObjects(DateTime.Now.AddYears(-1));
            if (numberOfFiles > 0 && verboseLogFunc != null)
                verboseLogFunc("Cleaned up after some very old removed files in bucket");

            // Start watching files
            fileMonitor = new FileMonitor(myS3Path,
                IGNORED_DIRECTORIES_NAMES, IGNORED_FILE_EXTENSIONS,
                OnChangedFileHandler, OnRenamedFileHandler, OnRemovedFileOrDirectoryHandler);
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                fileMonitor.Start();
            }));
            if (verboseLogFunc != null)
                verboseLogFunc("Started monitoring MyS3 folder and waiting for files to upload or download");

            // Start workers
            StartLastWriteTimeFixerForChangedFiles();
            StartComparingS3AndMyS3();
            StartDownloadWorker();
            StartUploadWorker();
            StartRenameWorker();
            StartRemoveWorker();
        }

        // ---

        public bool WrongEncryptionPassword { get { return wrongEncryptionPassword; } }
        private bool wrongEncryptionPassword = false;

        public bool DownloadsPaused { get { return pauseDownloads; } }
        private bool pauseDownloads = false;

        public void PauseDownloads(bool pause)
        {
            if (!pause && wrongEncryptionPassword) return;

            this.pauseDownloads = pause;

            string text = pauseDownloads ? "MyS3 downloads set to paused" : "MyS3 downloads set to continue";
            if (verboseLogFunc != null) verboseLogFunc(text);
        }

        public bool UploadsPaused { get { return pauseUploads; } }
        private bool pauseUploads = false;

        public void PauseUploads(bool pause)
        {
            if (!pause && wrongEncryptionPassword) return;

            this.pauseUploads = pause;

            string text = pauseUploads ? "MyS3 uploads set to paused" : "MyS3 uploads set to continue";
            if (verboseLogFunc != null) verboseLogFunc(text);
        }

        public void Pause(bool pause)
        {
            PauseDownloads(pause);
            PauseUploads(pause);
        }

        public bool restorePaused { get { return pauseRestore; } }
        private bool pauseRestore = false;

        public void PauseRestore(bool pause)
        {
            this.pauseRestore = pause;

            string text = pauseRestore ? "MyS3 restores set to paused" : "MyS3 restores set to continue";
            if (verboseLogFunc != null) verboseLogFunc(text);
        }

        public bool Stopping { get { return stop; } }
        private volatile bool stop = false;

        public void Stop()
        {
            lock (fileMonitor)
                fileMonitor.Stop();

            Pause(true);
            stop = true;

            if (verboseLogFunc != null)
                verboseLogFunc("MyS3 terminated, goodbye!");
        }

        // ---

        public int NumberOfFiles
        {
            get
            {
                return Directory.GetFiles(myS3Path, "*", SearchOption.AllDirectories).Length;
            }
        }

        public long GetTotalFileSize()
        {
            long size = 0;
            foreach (string offlineFilePath in Directory.GetFiles(myS3Path, "*", SearchOption.AllDirectories))
                size += (new FileInfo(offlineFilePath)).Length;
            return size;
        }

        public void TriggerS3AndMyS3Comparison()
        {
            newS3AndMyS3ComparisonNeeded = true;
        }
        private bool newS3AndMyS3ComparisonNeeded;
        private DateTime timeLastActivity;

        public bool IsComparingMyS3AndS3
        {
            get { return isComparingMyS3AndS3; }
        }
        private bool isComparingMyS3AndS3 = false;

        // ---

        public void OnChangedFileHandler(string offlineFilePath) // on file creation and change
        {
            // Get paths
            string offlineFilePathInsideMyS3 = offlineFilePath.Replace(myS3Path, "");
            string s3FilePath = Convert.ToBase64String(
                HashWrapper.CreateSHA2Hash(
                    Encoding.UTF8.GetBytes(offlineFilePathInsideMyS3.Replace("/", @"\")))).Replace(@"\", "").Replace("/", ""); // mys3 file path ==> hash as s3 key

            // ---

            // Skip if paused or file in use or removed
            if (UploadsPaused) return;
            lock (uploadQueue) if (uploadQueue.Contains(offlineFilePathInsideMyS3)) return;
            lock (uploadedList) if (uploadedList.Contains(s3FilePath)) return;
            lock (downloadQueue) if (downloadQueue.Contains(s3FilePath)) return;
            lock (downloadedList) if (downloadedList.Contains(s3FilePath)) return;
            lock (removeQueue) if (removeQueue.Contains(offlineFilePathInsideMyS3)) return;
            lock (renameQueue) if (renameQueue.ContainsKey(offlineFilePathInsideMyS3)) return;
            lock (renamedList) if (renamedList.Contains(offlineFilePathInsideMyS3)) return;

            // Index file
            lock (myS3Files)
                if (!myS3Files.ContainsKey(offlineFilePathInsideMyS3))
                    myS3Files.Add(offlineFilePathInsideMyS3, s3FilePath);

            // Set new write time to make file "new"
            // This blocks MyS3's (wrongful) removal of files that it thinks should be removed
            // Files that the user copies back into MyS3 despite having removed them in the past
            lock (changedQueue)
                if (!changedQueue.Contains(offlineFilePathInsideMyS3))
                    changedQueue.Add(offlineFilePathInsideMyS3);

            // Add upload
            lock (uploadQueue)
                if (!uploadQueue.Contains(offlineFilePathInsideMyS3))
                {
                    uploadQueue.Add(offlineFilePathInsideMyS3);

                    if (verboseLogFunc != null)
                        verboseLogFunc("Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                            "\" added to upload queue [" + uploadQueue.Count + "]");
                }
        }

        public void OnRenamedFileHandler(string oldOfflineFilePath, string newOfflineFilePath)
        {
            // Get paths
            string newOfflineFilePathInsideMyS3 = newOfflineFilePath.Replace(myS3Path, "");
            string oldOfflineFilePathInsideMyS3 = oldOfflineFilePath.Replace(myS3Path, "");
            string newS3FilePath = Convert.ToBase64String(
                HashWrapper.CreateSHA2Hash(
                    Encoding.UTF8.GetBytes(newOfflineFilePathInsideMyS3.Replace("/", @"\")))).Replace(@"\", "").Replace("/", ""); // mys3 file path ==> hash as s3 key

            // ---

            // Cancel old rename
            lock (renameQueue)
                if (renameQueue.ContainsKey(oldOfflineFilePathInsideMyS3))
                    renameQueue.Remove(oldOfflineFilePathInsideMyS3);

            // Cancel upload
            lock (uploadQueue)
                if (uploadQueue.Contains(oldOfflineFilePathInsideMyS3))
                    uploadQueue.Remove(oldOfflineFilePathInsideMyS3);

            // Index file
            lock (myS3Files)
                if (myS3Files.ContainsKey(oldOfflineFilePathInsideMyS3))
                {
                    myS3Files.Remove(oldOfflineFilePathInsideMyS3);
                    if (!myS3Files.ContainsKey(newOfflineFilePathInsideMyS3))
                        myS3Files.Add(newOfflineFilePathInsideMyS3, newS3FilePath);
                }

            // Do rename
            lock (renameQueue)
            {
                if (!renameQueue.ContainsKey(newOfflineFilePathInsideMyS3))
                {
                    renameQueue.Add(newOfflineFilePathInsideMyS3, oldOfflineFilePathInsideMyS3);

                    if (verboseLogFunc != null)
                        verboseLogFunc("S3 object for local file \"" + oldOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                            "\" ==> \"" + newOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                "\" added to rename queue [" + renameQueue.Count + "]");
                }
            }
        }

        public void OnRemovedFileOrDirectoryHandler(string offlinePath, bool directoryInsteadOfFile)
        {
            // Get paths
            string offlinePathInsideMyS3 = offlinePath.Replace(myS3Path, "");
            string s3FilePath = Convert.ToBase64String(
                HashWrapper.CreateSHA2Hash(
                    Encoding.UTF8.GetBytes(offlinePathInsideMyS3))).Replace(@"\", "").Replace("/", ""); // mys3 file path ==> hash as s3 key

            // Handle deleted directory
            if (directoryInsteadOfFile)
            {
                lock (myS3Files)
                foreach (KeyValuePair<string, string> kvp in myS3Files)
                {
                    string offlinePathInsideMyS3FromList = kvp.Key;
                    if (offlinePathInsideMyS3FromList.StartsWith(offlinePathInsideMyS3))
                        OnRemovedFileOrDirectoryHandler(myS3Path + offlinePathInsideMyS3FromList, false);
                }
                return;
            }

            // ---

            lock (myS3Files)
                if (myS3Files.ContainsKey(offlinePathInsideMyS3))
                    myS3Files.Remove(offlinePathInsideMyS3);

            // Cancel rename
            lock (renameQueue)
                if (renameQueue.Values.Contains(offlinePathInsideMyS3))
                    renameQueue.Remove(offlinePathInsideMyS3);

            // Cancel upload
            lock (uploadQueue)
                if (uploadQueue.Contains(offlinePathInsideMyS3))
                    uploadQueue.Remove(offlinePathInsideMyS3);

            // Do removal
            lock (removeQueue)
                if (!removeQueue.Contains(offlinePathInsideMyS3))
                {
                    removeQueue.Add(offlinePathInsideMyS3);

                    if (verboseLogFunc != null)
                        verboseLogFunc("S3 object for local file \"" + offlinePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                            "\" added to removal queue [" + removeQueue.Count + "]");
                }
        }

        // ---

        public enum TransferType { DOWNLOAD, UPLOAD, RESTORE }

        public void OnTransferProgress(string shownFilePath, long transferredBytes, long totalBytes, TransferType transferType)
        {
            double percentDone = (((double) transferredBytes / (double) totalBytes) * 100);
            percentDone = Math.Round(percentDone, 2);

            string typeText = null;
            switch (transferType)
            {
                case TransferType.DOWNLOAD:
                    DownloadPercent = percentDone;
                    typeText = "downloading";
                    break;
                case TransferType.UPLOAD:
                    UploadPercent = percentDone;
                    typeText = "uploading";
                    break;
                case TransferType.RESTORE:
                    RestoreDownloadPercent = percentDone;
                    typeText = "restoring";
                    break;
            }

            string text = ("Local file \"" + shownFilePath.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" " + typeText + " [" +
                Tools.GetByteSizeAsText(transferredBytes) + " / " + Tools.GetByteSizeAsText(totalBytes) + "][" +
                Tools.ReplaceCommaAndAddTrailingZero(percentDone + "") + " %]");
            if (verboseLogFunc != null) verboseLogFunc(text);
        }

        // ---

        private void StartLastWriteTimeFixerForChangedFiles()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                while (!stop)
                {
                    string offlineFilePathInsideMyS3 = null;
                    lock (changedQueue)
                        if (changedQueue.Count > 0)
                            offlineFilePathInsideMyS3 = changedQueue[0];

                    if (offlineFilePathInsideMyS3 == null)
                    {
                        Thread.Sleep(INACTIVE_PAUSE);
                    }
                    else
                    {
                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;

                        if (File.Exists(offlineFilePath))
                        {
                            if (!Tools.IsFileLocked(offlineFilePath))
                            {
                                File.SetLastWriteTime(offlineFilePath, DateTime.Now);
                                lock (changedQueue)
                                    changedQueue.Remove(offlineFilePathInsideMyS3);
                            }
                        }
                        else
                        {
                            lock (changedQueue)
                                changedQueue.Remove(offlineFilePathInsideMyS3);
                        }

                        Thread.Sleep(10);
                    }
                }
            }));
        }

        private void StartComparingS3AndMyS3()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) => {

                DateTime timeLastCompare = DateTime.UnixEpoch;

                while (!stop)
                {
                    // Compare after activity finishes
                    bool activityDone = false;
                    lock (uploadQueue)
                    {
                        lock (downloadQueue)
                        {
                            lock (renameQueue)
                            {
                                lock (removeQueue)
                                {
                                    if (uploadQueue.Count != 0 || downloadQueue.Count != 0 ||
                                        renameQueue.Count != 0 || removeQueue.Count != 0) timeLastActivity = DateTime.Now;

                                    activityDone = (uploadQueue.Count == 0 && downloadQueue.Count == 0 &&
                                        renameQueue.Count == 0 && removeQueue.Count == 0);
                                }
                            }
                        }
                    }
                    if (activityDone && timeLastActivity.AddSeconds(3) < DateTime.Now) // Give S3 a little time to finish last activity
                    {
                        // Run S3 and MyS3 comparison very often if shared bucket
                        if (sharedBucketWithMoreComparisons &&
                            timeLastCompare.AddSeconds(PAUSE_BETWEEN_EACH_S3_AND_MYS3_COMPARISON_WHEN_SHARED_BUCKET_IN_SECONDS) < DateTime.Now)
                                newS3AndMyS3ComparisonNeeded = true;

                        if (verboseLogFunc != null)
                            verboseLogFunc("Comparing S3 objects and MyS3 folder contents [" + NumberOfFiles + " " + (NumberOfFiles == 1 ? "file" : "files") +
                                " = " + Tools.GetByteSizeAsText(GetTotalFileSize()) + "]");

                        // New change or comparison requested
                        isComparingMyS3AndS3 = true;
                        if (Directory.GetLastWriteTime(myS3Path) > timeLastCompare || newS3AndMyS3ComparisonNeeded)
                        {
                            newS3AndMyS3ComparisonNeeded = false;

                            // Index every MyS3 file again to be sure (MyS3 path ==> S3 hashed key)
                            lock (myS3Files)
                            {
                                myS3Files.Clear();
                                foreach (string offlineFilePath in Directory.GetFiles(myS3Path, "*", SearchOption.AllDirectories))
                                {
                                    // Skip log and work folders
                                    if (offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH) ||
                                        offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH) ||
                                        offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH)) continue;

                                    // Ignore certain file extensions
                                    if (IGNORED_FILE_EXTENSIONS.Contains(Path.GetExtension(offlineFilePath))) continue;

                                    // ---

                                    // Get paths
                                    string offlineFilePathInsideMyS3 = offlineFilePath.Replace(myS3Path, "");
                                    string s3FilePath = Convert.ToBase64String(
                                        HashWrapper.CreateSHA2Hash(
                                            Encoding.UTF8.GetBytes(offlineFilePathInsideMyS3.Replace("/", @"\")))).Replace(@"\", "").Replace("/", ""); // mys3 file path ==> hash as s3 key

                                    // Add local file name ==> hashed S3 key
                                    myS3Files.Add(offlineFilePathInsideMyS3, s3FilePath);
                                }
                            }

                            // ---

                            // Remove files locally when already removed in S3 from elsewhere
                            Dictionary<string, DateTime> removedFiles = s3.GetCompleteRemovedObjectList();
                            foreach (string offlineFilePath in Directory.GetFiles(myS3Path, "*", SearchOption.AllDirectories))
                            {
                                // Skip log and work folders
                                if (offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH) ||
                                    offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH) ||
                                    offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH)) continue;

                                // Ignore certain file extensions
                                if (IGNORED_FILE_EXTENSIONS.Contains(Path.GetExtension(offlineFilePath))) continue;

                                // ---

                                // Get paths
                                string offlineFilePathInsideMyS3 = offlineFilePath.Replace(myS3Path, "");
                                string s3FilePath = Convert.ToBase64String(
                                    HashWrapper.CreateSHA2Hash(
                                        Encoding.UTF8.GetBytes(offlineFilePathInsideMyS3.Replace("/", @"\")))).Replace(@"\", "").Replace("/", ""); // mys3 file path ==> hash as s3 key

                                // Remove local file if older and not currently uploading because it's new
                                lock (uploadQueue)
                                if (removedFiles.ContainsKey(s3FilePath) && removedFiles[s3FilePath] > File.GetLastWriteTime(offlineFilePath) &&
                                    !uploadQueue.Contains(offlineFilePathInsideMyS3) && !Tools.IsFileLocked(offlineFilePath))
                                {
                                    // Remove file
                                    File.Delete(offlineFilePath);

                                    // Remove empty directories
                                    string path = Directory.GetParent(myS3Path + offlineFilePathInsideMyS3).FullName;
                                    while ((path + (Tools.RunningOnWindows() ? @"\" : @"/")) != myS3Path)
                                    {
                                        if (Directory.Exists(path) &&
                                            Directory.GetFiles(path).Length == 0 &&
                                            Directory.GetDirectories(path).Length == 0)
                                        {
                                            Directory.Delete(path, false);

                                            path = Directory.GetParent(path).FullName;
                                        }
                                        else
                                        {
                                            break;
                                        }
                                    }
                                }
                            }

                            // ---

                            // Finally do the file comparison, S3 objects against MyS3 files and vice versa
                            List<S3Object> s3ObjectsInfo = s3.GetCompleteObjectList();
                            if (s3ObjectsInfo != null)
                            {
                                // 1. Make searchable S3 object list
                                Dictionary<string, S3Object> s3Files = s3ObjectsInfo.ToDictionary(x => x.Key, x => x);

                                // 2. Compare and find needed uploads
                                if (!UploadsPaused)
                                    foreach (string offlineFilePath in Directory.GetFiles(myS3Path, "*", SearchOption.AllDirectories))
                                    {
                                        // Skip log and work folders
                                        if (offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH) ||
                                            offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH) ||
                                            offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH)) continue;

                                        // Ignore certain file extensions
                                        if (IGNORED_FILE_EXTENSIONS.Contains(Path.GetExtension(offlineFilePath))) continue;

                                        // ---

                                        // Get paths
                                        string offlineFilePathInsideMyS3 = offlineFilePath.Replace(myS3Path, "");
                                        string s3FilePath = Convert.ToBase64String(
                                            HashWrapper.CreateSHA2Hash(
                                                Encoding.UTF8.GetBytes(offlineFilePathInsideMyS3.Replace("/", @"\")))).Replace(@"\", "").Replace("/", ""); // mys3 file path ==> hash as s3 key

                                        // Offline file already uploaded?
                                        if (s3Files.ContainsKey(s3FilePath))
                                        {
                                            // Last change
                                            DateTime offlineFileTimeLastChange = File.GetLastWriteTime(offlineFilePath);
                                            DateTime s3FileTimeLastChange = s3Files[s3FilePath].LastModified; // local time ..

                                            // Newer file locally?
                                            if (offlineFileTimeLastChange > s3FileTimeLastChange)
                                                lock (uploadQueue)
                                                    lock (downloadQueue)
                                                        lock (renameQueue)
                                                            lock (removeQueue)
                                                                if (!uploadQueue.Contains(offlineFilePathInsideMyS3) &&
                                                                    !downloadQueue.Contains(s3FilePath) &&
                                                                    !renameQueue.ContainsKey(offlineFilePathInsideMyS3) &&
                                                                    !removeQueue.Contains(offlineFilePathInsideMyS3))
                                                                {
                                                                    uploadQueue.Add(offlineFilePathInsideMyS3);

                                                                    if (verboseLogFunc != null)
                                                                        verboseLogFunc("Found updated local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                                                                "\" so added it to upload queue [" + uploadQueue.Count + "]");
                                                                }
                                        }

                                        // New file offline?
                                        else if (File.Exists(offlineFilePath))
                                            lock (uploadQueue)
                                                lock (downloadQueue)
                                                    lock (renameQueue)
                                                        lock (removeQueue)
                                                            if (!uploadQueue.Contains(offlineFilePathInsideMyS3) &&
                                                                !downloadQueue.Contains(s3FilePath) &&
                                                                !renameQueue.ContainsKey(offlineFilePathInsideMyS3) &&
                                                                !removeQueue.Contains(offlineFilePathInsideMyS3))
                                                            {
                                                                uploadQueue.Add(offlineFilePathInsideMyS3);

                                                                if (verboseLogFunc != null)
                                                                    verboseLogFunc("Found new local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                                                                "\" so added it to upload queue [" + uploadQueue.Count + "]");
                                                            }

                                        // Give other threads access to locked queues
                                        Thread.Sleep(PAUSE_BETWEEN_EACH_S3_AND_MYS3_COMPARISON_CHECK);
                                    }

                                // 3. Compare and find needed downloads
                                if (!DownloadsPaused)
                                    foreach (KeyValuePair<string, S3Object> kvp in s3Files)
                                    {
                                        // Get paths
                                        string s3FilePath = kvp.Key;

                                        try
                                        {
                                            // Metadata
                                            MetadataCollection metadata = s3.GetMetadata(s3FilePath, null).Result.Metadata;

                                            // Get paths
                                            string offlineFilePathInsideMyS3 = null;
                                            try
                                            {
                                                offlineFilePathInsideMyS3 = Encoding.UTF8.GetString(
                                                    AesEncryptionWrapper.DecryptForGCM(
                                                        Convert.FromBase64String(metadata["x-amz-meta-encryptedfilepath"]),
                                                        EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                                                ));
                                            }
                                            catch (CryptographicException ex)
                                            {
                                                Pause(true);
                                                wrongEncryptionPassword = true;

                                                string problem = "S3 object \"" + s3FilePath.Substring(0, 10) + "****" +
                                                    "\" cannot be read because of wrong encryption/decryption password - \"" + ex.Message + "\"";

                                                verboseLogFunc(problem);
                                            }
                                            if (!Tools.RunningOnWindows()) offlineFilePathInsideMyS3 = offlineFilePathInsideMyS3.Replace(@"\", @"/");
                                            string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;

                                            // Offline file exists?
                                            if (myS3Files.Values.Contains(s3FilePath))
                                            {
                                                // Last change
                                                DateTime offlineFileTimeLastChange = File.GetLastWriteTime(offlineFilePath);
                                                DateTime s3FileTimeLastChange = kvp.Value.LastModified; // local time ..

                                                // Is S3 object newer?
                                                if (s3FileTimeLastChange > offlineFileTimeLastChange)
                                                    lock (uploadQueue)
                                                        lock (downloadQueue)
                                                            lock (renameQueue)
                                                                lock (removeQueue)
                                                                    if (!uploadQueue.Contains(offlineFilePathInsideMyS3) &&
                                                                        !downloadQueue.Contains(s3FilePath) &&
                                                                        !renameQueue.ContainsKey(offlineFilePathInsideMyS3) &&
                                                                        !removeQueue.Contains(offlineFilePathInsideMyS3))
                                                                    {
                                                                        downloadQueue.Add(s3FilePath);

                                                                        if (verboseLogFunc != null)
                                                                            verboseLogFunc("Found updated S3 object so added it to download queue [" + downloadQueue.Count + "]");
                                                                    }
                                            }

                                            // Offline file doesn't exist
                                            else if (!myS3Files.Values.Contains(s3FilePath))
                                            {
                                                lock (uploadQueue)
                                                    lock (downloadQueue)
                                                        lock (renameQueue)
                                                            lock (removeQueue)
                                                                if (!uploadQueue.Contains(offlineFilePathInsideMyS3) &&
                                                                    !downloadQueue.Contains(s3FilePath) &&
                                                                    !renameQueue.ContainsKey(offlineFilePathInsideMyS3) &&
                                                                    !removeQueue.Contains(offlineFilePathInsideMyS3))
                                                                {
                                                                    downloadQueue.Add(s3FilePath);

                                                                    if (verboseLogFunc != null)
                                                                        verboseLogFunc("Found new S3 object so added it to download queue [" + downloadQueue.Count + "]");
                                                                }
                                            }
                                        }
                                        catch (Exception) { }

                                        // Give other threads access to locked queues
                                        Thread.Sleep(PAUSE_BETWEEN_EACH_S3_AND_MYS3_COMPARISON_CHECK);
                                    }
                            }
                            else
                            {
                                if (verboseLogFunc != null)
                                    verboseLogFunc("Unable to get S3 object listing.");
                            }

                            // ---

                            isComparingMyS3AndS3 = false;

                            timeLastCompare = DateTime.Now;
                        }
                    }

                    Thread.Sleep(INACTIVE_PAUSE);
                }
            }));
        }

        // ---

        private NetworkSpeedCalculator uploadSpeedCalc = new NetworkSpeedCalculator();

        public double UploadPercent;
        public double UploadSpeed; // bytes / sec
        public long UploadSize; // bytes
        public long Uploaded; // bytes

        private void StartUploadWorker()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                while (!stop)
                {
                    int uploadCounter = 1;
                    lock (uploadedList) uploadedList.Clear();

                    bool hasUploads = false;
                    lock (uploadQueue) hasUploads = uploadQueue.Count > 0;
                    while (hasUploads && !pauseUploads)
                    {
                        // Get paths
                        string offlineFilePathInsideMyS3;
                        lock (uploadQueue)
                            offlineFilePathInsideMyS3 = uploadQueue[0];
                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                        string s3FilePath = Convert.ToBase64String(
                            HashWrapper.CreateSHA2Hash(
                                Encoding.UTF8.GetBytes(offlineFilePathInsideMyS3.Replace(@"/", @"\")))).Replace(@"\", "").Replace("/", ""); // mys3 file path ==> hash as s3 key
                        string encryptedFilePathTemp = null;

                        // Check existence
                        if (!File.Exists(offlineFilePath))
                        {
                            // Remove from queue
                            lock (uploadQueue)
                            {
                                if (uploadQueue.Contains(offlineFilePathInsideMyS3))
                                    uploadQueue.Remove(offlineFilePathInsideMyS3);

                                hasUploads = uploadQueue.Count > 0;
                            }

                            continue;
                        }

                        try
                        {
                            // Skip if file in use
                            if (Tools.IsFileLocked(offlineFilePath)) continue;

                            // Get paths
                            encryptedFilePathTemp = myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH + 
                            Path.GetFileName(offlineFilePath) + "." +
                                new Random().Next(0, int.MaxValue) + "." +
                                    (DateTime.Now - new DateTime(1970, 1, 1)).Ticks + ".ENCRYPTED";

                            // Encrypt file data
                            byte[] fileData = File.ReadAllBytes(offlineFilePath);
                            byte[] encryptedData = AesEncryptionWrapper.EncryptWithGCM(
                                fileData,
                                EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                            );
                            File.WriteAllBytes(encryptedFilePathTemp, encryptedData);

                            // Decryption test
                            byte[] decryptedUploadFileData = AesEncryptionWrapper.DecryptForGCM(
                                File.ReadAllBytes(encryptedFilePathTemp),
                                EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                            );
                            byte[] shrinkedDecryptedUploadFileData = new byte[fileData.Length];
                            Array.Copy(decryptedUploadFileData, 0, shrinkedDecryptedUploadFileData, 0, shrinkedDecryptedUploadFileData.Length);
                            if (!fileData.SequenceEqual(shrinkedDecryptedUploadFileData))
                                throw new CryptographicException(
                                    "Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" fails to be encrypted without corruption");

                            // Encrypt file path
                            string encryptedFilePath = Convert.ToBase64String(
                                AesEncryptionWrapper.EncryptWithGCM(
                                    Encoding.UTF8.GetBytes(offlineFilePathInsideMyS3.Replace("/", @"\")),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                            ));

                            // Metadata
                            MetadataCollection metadata = new MetadataCollection();
                            metadata.Add("x-amz-meta-decryptedsize", fileData.Length + "");
                            metadata.Add("x-amz-meta-encryptedfilepath", encryptedFilePath); // must be updated when renaming

                            // Set progress info
                            UploadPercent = 0;
                            UploadSize = new FileInfo(offlineFilePath).Length;
                            uploadSpeedCalc.Start(fileData.Length);

                            if (verboseLogFunc != null)
                                lock(uploadQueue)
                                    verboseLogFunc("Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" starts uploading [" +
                                        uploadCounter + "/" + (uploadCounter + uploadQueue.Count - 1) + "][" + Tools.GetFileSizeAsText(encryptedFilePathTemp) + "]");

                            // Start upload
                            CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
                            Task uploadTask = s3.UploadAsync(encryptedFilePathTemp, s3FilePath, offlineFilePathInsideMyS3,
                                metadata, cancelTokenSource.Token, OnTransferProgress);
                            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
                            {
                                try
                                {
                                    uploadTask.Wait();
                                }
                                catch (Exception)
                                {
                                    if (verboseLogFunc != null)
                                        verboseLogFunc("Local file \"" + 
                                            offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" upload aborted");
                                }
                            }));

                            // Wait and maybe abort
                            DateTime timeTransferProgressShown = DateTime.Now;
                            while (!uploadTask.IsCompleted && !uploadTask.IsCanceled)
                                if (File.Exists(offlineFilePath) && !pauseUploads)
                                {
                                    Thread.Sleep(25);

                                    // Manual progress estimation
                                    if (fileData.Length < 5 * 1024 * 1024) // <5 MB upload = no S3Wrapper progress report
                                    {
                                        double newUploadPercent = UploadPercent +
                                            100 * ((UploadSpeed * 25 / 1000) / fileData.Length); // will always be zero first upload
                                        if (newUploadPercent > 100) newUploadPercent = 100;
                                        UploadPercent = newUploadPercent;

                                        // Fix missing transfer progress report
                                        if (UploadSpeed != 0 &&
                                            (DateTime.Now - timeTransferProgressShown).TotalMilliseconds >= S3Wrapper.TRANSFER_EVENT_PAUSE_MILLISECONDS)
                                        {
                                            timeTransferProgressShown = DateTime.Now;

                                            OnTransferProgress(offlineFilePathInsideMyS3,
                                                (long)((UploadPercent / 100.0) * fileData.Length), fileData.Length, TransferType.UPLOAD);
                                        }
                                    }
                                }
                                else
                                {
                                    cancelTokenSource.Cancel();
                                }

                            // Upload complete, finish work locally
                            if (uploadTask.IsCompletedSuccessfully && File.Exists(offlineFilePath))
                            {
                                // Set progress info
                                UploadSpeed = uploadSpeedCalc.Stop();
                                UploadPercent = 100;
                                Uploaded += new FileInfo(encryptedFilePathTemp).Length;
                                if (verboseLogFunc != null)
                                    verboseLogFunc("Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" uploaded");

                                // Set last change locally
                                File.SetLastWriteTime(offlineFilePath, s3.GetMetadata(s3FilePath, null).Result.LastModified.ToLocalTime()); // to stop re-download

                                uploadCounter++;

                                // Add to special blocked queue - in case of slow file activity handling
                                lock (uploadedList)
                                    uploadedList.Add(s3FilePath);

                                // Remove from work queue
                                lock (uploadQueue)
                                    if (uploadQueue.Contains(offlineFilePathInsideMyS3))
                                        uploadQueue.Remove(offlineFilePathInsideMyS3);
                            }
                        }
                        catch (Exception ex)
                        {
                            string problem = "Problem uploading local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" - \"" + ex.Message + "\"";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            else errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Upload.log", problem);
                        }
                        finally
                        {
                            if (File.Exists(encryptedFilePathTemp)) File.Delete(encryptedFilePathTemp);
                        }

                        lock (uploadQueue) hasUploads = uploadQueue.Count > 0;

                        Thread.Sleep(PAUSE_BETWEEN_EACH_S3_OPERATION);
                    }

                    Thread.Sleep(INACTIVE_PAUSE);
                }
            }));
        }

        private NetworkSpeedCalculator downloadSpeedCalc = new NetworkSpeedCalculator();

        public double DownloadPercent;
        public double DownloadSpeed; // bytes / sec
        public long DownloadSize; // bytes
        public long Downloaded; // bytes

        private void StartDownloadWorker()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                while (!stop)
                {
                    int downloadCounter = 1;
                    lock (downloadedList) downloadedList.Clear();

                    bool hasDownloads = false;
                    lock (downloadQueue) hasDownloads = downloadQueue.Count > 0;
                    while (hasDownloads && !pauseDownloads)
                    {
                        // Get paths
                        string s3FilePath;
                        lock (downloadQueue)
                            s3FilePath = downloadQueue[0];
                        string offlineFilePathInsideMyS3 = null;
                        string encryptedFilePathTemp = null;

                        try
                        {
                            // Get metadata
                            GetObjectMetadataResponse metadataResult = s3.GetMetadata(s3FilePath, null).Result;
                            MetadataCollection metadata = metadataResult.Metadata;

                            // Get paths
                            offlineFilePathInsideMyS3 = Encoding.UTF8.GetString(
                                AesEncryptionWrapper.DecryptForGCM(
                                    Convert.FromBase64String(metadata["x-amz-meta-encryptedfilepath"]),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                            ));
                            if (!Tools.RunningOnWindows()) offlineFilePathInsideMyS3 = offlineFilePathInsideMyS3.Replace(@"\", @"/");
                            string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                            encryptedFilePathTemp = myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH + Path.GetFileName(offlineFilePath) + "." +
                                new Random().Next(0, int.MaxValue) + "." + (DateTime.Now - new DateTime(1970, 1, 1)).Ticks + ".ENCRYPTED";

                            // Abort if changed or busy
                            if (File.Exists(offlineFilePath) &&
                               (Tools.IsFileLocked(offlineFilePath) || File.GetLastWriteTime(offlineFilePath) > metadataResult.LastModified.ToLocalTime()))
                            {
                                // Remove from queue
                                lock (downloadQueue)
                                    downloadQueue.Remove(s3FilePath);

                                continue;
                            }

                            // Set progress info
                            DownloadPercent = 0;
                            DownloadSize = long.Parse(metadata["x-amz-meta-decryptedsize"]);
                            downloadSpeedCalc.Start(DownloadSize);
                            lock (namedDownloadQueue) namedDownloadQueue.Add(offlineFilePathInsideMyS3);

                            if (verboseLogFunc != null)
                                lock (downloadQueue)
                                    verboseLogFunc("S3 object for \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" starts downloading [" + downloadCounter + "/" + (downloadCounter + downloadQueue.Count - 1) +
                                            "][" + Tools.GetByteSizeAsText(metadataResult.Headers.ContentLength) + "]");

                            // Start download
                            CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
                            Task downloadTask = s3.DownloadAsync(encryptedFilePathTemp, s3FilePath, null, offlineFilePathInsideMyS3,
                                cancelTokenSource.Token, OnTransferProgress, TransferType.DOWNLOAD);
                            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
                            {
                                try
                                {
                                    downloadTask.Wait();
                                }
                                catch (Exception)
                                {
                                    if (verboseLogFunc != null)
                                        verboseLogFunc("S3 object download for \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" aborted");
                                }
                            }));

                            // Wait and maybe abort
                            while (!downloadTask.IsCompleted && !downloadTask.IsCanceled)
                                if (pauseDownloads) cancelTokenSource.Cancel();
                                else Thread.Sleep(25);

                            // Download complete
                            if (downloadTask.IsCompletedSuccessfully)
                            {
                                // Set progress info
                                DownloadSpeed = downloadSpeedCalc.Stop();
                                DownloadPercent = 100;
                                Downloaded += new FileInfo(encryptedFilePathTemp).Length;

                                // Decrypt file and resize array
                                byte[] decryptedFileData = AesEncryptionWrapper.DecryptForGCM(
                                    File.ReadAllBytes(encryptedFilePathTemp),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                                );
                                int correctFileDataLength = int.Parse(metadata["x-amz-meta-decryptedsize"]);
                                byte[] fileData = new byte[correctFileDataLength];
                                Array.Copy(decryptedFileData, 0, fileData, 0, fileData.Length);

                                // Create necessary directories
                                string directories = Tools.RunningOnWindows() ?
                                    offlineFilePath.Substring(0, offlineFilePath.LastIndexOf(@"\") + 1) :
                                    offlineFilePath.Substring(0, offlineFilePath.LastIndexOf(@"/") + 1);
                                if (!Directory.Exists(directories)) Directory.CreateDirectory(directories);

                                // Finish file work
                                File.WriteAllBytes(offlineFilePath, fileData);
                                File.SetLastWriteTime(offlineFilePath, metadataResult.LastModified.ToLocalTime());

                                // Add to file list
                                lock (myS3Files)
                                    if (!myS3Files.ContainsKey(offlineFilePathInsideMyS3))
                                        myS3Files.Add(offlineFilePathInsideMyS3, s3FilePath);

                                if (verboseLogFunc != null)
                                    verboseLogFunc("Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" reconstructed");

                                downloadCounter++;

                                // Add to block queue - in case of slow file activity handling
                                lock (downloadedList)
                                    downloadedList.Add(s3FilePath);

                                // Remove from work queue
                                lock (downloadQueue)
                                {
                                    lock (namedDownloadQueue)
                                    {
                                        downloadQueue.Remove(s3FilePath);
                                        namedDownloadQueue.Remove(offlineFilePathInsideMyS3);
                                    }
                                }
                            }
                        }
                        catch (CryptographicException)
                        {
                            string problem = "S3 object \"" + s3FilePath.Substring(0, 10) + "****" + "\" [" +
                                downloadCounter + "/" + (downloadCounter + downloadQueue.Count - 1) + "]" + " failed to be decrypted. Wrong encryption/decryption password?";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Decryption.log", problem);
                        }
                        catch (Exception ex) // Happens when trying to download deleted files = thrown when trying to first get metadata
                        {
                            string problem = "S3 object \"" + s3FilePath.Substring(0, 10) + "****" + "\" [" +
                                downloadCounter + "/" + (downloadCounter + downloadQueue.Count - 1) + "]" + " not downloaded: " + ex.Message;

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Download.log", problem);
                        }
                        finally
                        {
                            if (File.Exists(encryptedFilePathTemp)) File.Delete(encryptedFilePathTemp);
                        }

                        lock (downloadQueue) hasDownloads = downloadQueue.Count > 0;

                        Thread.Sleep(PAUSE_BETWEEN_EACH_S3_OPERATION);
                    }

                    Thread.Sleep(INACTIVE_PAUSE);
                }
            }));
        }

        private void StartRenameWorker()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                while (!stop)
                {
                    int renameCounter = 1;
                    lock (renamedList) renamedList.Clear();

                    bool mustRename = false;
                    lock (renameQueue) mustRename = renameQueue.Count > 0;
                    while (mustRename)
                    {
                        // Get paths
                        KeyValuePair<string, string> kvp;
                        lock (renameQueue) kvp = renameQueue.First();
                        string newOfflineFilePathInsideMyS3 = kvp.Key;
                        string newOfflineFilePath = myS3Path + newOfflineFilePathInsideMyS3;
                        string newS3FilePath = Convert.ToBase64String(
                            HashWrapper.CreateSHA2Hash(
                                Encoding.UTF8.GetBytes(newOfflineFilePathInsideMyS3.Replace("/", @"\")))).Replace(@"\", "").Replace("/", ""); // mys3 file path ==> hash as s3 key
                        string oldOfflineFilePathInsideMyS3 = kvp.Value;
                        string oldOfflineFilePath = myS3Path + oldOfflineFilePathInsideMyS3;
                        string oldS3FilePath = Convert.ToBase64String(
                            HashWrapper.CreateSHA2Hash(
                                Encoding.UTF8.GetBytes(oldOfflineFilePathInsideMyS3.Replace("/", @"\")))).Replace(@"\", "").Replace("/", ""); // mys3 file path ==> hash as s3 key

                        // Skip if busy
                        if (Tools.IsFileLocked(newOfflineFilePath)) continue;

                        MetadataCollection metadata;
                        bool renameSuccess = false; // First problem: S3 object to be copied may not exist yet, because upload have not finished / did not finish
                        try
                        {
                            // Get metadata
                            metadata = s3.GetMetadata(oldS3FilePath, null).Result.Metadata;
                            string newEncryptedFilePath = Convert.ToBase64String(
                                AesEncryptionWrapper.EncryptWithGCM(
                                    Encoding.UTF8.GetBytes(newOfflineFilePathInsideMyS3.Replace("/", @"\")),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                            ));
                            metadata.Add("x-amz-meta-encryptedfilepath", newEncryptedFilePath);

                            // Copy and remove
                            s3.CopyAsync(oldS3FilePath, newS3FilePath, metadata).Wait();
                            s3.RemoveAsync(oldS3FilePath, null).Wait();
                            renameSuccess = true;
                        }
                        catch (Exception ex)
                        {
                            string problem = "Problem renaming S3 object \"" + oldS3FilePath.Substring(0, 10) + "****" + "\"" + " for local file \"" +
                                oldOfflineFilePathInsideMyS3 + "\" to " + "\"" + newS3FilePath.Substring(0, 10) + "****" + "\" for \"" + newOfflineFilePathInsideMyS3 + "\" - \"" + ex.Message + "\"";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            else errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Rename.log", problem);

                            OnChangedFileHandler(newOfflineFilePath);
                        }

                        try
                        {
                            // Rename complete
                            if (renameSuccess)
                            {
                                if (File.Exists(newOfflineFilePath)) // Second problem: Local file may not exist any longer
                                {
                                    // Finish by setting last changed time
                                    File.SetLastWriteTime(
                                        newOfflineFilePath,
                                        s3.GetMetadata(newS3FilePath, null).Result.LastModified.ToLocalTime()
                                    ); // copy last changed time = stop re-download                                

                                    if (verboseLogFunc != null)
                                        lock (renameQueue)
                                            verboseLogFunc("S3 object for local file \"" + newOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" renamed [" +
                                                    renameCounter + "/" + (renameCounter + renameQueue.Count - 1) + "]");

                                    renameCounter++;

                                    // Add to block queue - in case of slow file activity handling
                                    lock (renamedList)
                                        renamedList.Add(newS3FilePath);

                                    // Remove from work queue
                                    lock (renameQueue)
                                        if (renameQueue.ContainsKey(newOfflineFilePathInsideMyS3))
                                            renameQueue.Remove(newOfflineFilePathInsideMyS3);
                                }
                                else
                                {
                                    s3.RemoveAsync(newS3FilePath, null).Wait(); // Clean up because local file removed
                                }
                            }
                        }
                        catch (Exception) {} // If file not found in S3

                        lock (renameQueue) mustRename = renameQueue.Count > 0;

                        Thread.Sleep(PAUSE_BETWEEN_EACH_S3_OPERATION);
                    }

                    Thread.Sleep(INACTIVE_PAUSE);
                }
            }));
        }

        private void StartRemoveWorker()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                while (!stop)
                {
                    int removeCounter = 1;
                    bool mustRemove = false;
                    lock (removeQueue) mustRemove = removeQueue.Count > 0;
                    while (mustRemove)
                    {
                        // Get paths
                        string offlineFilePathInsideMyS3 = null;
                        lock(removeQueue) offlineFilePathInsideMyS3 = removeQueue[0];
                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                        string s3FilePath = Convert.ToBase64String(
                            HashWrapper.CreateSHA2Hash(
                                Encoding.UTF8.GetBytes(offlineFilePathInsideMyS3.Replace("/", @"\")))).Replace(@"\", "").Replace("/", ""); // mys3 file path ==> hash as s3 key

                        // Remove file from S3
                        bool regularFileRemoval = true;
                        lock (myS3Files)
                            regularFileRemoval = myS3Files.ContainsKey(offlineFilePathInsideMyS3);
                        if (regularFileRemoval)
                        try
                        {
                            // Remove
                            s3.RemoveAsync(s3FilePath, null).Wait();

                            if (verboseLogFunc != null)
                                lock (removeQueue)
                                verboseLogFunc("S3 object for local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" removed [" +
                                        removeCounter + "/" + (removeCounter + removeQueue.Count - 1) + "]");

                            removeCounter++;

                            // Remove from work queue
                            lock (removeQueue)
                            {
                                if (removeQueue.Contains(offlineFilePathInsideMyS3))
                                    removeQueue.Remove(offlineFilePathInsideMyS3);

                                mustRemove = removeQueue.Count > 0;
                            }
                            }
                            catch (Exception ex)
                        {
                            string problem = "Problem removing S3 object \"" + s3FilePath.Substring(0, 10) + "****" + "\" for local file \"" +
                                offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" - \"" + ex.Message + "\"";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            else errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Remove.log", problem);
                        }

                        Thread.Sleep(PAUSE_BETWEEN_EACH_S3_OPERATION);
                    }

                    Thread.Sleep(INACTIVE_PAUSE);
                }
            }));
        }

        // ---

        private NetworkSpeedCalculator restoreSpeedCalc = new NetworkSpeedCalculator();

        public double RestoreDownloadPercent;
        public double RestoreDownloadSpeed; // bytes / sec
        public long RestoreDownloadSize; // bytes
        public long RestoreDownloaded; // bytes

        public void RestoreFiles(DateTime earliestLastModified, bool onlyRestoreLastRemoved)
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) => {

                // Get all keys and versions
                List<S3ObjectVersion> completeFileVersionsList = s3.GetCompleteObjectVersionsList(null);

                // Restore last removed MyS3 objects
                if (onlyRestoreLastRemoved)
                {
                    // Get all delete markers
                    Dictionary<string, List<S3ObjectVersion>> restoreQueueTemp = new Dictionary<string, List<S3ObjectVersion>>(); // key ==> delete markers
                    foreach (S3ObjectVersion version in completeFileVersionsList)
                    {
                        if (version.IsDeleteMarker && version.LastModified >= earliestLastModified)
                        {
                            if (restoreQueueTemp.ContainsKey(version.Key))
                                restoreQueueTemp[version.Key].Add(version);
                            else
                                restoreQueueTemp.Add(version.Key, new List<S3ObjectVersion>() { version });
                        }
                    }

                    // Find and remove newest delete markers
                    foreach (KeyValuePair<string, List<S3ObjectVersion>> kvp in restoreQueueTemp)
                    {
                        List<S3ObjectVersion> versions = kvp.Value;
                        S3ObjectVersion newestVersion = null;

                        foreach (S3ObjectVersion version in versions)
                            if (newestVersion == null || version.LastModified > newestVersion.LastModified)
                                newestVersion = version;

                        lock (restoreDownloadQueue)
                            if (!restoreDownloadQueue.ContainsKey(kvp.Key))
                                restoreDownloadQueue.Add(kvp.Key, new List<string>() { newestVersion.VersionId } );
                    }

                    bool hasRestores = false;
                    lock (restoreDownloadQueue) hasRestores = restoreDownloadQueue.Count > 0;
                    if (hasRestores)
                    {
                        if (verboseLogFunc != null)
                            lock (restoreDownloadQueue)
                                verboseLogFunc("Restoring " + restoreDownloadQueue.Count + " removed " + (restoreDownloadQueue.Count == 1 ? "file" : "files"));

                        // Remove delete markers
                        s3.RemoveAsync(restoreDownloadQueue).Wait();
                        lock (restoreDownloadQueue)
                            restoreDownloadQueue.Clear();

                        // Trigger download from S3
                        newS3AndMyS3ComparisonNeeded = true;
                    }
                }

                // Restore every earlier file version in MyS3 and place in restore folder
                else
                {
                    foreach (S3ObjectVersion version in completeFileVersionsList)
                    {
                        if (!version.IsDeleteMarker && version.LastModified >= earliestLastModified)
                        {
                            lock (restoreDownloadQueue)
                                if (restoreDownloadQueue.ContainsKey(version.Key))
                                    restoreDownloadQueue[version.Key].Add(version.VersionId);
                                else
                                    restoreDownloadQueue.Add(version.Key, new List<string>() { version.VersionId });
                        }
                    }

                    bool hasRestores = false;
                    lock (restoreDownloadQueue) hasRestores = restoreDownloadQueue.Count > 0;
                    if (hasRestores)
                    {
                        if (verboseLogFunc != null)
                            lock (restoreDownloadQueue)
                                verboseLogFunc("Restoring file versions for " + restoreDownloadQueue.Count + " " + (restoreDownloadQueue.Count == 1 ? "file" : "files"));

                        // Start restore worker
                        StartRestoreWorker();
                    }
                }
            }));
        }

        private void StartRestoreWorker()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                int restoreCounter = 1;
                bool mustRestore = false;
                lock (restoreDownloadQueue) mustRestore = restoreDownloadQueue.Count > 0;
                while (mustRestore && !pauseRestore)
                {
                    // Get paths and versions
                    string s3FilePath = null;
                    string[] versions = null;
                    lock (restoreDownloadQueue)
                    {
                        s3FilePath = restoreDownloadQueue.First().Key;
                        versions = restoreDownloadQueue.First().Value.ToArray();
                    }
                    string originalOfflineFilePathInsideMyS3 = null;
                    string offlineFilePathInsideMyS3 = null;
                    string encryptedFilePathTemp = null;

                    int versionCounter = 0;
                    foreach (string version in versions)
                    {
                        versionCounter++;

                        try
                        {
                            // Get metadata
                            GetObjectMetadataResponse metadataResult = s3.GetMetadata(s3FilePath, version).Result;
                            MetadataCollection metadata = metadataResult.Metadata;

                            // Get paths
                            try
                            {
                                originalOfflineFilePathInsideMyS3 =
                                    Encoding.UTF8.GetString(
                                        AesEncryptionWrapper.DecryptForGCM(
                                            Convert.FromBase64String(metadata["x-amz-meta-encryptedfilepath"]),
                                            EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                                        ));
                            }
                            catch (CryptographicException ex)
                            {
                                Pause(true);
                                wrongEncryptionPassword = true;

                                string problem = "S3 object \"" + s3FilePath.Substring(0, 10) + "****" +
                                    "\" for restore cannot be read because of wrong encryption/decryption password - \"" + ex.Message + "\"";

                                verboseLogFunc(problem);
                            }
                            if (!Tools.RunningOnWindows()) originalOfflineFilePathInsideMyS3 = originalOfflineFilePathInsideMyS3.Replace(@"\", @"/");
                            offlineFilePathInsideMyS3 =
                                MyS3Runner.RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH +
                                Path.GetDirectoryName(originalOfflineFilePathInsideMyS3) + (Tools.RunningOnWindows() ? @"\" : @"/") +
                                Path.GetFileNameWithoutExtension(originalOfflineFilePathInsideMyS3) + "[" + versionCounter + "]" +
                                    Path.GetExtension(originalOfflineFilePathInsideMyS3);
                            string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                            encryptedFilePathTemp = myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH + Path.GetFileName(offlineFilePath) + "." +
                                new Random().Next(0, int.MaxValue) + "." + (DateTime.Now - new DateTime(1970, 1, 1)).Ticks + ".ENCRYPTED";

                            // Skip if restore file exists
                            if (File.Exists(offlineFilePath)) continue;

                            // ---

                            // Set progress info
                            RestoreDownloadPercent = 0;
                            RestoreDownloadSize = long.Parse(metadata["x-amz-meta-decryptedsize"]);
                            restoreSpeedCalc.Start(RestoreDownloadSize);
                            lock (namedRestoreDownloadQueue) namedRestoreDownloadQueue.Add(offlineFilePathInsideMyS3);
                            if (verboseLogFunc != null)
                                lock (restoreDownloadQueue)
                                    verboseLogFunc("S3 object for restoring \"" + originalOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                            "\" [v." + versionCounter + "] starts downloading [" + restoreCounter + "/" + (restoreCounter + restoreDownloadQueue.Count / 2 - 1) +
                                                "][" + Tools.GetByteSizeAsText(metadataResult.Headers.ContentLength) + "]");

                            // Start download
                            CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
                            Task downloadTask = s3.DownloadAsync(encryptedFilePathTemp, s3FilePath, version, originalOfflineFilePathInsideMyS3,
                                cancelTokenSource.Token, OnTransferProgress, TransferType.RESTORE);
                            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
                            {
                                try
                                {
                                    downloadTask.Wait();
                                }
                                catch (Exception)
                                {
                                    if (verboseLogFunc != null)
                                        verboseLogFunc("S3 object download for restoring \"" + originalOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" [v." + versionCounter + "] aborted");
                                }
                            }));

                            // Wait and maybe abort
                            while (!downloadTask.IsCompleted && !downloadTask.IsCanceled)
                                if (pauseRestore) cancelTokenSource.Cancel();
                                else Thread.Sleep(25);

                            // Restore complete
                            if (downloadTask.IsCompletedSuccessfully)
                            {
                                // Set progress info
                                RestoreDownloadSpeed = restoreSpeedCalc.Stop();
                                RestoreDownloadPercent = 100;
                                RestoreDownloaded += new FileInfo(encryptedFilePathTemp).Length;

                                // Decrypt file and resize array
                                byte[] decryptedFileData = AesEncryptionWrapper.DecryptForGCM(
                                    File.ReadAllBytes(encryptedFilePathTemp),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                                );
                                int correctFileDataLength = int.Parse(metadata["x-amz-meta-decryptedsize"]);
                                byte[] fileData = new byte[correctFileDataLength];
                                Array.Copy(decryptedFileData, 0, fileData, 0, fileData.Length);

                                // Create necessary directories
                                string directories = offlineFilePath.Substring(0, offlineFilePath.LastIndexOf(@"\") + 1);
                                if (!Directory.Exists(directories)) Directory.CreateDirectory(directories);

                                // Finish file work
                                File.WriteAllBytes(offlineFilePath, fileData);
                                File.SetLastWriteTime(offlineFilePath, metadataResult.LastModified.ToLocalTime());

                                if (verboseLogFunc != null)
                                    verboseLogFunc("Local file \"" + originalOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" [v." + versionCounter + "] restored");

                                restoreCounter++;

                                // Remove from work queue
                                lock (restoreDownloadQueue)
                                    restoreDownloadQueue.Remove(s3FilePath);
                            }
                        }
                        catch (CryptographicException)
                        {
                            lock (restoreDownloadQueue)
                            {
                                string problem = "S3 restore file \"" + s3FilePath.Substring(0, 10) + "****" + "\" [" +
                                    restoreCounter + "/" + (restoreCounter + restoreDownloadQueue.Count - 1) + "]" + " failed to be decrypted. Wrong encryption/decryption password?";

                                if (verboseLogFunc != null) verboseLogFunc(problem);
                                errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Decryption.log", problem);
                            }
                        }
                        catch (Exception ex) // Happens when trying to download deleted files or it's metadata
                        {
                            lock (restoreDownloadQueue)
                            {
                                string problem = "S3 restore file \"" + s3FilePath.Substring(0, 10) + "****" + "\" [" +
                                restoreCounter + "/" + (restoreCounter + restoreDownloadQueue.Count - 1) + "]" + " not downloaded: " + ex.Message;

                                if (verboseLogFunc != null) verboseLogFunc(problem);
                                errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Restore.log", problem);
                            }
                        }
                        finally
                        {
                            if (File.Exists(encryptedFilePathTemp)) File.Delete(encryptedFilePathTemp);

                            // Remove from queue - only one attempt
                            Thread.Sleep(500); // give client time to show uploaded state
                            lock (namedRestoreDownloadQueue)
                                namedRestoreDownloadQueue.Remove(offlineFilePathInsideMyS3);
                        }
                    }

                    Thread.Sleep(PAUSE_BETWEEN_EACH_S3_OPERATION);
                }

                Thread.Sleep(INACTIVE_PAUSE);
            }));
        }

        // ---

        public  bool IsDisposed { get { return isDisposed; } }
        private bool isDisposed = false;

        public void Dispose()
        {
            Dispose(true);
        }

        protected void Dispose(bool disposing)
        {
            if (isDisposed)
            {
                return;
            }

            if (disposing)
            {
                fileMonitor.Dispose();

                Stop();

                isDisposed = true;
            }
        }
    }
}
