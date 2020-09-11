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
using System.Runtime.Serialization.Formatters.Binary;

using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

using EncryptionAndHashingLibrary;

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

        // ---

        private static readonly string INDEX_FILE_PATH = ".index.bin"; // bucket name must be added at runtime - example: "my-bucket.index.bin"

        private Dictionary<string, string> fileIndex = new Dictionary<string, string>(); // MyS3 file paths ==> S3 object paths

        // ---

        private List<string> changedQueue = new List<string>(); // MyS3 file paths

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
        private List<string> uploadQueue = new List<string>(); // MyS3 file paths
        private Dictionary<string, DateTime> uploadedList = new Dictionary<string, DateTime>(); // MyS3 file paths ==> time uploaded

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
        private Dictionary<string, DateTime> downloadedList = new Dictionary<string, DateTime>(); // S3 object paths ==> time uploaded

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
        private Dictionary<string, DateTime> renamedList = new Dictionary<string, DateTime>(); // MyS3 new file paths ==> time renamed

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

        // ---

        private static readonly int SECONDS_PAUSE_BETWEEN_EACH_S3_AND_MYS3_COMPARISON_WHEN_SHARED_BUCKET = 60; // 1 minute between each comparison check

        private static readonly int INACTIVE_PAUSE = 1000;

        // ---

        public MyS3Runner(string bucket, string region,
            string awsAccessKeyID, string awsSecretAccessKey,
            string myS3Path, // null allowed = use default path
            string encryptionPassword,
            bool sharedBucketWithMoreComparisons,
            Action<string> verboseLogFunc, Action<string, string> errorLogFunc)
        {       // content                         path, content

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
            // Setup root directory
            if (myS3Path == null)
                myS3Path = Environment.ExpandEnvironmentVariables(DEFAULT_RELATIVE_LOCAL_MYS3_DIRECTORY_PATH);
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

            // Clean up old removed files
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                int numberOfFiles = s3.CleanUpRemovedObjects(DateTime.Now.AddYears(-1));
                if (numberOfFiles > 0 && verboseLogFunc != null)
                    verboseLogFunc("Cleaned up some very old removed files");
            }));

            // Start watching files
            fileMonitor = new FileMonitor(myS3Path,
                IGNORED_DIRECTORIES_NAMES, IGNORED_FILE_EXTENSIONS,
                OnChangedFileHandler, OnRenamedFileHandler, OnRemovedFileOrDirectoryHandler);
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                fileMonitor.Start();
            }));
            if (verboseLogFunc != null)
                verboseLogFunc("Started monitoring MyS3 folder for new file changes");

            // Re-establish file index
            if (verboseLogFunc != null)
                verboseLogFunc("Started indexing all accessible files in MyS3");
            LoadFileIndex();

            // Start rest of the workers
            StartLastWriteTimeFixerForChangedFiles();
            StartComparingS3AndMyS3();
            StartDownloadWorker();
            StartUploadWorker();
            StartRenameWorker();
            StartRemoveWorker();
        }

        // ---

        public bool IsIndexingFiles { get { return isIndexingFiles; } }
        private bool isIndexingFiles = true;

        private void SaveFileIndex()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                // Serialize
                byte[] data;
                using (MemoryStream ms = new MemoryStream())
                {
                    BinaryFormatter binFormatter = new BinaryFormatter();
                    lock (fileIndex)
                        binFormatter.Serialize(ms, fileIndex);
                    data = ms.ToArray();
                }

                Tools.WriteSettingsFile(Bucket + INDEX_FILE_PATH, data);
            }));
        }

        private void LoadFileIndex()
        {
            if (Tools.SettingsFileExists(Bucket + INDEX_FILE_PATH))
            {
                isIndexingFiles = true;

                byte[] data = Tools.ReadSettingsFile(Bucket + INDEX_FILE_PATH);

                // Deserialize
                using (MemoryStream ms = new MemoryStream(data))
                {
                    BinaryFormatter binFormatter = new BinaryFormatter();
                    lock (fileIndex)
                        fileIndex = (Dictionary<string, string>)binFormatter.Deserialize(ms);
                }

                // Remove missing files from index
                while (!stop)
                {
                    bool foundMissingFile = false;
                    foreach (KeyValuePair<string, string> kvp in fileIndex)
                    {
                        string offlineFilePath = myS3Path + kvp.Key;
                        if (!File.Exists(offlineFilePath))
                        {
                            fileIndex.Remove(kvp.Key);
                            foundMissingFile = true;
                            break;
                        }
                    }

                    if (!foundMissingFile) break;
                }

                // Find new files and add to index
                foreach (string offlineFilePath in Directory.GetFiles(myS3Path, "*", SearchOption.AllDirectories))
                {
                    // Get paths
                    string offlineFilePathInsideMyS3 = offlineFilePath.Replace(myS3Path, "");
                    string s3FilePath = Convert.ToBase64String(
                        HashWrapper.CreateSHA2Hash(
                            Encoding.UTF8.GetBytes(offlineFilePathInsideMyS3.Replace("/", "")))).Replace(@"\", ""); // mys3 file path ==> hash as s3 key

                    // Skip log and work folders
                    if (offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH) ||
                        offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH) ||
                        offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH)) continue;

                    // Ignore certain file extensions
                    if (IGNORED_FILE_EXTENSIONS.Contains(Path.GetExtension(offlineFilePath))) continue;

                    lock (fileIndex)
                    {
                        // Ignore already indexed files, busy files or files without access
                        if (fileIndex.ContainsKey(offlineFilePathInsideMyS3) ||
                            Tools.IsFileLocked(offlineFilePath))
                            continue;

                        // Add local file name ==> hashed S3 key
                        if (!fileIndex.ContainsKey(offlineFilePathInsideMyS3))
                            fileIndex.Add(offlineFilePathInsideMyS3, s3FilePath);
                    }

                    Thread.Sleep(10);
                    if (stop) break;
                }

                isIndexingFiles = false;
            }
        }

        //

        public int NumberOfFiles { get { return fileIndex.Count; } }

        public long GetTotalFileSize()
        {
            long size = 0;
            lock (fileIndex)
                foreach (KeyValuePair<string, string> kvp in fileIndex)
                    if (File.Exists(myS3Path + kvp.Key))
                        size += (new FileInfo(myS3Path + kvp.Key)).Length;
            return size;
        }

        //

        public void TriggerS3AndMyS3Comparison()
        {
            newS3AndMyS3ComparisonNeeded = true;
        }
        private bool newS3AndMyS3ComparisonNeeded;
        private DateTime timeLastActivity;

        public bool IsComparingFiles { get { return isComparingFiles; } }
        private bool isComparingFiles = false;

        public int ComparisonPercent
        {
            get
            {
                if (totalNumberOfComparisons > 0)
                    return (int)Math.Round((numberOfComparisons / totalNumberOfComparisons) * 100);
                else
                    return 0;
            }
        }
        private double numberOfComparisons;
        private double totalNumberOfComparisons;

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
            // Stop monitoring files
            fileMonitor.Stop();

            // Save file index
            SaveFileIndex();

            // Terminate
            Pause(true);
            stop = true;

            if (verboseLogFunc != null)
                verboseLogFunc("MyS3 terminated, goodbye!");
        }

        // ---

        public void OnChangedFileHandler(string offlineFilePath) // on file creation and change
        {
            // Get paths
            string offlineFilePathInsideMyS3 = offlineFilePath.Replace(myS3Path, "");
            string s3FilePath = Convert.ToBase64String(
                HashWrapper.CreateSHA2Hash(
                    Encoding.UTF8.GetBytes(
                        offlineFilePathInsideMyS3.Replace("/", "")))).Replace(@"\", ""); // mys3 file path ==> hash as s3 key

            // ---

            // Skip if file too new, in use or removed
            lock (uploadQueue) if (uploadQueue.Contains(offlineFilePathInsideMyS3)) return;
            lock (uploadedList) if (uploadedList.ContainsKey(offlineFilePathInsideMyS3) &&
                    uploadedList[offlineFilePathInsideMyS3].AddSeconds(3) > DateTime.Now) return;
            lock (downloadQueue) if (downloadQueue.Contains(s3FilePath)) return;
            lock (downloadedList) if (downloadedList.ContainsKey(s3FilePath) &&
                    downloadedList[s3FilePath].AddSeconds(1) > DateTime.Now) return;
            lock (removeQueue) if (removeQueue.Contains(offlineFilePathInsideMyS3)) return;
            lock (renameQueue) if (renameQueue.ContainsKey(offlineFilePathInsideMyS3)) return;
            lock (renamedList) if (renamedList.ContainsKey(offlineFilePathInsideMyS3) &&
                    renamedList[offlineFilePathInsideMyS3].AddSeconds(3) > DateTime.Now) return;

            // Index file
            lock (fileIndex)
                if (!fileIndex.ContainsKey(offlineFilePathInsideMyS3))
                    fileIndex.Add(offlineFilePathInsideMyS3, s3FilePath);

            // Set new write time to make file "new".
            // This blocks MyS3's (wrongful) removal of files that it thinks should be removed,
            // files that the user copies back into MyS3 despite having removed them in the past.
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
                    Encoding.UTF8.GetBytes(
                        newOfflineFilePathInsideMyS3.Replace("/", "")))).Replace(@"\", ""); // mys3 file path ==> hash as s3 key

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
            lock (fileIndex)
                if (fileIndex.ContainsKey(oldOfflineFilePathInsideMyS3))
                {
                    fileIndex.Remove(oldOfflineFilePathInsideMyS3);
                    if (!fileIndex.ContainsKey(newOfflineFilePathInsideMyS3))
                        fileIndex.Add(newOfflineFilePathInsideMyS3, newS3FilePath);
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

            // Handle directory
            if (directoryInsteadOfFile)
            {
                lock (fileIndex)
                foreach (KeyValuePair<string, string> kvp in fileIndex)
                {
                    string offlinePathInsideMyS3FromList = kvp.Key;
                    if (offlinePathInsideMyS3FromList.StartsWith(offlinePathInsideMyS3))
                        OnRemovedFileOrDirectoryHandler(myS3Path + offlinePathInsideMyS3FromList, false);
                }
                return;
            }

            // ---

            // Cancel rename
            lock (renameQueue)
                if (renameQueue.Values.Contains(offlinePathInsideMyS3))
                    renameQueue.Remove(offlinePathInsideMyS3);

            // Cancel upload
            lock (uploadQueue)
                if (uploadQueue.Contains(offlinePathInsideMyS3))
                    uploadQueue.Remove(offlinePathInsideMyS3);

            // Remove from index
            lock (fileIndex)
                if (fileIndex.ContainsKey(offlinePathInsideMyS3))
                    fileIndex.Remove(offlinePathInsideMyS3);

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

        public void OnTransferProgressHandler(string shownFilePath, long transferredBytes, long totalBytes, TransferType transferType)
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
                    // Get path
                    string offlineFilePathInsideMyS3 = null;
                    lock (changedQueue)
                        if (changedQueue.Count > 0)
                            offlineFilePathInsideMyS3 = changedQueue[0];

                    // Inactivity
                    if (offlineFilePathInsideMyS3 == null)
                    {
                        Thread.Sleep(INACTIVE_PAUSE);
                    }

                    // Set last write time
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

                        // Pause to allow other threads queue access
                        Thread.Sleep(25);
                    }
                }
            }));
        }

        private void StartComparingS3AndMyS3()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) => {

                DateTime timeLastCompare = DateTime.UnixEpoch; // Run comparison when starting

                while (!stop)
                {
                    // Always wait for activity to finish before comparison
                    bool activityDone = false;
                    lock (uploadQueue)
                        lock (downloadQueue)
                            lock (renameQueue)
                                lock (removeQueue)
                                {
                                    if (isIndexingFiles ||
                                        uploadQueue.Count != 0 || downloadQueue.Count != 0 ||
                                        renameQueue.Count != 0 || removeQueue.Count != 0)
                                            timeLastActivity = DateTime.Now;

                                    activityDone = (!isIndexingFiles &&
                                        uploadQueue.Count == 0 && downloadQueue.Count == 0 &&
                                        renameQueue.Count == 0 && removeQueue.Count == 0);
                                }

                    // Start compare work
                    if (activityDone && timeLastActivity.AddSeconds(3) < DateTime.Now) // Give S3 a little time to finish last activity
                    {
                        // S3 and MyS3 comparison runs on schedule if bucket is shared
                        if (sharedBucketWithMoreComparisons &&
                            timeLastCompare.AddSeconds(SECONDS_PAUSE_BETWEEN_EACH_S3_AND_MYS3_COMPARISON_WHEN_SHARED_BUCKET) < DateTime.Now)
                                newS3AndMyS3ComparisonNeeded = true;

                        // ---

                        // New change or comparison requested
                        if (Directory.GetLastWriteTime(myS3Path) > timeLastCompare || newS3AndMyS3ComparisonNeeded)
                        {
                            if (verboseLogFunc != null)
                                verboseLogFunc("Comparing S3 objects and MyS3 folder contents [" + NumberOfFiles + " " + (NumberOfFiles == 1 ? "file" : "files") +
                                    " = " + Tools.GetByteSizeAsText(GetTotalFileSize()) + "]");

                            newS3AndMyS3ComparisonNeeded = false;
                            isComparingFiles = true;

                            // 0. Get lists
                            Dictionary<string, DateTime> removedS3Objects = new Dictionary<string, DateTime>();
                            if (sharedBucketWithMoreComparisons)
                                removedS3Objects = s3.GetCompleteRemovedObjectList();
                            List<S3Object> s3ObjectsInfo = s3.GetCompleteObjectList();

                            numberOfComparisons = 0;
                            totalNumberOfComparisons = fileIndex.Count + fileIndex.Count + s3ObjectsInfo.Count;

                            // 1. Find files locally that should be removed when already removed in S3 from elsewhere (shared bucket only)
                            foreach (KeyValuePair<string, string> kvp in fileIndex)
                            {
                                numberOfComparisons++;

                                // Get paths
                                string offlineFilePathInsideMyS3 = kvp.Key;
                                string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                                string s3FilePath = kvp.Value;

                                // Remove local file if older, and not currently uploading because it's new
                                lock (uploadQueue)
                                {
                                    if (removedS3Objects.ContainsKey(s3FilePath) && // object has already been removed from somewhere else
                                        removedS3Objects[s3FilePath] > File.GetLastWriteTime(offlineFilePath) && // object was newer then local file
                                        !uploadQueue.Contains(offlineFilePathInsideMyS3)) // it's not a new file because it's not uploading
                                    {
                                        // Remove file
                                        File.Delete(offlineFilePath); // triggers file remove handler with necessary actions

                                        // Remove empty directories
                                        string path = Directory.GetParent(myS3Path + offlineFilePathInsideMyS3).FullName;
                                        while ((path + (Tools.RunningOnWindows() ? @"\" : @"/")) != myS3Path) // not root path for deletion
                                        {
                                            // Empty folder
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

                                // Give other threads access to locked queue
                                Thread.Sleep(25);
                            }

                            // ---

                            // inally do the file comparison, S3 objects against MyS3 files and vice versa
                            if (s3ObjectsInfo != null)
                            {
                                // Get searchable S3 object list
                                Dictionary<string, S3Object> s3Files = s3ObjectsInfo.ToDictionary(x => x.Key, x => x);

                                // 1. Compare and find needed uploads
                                foreach (KeyValuePair<string, string> kvp in fileIndex)
                                {
                                    numberOfComparisons++;

                                    // Get paths
                                    string offlineFilePathInsideMyS3 = kvp.Key;
                                    string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                                    string s3FilePath = kvp.Value;

                                    // Ignore busy files or files without access
                                    if (Tools.IsFileLocked(offlineFilePath)) continue;

                                    // ---

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
                                    Thread.Sleep(25);
                                }

                                // 2. Compare and find needed downloads
                                foreach (KeyValuePair<string, S3Object> kvp in s3Files)
                                {
                                    numberOfComparisons++;

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

                                        // Offline file exists
                                        if (fileIndex.Values.Contains(s3FilePath))
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
                                                                    // Skip if in use
                                                                    if (Tools.IsFileLocked(offlineFilePath)) continue;

                                                                    downloadQueue.Add(s3FilePath);

                                                                    if (verboseLogFunc != null)
                                                                        verboseLogFunc("Found updated S3 object so added it to download queue [" + downloadQueue.Count + "]");
                                                                }
                                        }

                                        // Offline file doesn't exist
                                        else if (!fileIndex.Values.Contains(s3FilePath))
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
                                    Thread.Sleep(25);
                                }
                            }
                            else
                            {
                                if (verboseLogFunc != null)
                                    verboseLogFunc("Unable to get list of S3 objects. Perhaps missing permissions in IAM custom policy?");
                            }

                            // ---

                            isComparingFiles = false;

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

                    bool haveUploads = false;
                    lock (uploadQueue) haveUploads = uploadQueue.Count > 0;
                    while (haveUploads && !pauseUploads)
                    {
                        // Remove old uploads from "log"
                        while (true)
                        {
                            bool foundOldUpload = false;
                            foreach (KeyValuePair<string, DateTime> kvp in uploadedList)
                                if (kvp.Value.AddSeconds(5) < DateTime.Now)
                                {
                                    uploadedList.Remove(kvp.Key);
                                    foundOldUpload = true;
                                    break;
                                }
                            if (!foundOldUpload) break;
                        }

                        // ---

                        // Get paths
                        string offlineFilePathInsideMyS3;
                        lock (uploadQueue)
                            offlineFilePathInsideMyS3 = uploadQueue[0];
                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                        string s3FilePath = Convert.ToBase64String(
                            HashWrapper.CreateSHA2Hash(
                                Encoding.UTF8.GetBytes(
                                    offlineFilePathInsideMyS3.Replace(@"/", "")))).Replace(@"\", ""); // mys3 file path ==> hash as s3 key
                        string encryptedUploadFilePath = null;

                        // Check existence and access
                        lock (changedQueue)
                            if (changedQueue.Contains(offlineFilePath))
                            {
                                Thread.Sleep(25);

                                continue;
                            }
                        if (!File.Exists(offlineFilePath) || Tools.IsFileLocked(offlineFilePath))
                        {
                            // Remove from queue
                            lock (uploadQueue)
                            {
                                if (uploadQueue.Contains(offlineFilePathInsideMyS3))
                                    uploadQueue.Remove(offlineFilePathInsideMyS3);

                                if (verboseLogFunc != null)
                                    verboseLogFunc("Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" removed from upload queue [" + uploadQueue.Count + "]");

                                haveUploads = uploadQueue.Count > 0;
                            }

                            continue;
                        }

                        try
                        {
                            // Get paths
                            encryptedUploadFilePath = myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH + 
                                Path.GetFileName(offlineFilePath) + "." +
                                    new Random().Next(0, int.MaxValue) + "." +
                                        (DateTime.Now - new DateTime(1970, 1, 1)).Ticks + ".ENCRYPTED";

                            // Encrypt file data
                            byte[] fileData = File.ReadAllBytes(offlineFilePath);
                            byte[] encryptedData = AesEncryptionWrapper.EncryptWithGCM(
                                fileData,
                                EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                            );
                            File.WriteAllBytes(encryptedUploadFilePath, encryptedData);

                            // Decryption test
                            byte[] decryptedUploadFileData = AesEncryptionWrapper.DecryptForGCM(
                                File.ReadAllBytes(encryptedUploadFilePath),
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
                            UploadSize = new FileInfo(encryptedUploadFilePath).Length;
                            uploadSpeedCalc.Start(fileData.Length);

                            if (verboseLogFunc != null)
                                lock(uploadQueue)
                                    verboseLogFunc("Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" starts uploading [" +
                                        uploadCounter + "/" + (uploadCounter + uploadQueue.Count - 1) + "][" + Tools.GetFileSizeAsText(encryptedUploadFilePath) + "]");

                            // Start upload
                            CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
                            Task uploadTask = s3.UploadAsync(encryptedUploadFilePath, s3FilePath, offlineFilePathInsideMyS3,
                                metadata, cancelTokenSource.Token, OnTransferProgressHandler);
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
                                    if (fileData.Length < S3Wrapper.MIN_MULTIPART_SIZE) // <5 MB upload = no S3Wrapper progress report
                                    {
                                        // Upload percent estimate
                                        double newUploadPercent = UploadPercent +
                                            100 * ((UploadSpeed * 25 / 1000) / fileData.Length); // will be zero first upload
                                        if (newUploadPercent > 100) newUploadPercent = 100;
                                        if (!Double.IsNaN(newUploadPercent))
                                            UploadPercent = newUploadPercent;

                                        // Transfer progress report
                                        if (UploadSpeed != 0 &&
                                            (DateTime.Now - timeTransferProgressShown).TotalMilliseconds >= S3Wrapper.TRANSFER_EVENT_PAUSE_MILLISECONDS)
                                        {
                                            timeTransferProgressShown = DateTime.Now;

                                            OnTransferProgressHandler(offlineFilePathInsideMyS3,
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
                                double newUploadSpeed = uploadSpeedCalc.Stop();
                                if (newUploadSpeed != -1) UploadSpeed = newUploadSpeed;
                                UploadPercent = 100;
                                Uploaded += new FileInfo(encryptedUploadFilePath).Length;
                                if (verboseLogFunc != null)
                                    verboseLogFunc("Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" uploaded");

                                // Set last change locally
                                File.SetLastWriteTime(
                                    offlineFilePath,
                                    s3.GetMetadata(s3FilePath, null).Result.LastModified.ToLocalTime()
                                ); // to stop re-download

                                uploadCounter++;

                                // Add to special blocked queue - in case of slow file activity handling
                                lock (uploadedList)
                                    uploadedList.Add(offlineFilePathInsideMyS3, DateTime.Now);
                            }
                        }
                        catch (Exception ex)
                        {
                            string problem = "Problem uploading local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" - \"" + ex.Message + "\"";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            else errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Upload.log", problem);
                        }

                        if (File.Exists(encryptedUploadFilePath)) File.Delete(encryptedUploadFilePath);

                        // Remove from work queue = only one try
                        lock (uploadQueue)
                            if (uploadQueue.Contains(offlineFilePathInsideMyS3))
                                uploadQueue.Remove(offlineFilePathInsideMyS3);

                        // Give other threads access to queues
                        Thread.Sleep(25);

                        lock (uploadQueue) haveUploads = uploadQueue.Count > 0;
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

                    bool haveDownloads = false;
                    lock (downloadQueue) haveDownloads = downloadQueue.Count > 0;
                    while (haveDownloads && !pauseDownloads)
                    {
                        // Remove old downloads from "log"
                        while (true)
                        {
                            bool foundOldDownload = false;
                            foreach (KeyValuePair<string, DateTime> kvp in downloadedList)
                                if (kvp.Value.AddSeconds(5) < DateTime.Now)
                                {
                                    downloadedList.Remove(kvp.Key);
                                    foundOldDownload = true;
                                    break;
                                }
                            if (!foundOldDownload) break;
                        }

                        // ---

                        // Get paths
                        string s3FilePath;
                        lock (downloadQueue)
                            s3FilePath = downloadQueue[0];
                        string offlineFilePathInsideMyS3 = null;
                        string encryptedDownloadFilePath = null;

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
                            encryptedDownloadFilePath = myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH +
                                Path.GetFileName(offlineFilePath) + "." +
                                new Random().Next(0, int.MaxValue) + "." + (DateTime.Now - new DateTime(1970, 1, 1)).Ticks + ".ENCRYPTED";

                            // Abort if changed or busy
                            if (File.Exists(offlineFilePath) &&
                               (Tools.IsFileLocked(offlineFilePath) || File.GetLastWriteTime(offlineFilePath) > metadataResult.LastModified.ToLocalTime()))
                            {
                                // Remove from queue
                                lock (downloadQueue)
                                {
                                    downloadQueue.Remove(s3FilePath);

                                    if (verboseLogFunc != null)
                                        verboseLogFunc("S3 object for \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" removed from download queue");

                                    haveDownloads = downloadQueue.Count > 0;
                                }

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
                            Task downloadTask = s3.DownloadAsync(encryptedDownloadFilePath, s3FilePath, null, offlineFilePathInsideMyS3,
                                cancelTokenSource.Token, OnTransferProgressHandler, TransferType.DOWNLOAD);
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
                                if (pauseDownloads)
                                    cancelTokenSource.Cancel();
                                else
                                    Thread.Sleep(25);

                            // Download complete
                            if (downloadTask.IsCompletedSuccessfully)
                            {
                                // Set progress info
                                double newDownloadSpeed = downloadSpeedCalc.Stop();
                                if (newDownloadSpeed != -1) DownloadSpeed = newDownloadSpeed;
                                DownloadPercent = 100;
                                Downloaded += new FileInfo(encryptedDownloadFilePath).Length;

                                // Decrypt file and resize it's array
                                byte[] decryptedFileData = AesEncryptionWrapper.DecryptForGCM(
                                    File.ReadAllBytes(encryptedDownloadFilePath),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                                );
                                long correctFileDataLength = long.Parse(metadata["x-amz-meta-decryptedsize"]);
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
                                lock (fileIndex)
                                    if (!fileIndex.ContainsKey(offlineFilePathInsideMyS3))
                                        fileIndex.Add(offlineFilePathInsideMyS3, s3FilePath);

                                if (verboseLogFunc != null)
                                    verboseLogFunc("Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" reconstructed");

                                downloadCounter++;

                                // Add to block queue - in case of slow file activity handling
                                lock (downloadedList)
                                    downloadedList.Add(s3FilePath, DateTime.Now);
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

                        if (File.Exists(encryptedDownloadFilePath)) File.Delete(encryptedDownloadFilePath);

                        // Remove from work queue = only one try
                        lock (downloadQueue)
                            lock (namedDownloadQueue)
                            {
                                downloadQueue.Remove(s3FilePath);
                                namedDownloadQueue.Remove(offlineFilePathInsideMyS3);
                            }

                        // Give other threads access to queues
                        Thread.Sleep(25);

                        lock (downloadQueue) haveDownloads = downloadQueue.Count > 0;
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

                    bool haveRenameWork = false;
                    lock (renameQueue) haveRenameWork = renameQueue.Count > 0;
                    while (haveRenameWork)
                    {
                        // Remove old renames from "log"
                        while (true)
                        {
                            bool foundOldRename = false;
                            foreach (KeyValuePair<string, DateTime> renamedKVP in renamedList)
                                if (renamedKVP.Value.AddSeconds(5) < DateTime.Now)
                                {
                                    renamedList.Remove(renamedKVP.Key);
                                    foundOldRename = true;
                                    break;
                                }
                            if (!foundOldRename) break;
                        }

                        // ---

                        // Get paths
                        KeyValuePair<string, string> renameKVP;
                        lock (renameQueue)
                            renameKVP = renameQueue.First();
                        string newOfflineFilePathInsideMyS3 = renameKVP.Key;
                        string newOfflineFilePath = myS3Path + newOfflineFilePathInsideMyS3;
                        string newS3FilePath = Convert.ToBase64String(
                            HashWrapper.CreateSHA2Hash(
                                Encoding.UTF8.GetBytes(
                                    newOfflineFilePathInsideMyS3.Replace("/", "")))).Replace(@"\", ""); // mys3 file path ==> hash as s3 key
                        string oldOfflineFilePathInsideMyS3 = renameKVP.Value;
                        string oldOfflineFilePath = myS3Path + oldOfflineFilePathInsideMyS3;
                        string oldS3FilePath = Convert.ToBase64String(
                            HashWrapper.CreateSHA2Hash(
                                Encoding.UTF8.GetBytes(
                                    oldOfflineFilePathInsideMyS3.Replace("/", "")))).Replace(@"\", ""); // mys3 file path ==> hash as s3 key

                        // Start process
                        bool copySuccess = false; // First problem: S3 object to be copied may not exist yet, because upload did not finish before renaming
                        try
                        {
                            // Get metadata
                            MetadataCollection metadata = s3.GetMetadata(oldS3FilePath, null).Result.Metadata;
                            string newEncryptedFilePath = Convert.ToBase64String(
                                AesEncryptionWrapper.EncryptWithGCM(
                                    Encoding.UTF8.GetBytes(newOfflineFilePathInsideMyS3.Replace("/", @"\")),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                            ));
                            metadata.Add("x-amz-meta-encryptedfilepath", newEncryptedFilePath); // overwrites

                            // Copy and remove
                            s3.CopyAsync(oldS3FilePath, newS3FilePath, metadata).Wait();
                            copySuccess = true;
                        }
                        catch (Exception ex)
                        {
                            string problem = "Problem renaming S3 object \"" + oldS3FilePath.Substring(0, 10) + "****" + "\"" + " for local file \"" +
                                oldOfflineFilePathInsideMyS3 + "\" to " + "\"" + newS3FilePath.Substring(0, 10) + "****" + "\" for \"" + newOfflineFilePathInsideMyS3 + "\" - \"" + ex.Message + "\"";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            else errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Rename.log", problem);

                            OnChangedFileHandler(newOfflineFilePath);
                        }

                        // Give S3 more time to finish work (requesting metadata immediately sometimes gives NotFound error)
                        Thread.Sleep(25);

                        // Copy complete
                        if (copySuccess)
                        {
                            if (File.Exists(newOfflineFilePath)) // Second problem: Local file may not exist any longer
                            {
                                // Finish by setting last changed time
                                File.SetLastWriteTime(
                                    newOfflineFilePath,
                                    s3.GetMetadata(newS3FilePath, null).Result.LastModified.ToLocalTime()
                                );

                                if (verboseLogFunc != null)
                                    lock (renameQueue)
                                        verboseLogFunc("S3 object for local file \"" + newOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" renamed [" +
                                                renameCounter + "/" + (renameCounter + renameQueue.Count - 1) + "]");

                                renameCounter++;

                                // Add to block queue - in case of slow file activity handling
                                lock (renamedList)
                                    renamedList.Add(newS3FilePath, DateTime.Now);

                                // Remove old object
                                try { s3.RemoveAsync(oldS3FilePath, null).Wait(); }
                                catch (Exception) { }
                            }

                            // Clean up because local file removed
                            else
                            {
                                try { s3.RemoveAsync(newS3FilePath, null).Wait(); }
                                catch (Exception) { }
                            }

                            // Remove from work queue = only one try
                            lock (renameQueue)
                                if (renameQueue.ContainsKey(newOfflineFilePathInsideMyS3))
                                    renameQueue.Remove(newOfflineFilePathInsideMyS3);
                        }

                        // Copy failed so remove and upload instead
                        else
                        {
                            // Remove from work queue first
                            lock (renameQueue)
                                if (renameQueue.ContainsKey(newOfflineFilePathInsideMyS3))
                                    renameQueue.Remove(newOfflineFilePathInsideMyS3);

                            // Remove old object
                            try { s3.RemoveAsync(oldS3FilePath, null).Wait(); }
                            catch (Exception) { }

                            OnChangedFileHandler(newOfflineFilePath);
                        }

                        // Give other threads access to queues
                        Thread.Sleep(25);

                        lock (renameQueue) haveRenameWork = renameQueue.Count > 0;
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

                    bool haveRemoveWork = false;
                    lock (removeQueue) haveRemoveWork = removeQueue.Count > 0;
                    while (haveRemoveWork)
                    {
                        // Get paths
                        string offlineFilePathInsideMyS3 = null;
                        lock(removeQueue)
                            offlineFilePathInsideMyS3 = removeQueue[0];
                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                        string s3FilePath = Convert.ToBase64String(
                            HashWrapper.CreateSHA2Hash(
                                Encoding.UTF8.GetBytes(
                                    offlineFilePathInsideMyS3.Replace("/", "")))).Replace(@"\", ""); // mys3 file path ==> hash as s3 key

                        // Remove file from S3
                        try
                        {
                            s3.RemoveAsync(s3FilePath, null).Wait();

                            if (verboseLogFunc != null)
                                lock (removeQueue)
                                verboseLogFunc("S3 object for local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" removed [" +
                                        removeCounter + "/" + (removeCounter + removeQueue.Count - 1) + "]");

                            removeCounter++;
                        }
                        catch (Exception ex)
                        {
                            string problem = "Problem removing S3 object \"" + s3FilePath.Substring(0, 10) + "****" + "\" for local file \"" +
                                offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" - \"" + ex.Message + "\"";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            else errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Remove.log", problem);
                        }

                        // Remove from work queue
                        lock (removeQueue)
                            if (removeQueue.Contains(offlineFilePathInsideMyS3))
                                removeQueue.Remove(offlineFilePathInsideMyS3);

                        // Give other threads access to queues
                        Thread.Sleep(25);

                        lock (removeQueue) haveRemoveWork = removeQueue.Count > 0;
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

                    // Do the restoring of removed S3 objects
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
                    // Find all file versions that fits the parameters
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

                    // Do the restoring of discovered earlier file versions
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
                                cancelTokenSource.Token, OnTransferProgressHandler, TransferType.RESTORE);
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
                                double newRestoreDownloadSpeed = restoreSpeedCalc.Stop();
                                if (newRestoreDownloadSpeed != -1) RestoreDownloadSpeed = newRestoreDownloadSpeed;
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

                                // Remove from queue
                                Thread.Sleep(500); // give client time to show uploaded state

                                // Remove from work queue
                                lock (restoreDownloadQueue)
                                    restoreDownloadQueue.Remove(s3FilePath);

                                lock (namedRestoreDownloadQueue)
                                    namedRestoreDownloadQueue.Remove(offlineFilePathInsideMyS3);
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
                        }
                    }

                    // Give other threads access to queues
                    Thread.Sleep(25);
                }
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
