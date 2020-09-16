using System;
using System.IO;
using System.Net;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
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

        private static readonly string INDEX_FILE_PATH = ".index.bin";
        // bucket name must be added at runtime - example: "my-bucket.index.bin"

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
            ".________________________", // use this if creating test files
            ".db",
            ".ini"
        };

        private static readonly int SECONDS_PAUSE_BETWEEN_EACH_S3_AND_MYS3_COMPARISON_WHEN_SHARED_BUCKET = 60; // 1 minute between each comparison check
        private static readonly int INACTIVE_PAUSE = 1000;

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

            // Setup necessary work directories
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
            // Setup S3 interface
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

            // Establish file index
            if (verboseLogFunc != null)
                verboseLogFunc("Started indexing all accessible files in MyS3");
            LoadFileIndex();

            // Start workers
            StartLastWriteTimeFixerForChangedFiles();
            StartDownloadWorker();
            StartUploadWorker();
            StartRenameWorker();
            StartRemoveWorker();
        }

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
            PauseRestores(pause);
        }

        public bool restorePaused { get { return pauseRestore; } }
        private bool pauseRestore = false;

        public void PauseRestores(bool pause)
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

        private Dictionary<string, string> myS3FileIndexDict = new Dictionary<string, string>(); // local file paths ==> S3 object paths
        private Dictionary<string, Tuple<S3Object, MetadataCollection>> s3ObjectIndexDict
            = new Dictionary<string, Tuple<S3Object, MetadataCollection>>(); // S3 object key ==> S3Object info, metadata
                                                                             // S3Object is misleading, it has very little S3 object information

        private List<string> changedList = new List<string>(); // temp list of local file paths

        public ImmutableList<string> UploadList
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(uploadList, 10, ref acquiredLock);
                    if (acquiredLock)
                        return uploadList.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(uploadList);
                }
            }
        }
        private List<string> uploadList = new List<string>(); // local file paths
        private Dictionary<string, DateTime> uploadedList = new Dictionary<string, DateTime>(); // local file paths ==> time uploaded

        public ImmutableList<string> DownloadList
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(downloadList, 10, ref acquiredLock);
                    if (acquiredLock)
                        return downloadList.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(downloadList);
                }
            }
        }
        private List<string> downloadList = new List<string>(); // S3 object paths
        private Dictionary<string, DateTime> downloadedDict = new Dictionary<string, DateTime>(); // S3 object paths ==> time uploaded

        public ImmutableList<string> NamedDownloadList
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(namedDownloadList, 10, ref acquiredLock);
                    if (acquiredLock)
                        return namedDownloadList.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(namedDownloadList);
                }
            }
        }
        private List<string> namedDownloadList = new List<string>(); // local file paths

        private Dictionary<string, string> renameDict = new Dictionary<string, string>(); // MyS3 new file paths ==> old file paths
        private Dictionary<string, DateTime> renamedDict = new Dictionary<string, DateTime>(); // MyS3 new file paths ==> time renamed

        private List<string> removeList = new List<string>(); // local file paths

        public ImmutableList<string> RestoreDownloadList
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(restoreDownloadDict, 10, ref acquiredLock);
                    if (acquiredLock)
                        return restoreDownloadDict.Keys.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(restoreDownloadDict);
                }
            }
        }
        private Dictionary<string, List<string>> restoreDownloadDict = new Dictionary<string, List<string>>(); // S3 object paths ==> versionIds

        public ImmutableList<string> NamedRestoreDownloadList
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(namedRestoreDownloadList, 10, ref acquiredLock);
                    if (acquiredLock)
                        return namedRestoreDownloadList.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(namedRestoreDownloadList);
                }
            }
        }
        private List<string> namedRestoreDownloadList = new List<string>(); // local file paths

        // ---

        private static string MyS3FilePathToS3ObjectKey(string filePath)
        {
            return Convert.ToBase64String(
                HashWrapper.CreateSHA2Hash(
                    Encoding.UTF8.GetBytes(
                        filePath.Replace(@"/", "").Replace(@"\", "")
                    )
                )
            ).Replace(@"\", "").Replace(@"/", "");
        }

        //

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
                    lock (myS3FileIndexDict)
                        binFormatter.Serialize(ms, myS3FileIndexDict);
                    data = ms.ToArray();
                }

                Tools.WriteSettingsFile(Bucket + INDEX_FILE_PATH, data);
            }));
        }

        private void LoadFileIndex()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                isIndexingFiles = true;

                if (Tools.SettingsFileExists(Bucket + INDEX_FILE_PATH))
                {
                    byte[] data = Tools.ReadSettingsFile(Bucket + INDEX_FILE_PATH);

                    // Deserialize
                    using (MemoryStream ms = new MemoryStream(data))
                    {
                        BinaryFormatter binFormatter = new BinaryFormatter();
                        lock (myS3FileIndexDict)
                            myS3FileIndexDict = (Dictionary<string, string>)binFormatter.Deserialize(ms);
                    }

                    // Remove missing files from index
                    while (!stop)
                    {
                        bool foundMissingFile = false;
                        foreach (KeyValuePair<string, string> kvp in myS3FileIndexDict)
                        {
                            string offlineFilePath = myS3Path + kvp.Key;
                            if (!File.Exists(offlineFilePath))
                            {
                                myS3FileIndexDict.Remove(kvp.Key);
                                foundMissingFile = true;
                                break;
                            }
                        }

                        if (!foundMissingFile) break;
                    }
                }

                // Find new files and add to index
                foreach (string offlineFilePath in Directory.GetFiles(myS3Path, "*", SearchOption.AllDirectories))
                {
                    // Get paths
                    string offlineFilePathInsideMyS3 = offlineFilePath.Replace(myS3Path, "");
                    string s3ObjectKey = MyS3FilePathToS3ObjectKey(offlineFilePathInsideMyS3);

                    // Skip log and work folders
                    if (offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH) ||
                        offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH) ||
                        offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH)) continue;

                    // Ignore certain file extensions
                    if (IGNORED_FILE_EXTENSIONS.Contains(Path.GetExtension(offlineFilePath))) continue;

                    lock (myS3FileIndexDict)
                    {
                        // Ignore already indexed files, busy files or files without access
                        if (myS3FileIndexDict.ContainsKey(offlineFilePathInsideMyS3) ||
                            Tools.IsFileLocked(offlineFilePath))
                            continue;

                        // Add local file name ==> hashed S3 key
                        if (!myS3FileIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                            myS3FileIndexDict.Add(offlineFilePathInsideMyS3, s3ObjectKey);
                    }

                    Thread.Sleep(10);
                    if (stop) break;
                }

                isIndexingFiles = false;

                // ---

                // Start comparison worker for S3 objects and MyS3 files
                StartComparingS3AndMyS3();
            }));
        }

        //

        public int NumberOfFiles { get { return myS3FileIndexDict.Count; } }

        public long GetTotalFileSize()
        {
            long size = 0;
            lock (myS3FileIndexDict)
                foreach (KeyValuePair<string, string> kvp in myS3FileIndexDict)
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

        public void OnChangedFileHandler(string offlineFilePath) // on file creation and change
        {
            // Get paths
            string offlineFilePathInsideMyS3 = offlineFilePath.Replace(myS3Path, "");
            string s3ObjectKey = MyS3FilePathToS3ObjectKey(offlineFilePathInsideMyS3);

            // ---

            // Skip if file too new, in use or removed
            lock (uploadList) if (uploadList.Contains(offlineFilePathInsideMyS3)) return;
            lock (uploadedList) if (uploadedList.ContainsKey(offlineFilePathInsideMyS3) &&
                uploadedList[offlineFilePathInsideMyS3].AddSeconds(3) > DateTime.Now) return;
            lock (downloadList) if (downloadList.Contains(s3ObjectKey)) return;
            lock (downloadedDict) if (downloadedDict.ContainsKey(s3ObjectKey) &&
                downloadedDict[s3ObjectKey].AddSeconds(1) > DateTime.Now) return;
            lock (removeList) if (removeList.Contains(offlineFilePathInsideMyS3)) return;
            lock (renameDict) if (renameDict.ContainsKey(offlineFilePathInsideMyS3)) return;
            lock (renamedDict) if (renamedDict.ContainsKey(offlineFilePathInsideMyS3) &&
                renamedDict[offlineFilePathInsideMyS3].AddSeconds(3) > DateTime.Now) return;

            // Index file
            lock (myS3FileIndexDict)
                if (!myS3FileIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                    myS3FileIndexDict.Add(offlineFilePathInsideMyS3, s3ObjectKey);

            // Set new write time to make file "new".
            // This blocks MyS3's (wrongful) removal of files that it thinks should be removed,
            // files that the user copies back into MyS3 despite having removed them in the past.
            lock (changedList)
                if (!changedList.Contains(offlineFilePathInsideMyS3))
                    changedList.Add(offlineFilePathInsideMyS3);

            // Add upload
            lock (uploadList)
                if (!uploadList.Contains(offlineFilePathInsideMyS3))
                {
                    uploadList.Add(offlineFilePathInsideMyS3);

                    if (verboseLogFunc != null)
                        verboseLogFunc("Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                            "\" added to upload queue [" + uploadList.Count + "]");
                }
        }

        public void OnRenamedFileHandler(string oldOfflineFilePath, string newOfflineFilePath)
        {
            // Get paths
            string newOfflineFilePathInsideMyS3 = newOfflineFilePath.Replace(myS3Path, "");
            string newS3ObjectKey = MyS3FilePathToS3ObjectKey(newOfflineFilePathInsideMyS3);
            string oldOfflineFilePathInsideMyS3 = oldOfflineFilePath.Replace(myS3Path, "");
            string oldS3ObjectKey = MyS3FilePathToS3ObjectKey(oldOfflineFilePathInsideMyS3);

            // ---

            // Cancel old rename
            lock (renameDict)
                if (renameDict.ContainsKey(oldOfflineFilePathInsideMyS3))
                    renameDict.Remove(oldOfflineFilePathInsideMyS3);

            // Cancel upload
            lock (uploadList)
                if (uploadList.Contains(oldOfflineFilePathInsideMyS3))
                    uploadList.Remove(oldOfflineFilePathInsideMyS3);

            // Index file
            lock (myS3FileIndexDict)
                if (myS3FileIndexDict.ContainsKey(oldOfflineFilePathInsideMyS3))
                {
                    myS3FileIndexDict.Remove(oldOfflineFilePathInsideMyS3);
                    if (!myS3FileIndexDict.ContainsKey(newOfflineFilePathInsideMyS3))
                        myS3FileIndexDict.Add(newOfflineFilePathInsideMyS3, newS3ObjectKey);
                }

            // Do rename
            lock (s3ObjectIndexDict)
                lock (renameDict)
                {
                    if (s3ObjectIndexDict.ContainsKey(oldS3ObjectKey) && !renameDict.ContainsKey(newOfflineFilePathInsideMyS3))
                    {
                        renameDict.Add(newOfflineFilePathInsideMyS3, oldOfflineFilePathInsideMyS3);

                        if (verboseLogFunc != null)
                            verboseLogFunc("S3 object for local file \"" + oldOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                "\" ==> \"" + newOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                    "\" added to rename queue [" + renameDict.Count + "]");
                    }
                }
        }

        public void OnRemovedFileOrDirectoryHandler(string offlinePath, bool directoryInsteadOfFile)
        {
            // Get paths
            string offlinePathInsideMyS3 = offlinePath.Replace(myS3Path, "");
            string s3ObjectKey = MyS3FilePathToS3ObjectKey(offlinePathInsideMyS3);

            // Handle directory
            if (directoryInsteadOfFile)
            {
                lock (myS3FileIndexDict)
                foreach (KeyValuePair<string, string> kvp in myS3FileIndexDict)
                {
                    string offlinePathInsideMyS3FromList = kvp.Key;
                    if (offlinePathInsideMyS3FromList.StartsWith(offlinePathInsideMyS3))
                        OnRemovedFileOrDirectoryHandler(myS3Path + offlinePathInsideMyS3FromList, false);
                }
                return;
            }

            // ---

            // Cancel rename
            lock (renameDict)
                if (renameDict.Values.Contains(offlinePathInsideMyS3))
                    renameDict.Remove(offlinePathInsideMyS3);

            // Cancel upload
            lock (uploadList)
                if (uploadList.Contains(offlinePathInsideMyS3))
                    uploadList.Remove(offlinePathInsideMyS3);

            // Remove from index
            lock (myS3FileIndexDict)
                if (myS3FileIndexDict.ContainsKey(offlinePathInsideMyS3))
                    myS3FileIndexDict.Remove(offlinePathInsideMyS3);

            // Do removal
            lock (s3ObjectIndexDict)
                lock (removeList)
                    if (s3ObjectIndexDict.ContainsKey(s3ObjectKey) && !removeList.Contains(offlinePathInsideMyS3))
                    {
                        removeList.Add(offlinePathInsideMyS3);

                        if (verboseLogFunc != null)
                            verboseLogFunc("S3 object for local file \"" + offlinePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                "\" added to removal queue [" + removeList.Count + "]");
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
                    lock (changedList)
                        if (changedList.Count > 0)
                            offlineFilePathInsideMyS3 = changedList[0];

                    // Wait for new changed file = inactivity
                    if (offlineFilePathInsideMyS3 == null)
                    {
                        Thread.Sleep(INACTIVE_PAUSE);
                    }

                    // Set last write time to now
                    else
                    {
                        // Get path
                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;

                        // Has access so set new time
                        if (File.Exists(offlineFilePath))
                        {
                            if (!Tools.IsFileLocked(offlineFilePath))
                            {
                                File.SetLastWriteTime(offlineFilePath, DateTime.Now);

                                lock (changedList)
                                    changedList.Remove(offlineFilePathInsideMyS3);
                            }
                        }

                        // Forget and move on
                        else
                        {
                            lock (changedList)
                                changedList.Remove(offlineFilePathInsideMyS3);
                        }

                        // Pause to allow other threads queue access and or try again soon if file busy
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
                    // Wait for the right moment to run comparisons
                    bool activityDone = false;
                    lock (uploadList)
                        lock (downloadList)
                            lock (renameDict)
                                lock (removeList)
                                {
                                    if (isIndexingFiles ||
                                        uploadList.Count != 0 || downloadList.Count != 0 ||
                                        renameDict.Count != 0 || removeList.Count != 0)
                                            timeLastActivity = DateTime.Now;

                                    activityDone = (!isIndexingFiles &&
                                        uploadList.Count == 0 && downloadList.Count == 0 &&
                                        renameDict.Count == 0 && removeList.Count == 0);
                                }

                    // ---

                    // Start compare work
                    if (activityDone && timeLastActivity.AddSeconds(3) < DateTime.Now) // Give S3 a little time to finish last activity
                    {
                        // S3 and MyS3 comparison also runs on a schedule if bucket is shared
                        if (sharedBucketWithMoreComparisons &&
                            timeLastCompare.AddSeconds(SECONDS_PAUSE_BETWEEN_EACH_S3_AND_MYS3_COMPARISON_WHEN_SHARED_BUCKET) < DateTime.Now)
                                newS3AndMyS3ComparisonNeeded = true;

                        // ---

                        // New change or comparison requested
                        if (Directory.GetLastWriteTime(myS3Path) > timeLastCompare || newS3AndMyS3ComparisonNeeded)
                        {
                            newS3AndMyS3ComparisonNeeded = false;

                            // ---

                            if (verboseLogFunc != null)
                                verboseLogFunc("Retrieving S3 object lists for S3 and MyS3 comparisons");

                            isComparingFiles = true;
                            numberOfComparisons = 0;

                            try
                            {
                                // Get list of removed S3 objects
                                Dictionary<string, DateTime> removedS3Objects =
                                    sharedBucketWithMoreComparisons ?
                                        s3.GetCompleteRemovedObjectList() :
                                        new Dictionary<string, DateTime>();

                                // Get list of S3 objects with metadata
                                lock (s3ObjectIndexDict)
                                {
                                    // Get all S3 object info
                                    s3ObjectIndexDict = s3.GetCompleteObjectList().ToDictionary(
                                        x => x.Key,
                                        x => Tuple.Create<S3Object, MetadataCollection>(x, null)
                                    );

                                    // Get all S3 object custom metadata
                                    for (int s = 0; s < s3ObjectIndexDict.Count; s++)
                                    {
                                        string s3ObjectKey = s3ObjectIndexDict.Keys.ElementAt(s);

                                        // Get metadata
                                        GetObjectMetadataResponse s3ObjectMetadataResult = s3.GetMetadata(s3ObjectKey, null).Result;
                                        MetadataCollection s3ObjectMetadata = s3ObjectMetadataResult.Metadata;

                                        // Update S3 object index
                                        S3Object s3ObjectInfo = s3ObjectIndexDict[s3ObjectKey].Item1;
                                        s3ObjectIndexDict[s3ObjectKey] = Tuple.Create(s3ObjectInfo, s3ObjectMetadata);
                                    }
                                }

                                totalNumberOfComparisons =
                                    ((removedS3Objects.Count > 0) ? myS3FileIndexDict.Count : 0) + // looping through local files
                                    s3ObjectIndexDict.Count + // looping through S3 objects
                                    myS3FileIndexDict.Count; // looping through local files, again

                                // ---

                                if (verboseLogFunc != null)
                                    verboseLogFunc("Comparing S3 and MyS3 [" + NumberOfFiles + " " + (NumberOfFiles == 1 ? "file" : "files") +
                                        " = " + Tools.GetByteSizeAsText(GetTotalFileSize()) + "]");

                                // 1. Find files locally that should be removed when already removed in S3 from elsewhere (shared bucket only)
                                if (removedS3Objects.Count > 0)
                                {
                                    foreach (KeyValuePair<string, string> kvp in myS3FileIndexDict)
                                    {
                                        numberOfComparisons++;

                                        // Get paths
                                        string offlineFilePathInsideMyS3 = kvp.Key;
                                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                                        string s3ObjectKey = kvp.Value;

                                        // Remove local file if older, and not currently uploading because it's new
                                        lock (uploadList)
                                        {
                                            if (removedS3Objects.ContainsKey(s3ObjectKey) && // object has already been removed from somewhere else
                                                removedS3Objects[s3ObjectKey] > File.GetLastWriteTime(offlineFilePath) && // object was newer then local file
                                                !uploadList.Contains(offlineFilePathInsideMyS3)) // it's not a new file because it's not uploading
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
                                }

                                // ---

                                // 2. Compare and find needed downloads
                                for (int s = 0; s < s3ObjectIndexDict.Count; s++)
                                {
                                    numberOfComparisons++;

                                    string s3ObjectKey = s3ObjectIndexDict.Keys.ElementAt(s);

                                    // S3 object info and metadata
                                    S3Object s3ObjectInfo = s3ObjectIndexDict[s3ObjectKey].Item1;
                                    MetadataCollection s3ObjectMetadata = s3ObjectIndexDict[s3ObjectKey].Item2;

                                    // Get paths
                                    string offlineFilePathInsideMyS3 = null;
                                    try
                                    {
                                        offlineFilePathInsideMyS3 = Encoding.UTF8.GetString(
                                            AesEncryptionWrapper.DecryptForGCM(
                                                Convert.FromBase64String(s3ObjectMetadata["x-amz-meta-encryptedfilepath"]),
                                                EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                                        ));
                                    }
                                    catch (CryptographicException ex)
                                    {
                                        Pause(true);
                                        wrongEncryptionPassword = true;

                                        string problem = "S3 object \"" + s3ObjectKey.Substring(0, 10) + "****" +
                                            "\" cannot be read because of wrong encryption/decryption password - \"" + ex.Message + "\"";

                                        verboseLogFunc(problem);
                                    }
                                    if (!Tools.RunningOnWindows()) offlineFilePathInsideMyS3 = offlineFilePathInsideMyS3.Replace(@"\", @"/");
                                    string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;

                                    // ---

                                    // Offline file exists
                                    if (myS3FileIndexDict.Values.Contains(s3ObjectKey))
                                    {
                                        // Last change
                                        DateTime offlineFileTimeLastChange = File.GetLastWriteTime(offlineFilePath);
                                        DateTime s3FileTimeLastChange = s3ObjectInfo.LastModified; // local time

                                        // Is S3 object newer?
                                        if (s3FileTimeLastChange > offlineFileTimeLastChange)
                                            lock (uploadList)
                                                lock (downloadList)
                                                    lock (renameDict)
                                                        lock (removeList)
                                                            if (!uploadList.Contains(offlineFilePathInsideMyS3) &&
                                                                !downloadList.Contains(s3ObjectKey) &&
                                                                !renameDict.ContainsKey(offlineFilePathInsideMyS3) &&
                                                                !removeList.Contains(offlineFilePathInsideMyS3))
                                                            {
                                                                // Skip if in use
                                                                if (Tools.IsFileLocked(offlineFilePath)) continue;

                                                                downloadList.Add(s3ObjectKey);

                                                                if (verboseLogFunc != null)
                                                                    verboseLogFunc("Found updated S3 object so added it to download queue [" + downloadList.Count + "]");
                                                            }
                                    }

                                    // Offline file doesn't exist
                                    else if (!myS3FileIndexDict.Values.Contains(s3ObjectKey))
                                    {
                                        lock (uploadList)
                                            lock (downloadList)
                                                lock (renameDict)
                                                    lock (removeList)
                                                        if (!uploadList.Contains(offlineFilePathInsideMyS3) &&
                                                            !downloadList.Contains(s3ObjectKey) &&
                                                            !renameDict.ContainsKey(offlineFilePathInsideMyS3) &&
                                                            !removeList.Contains(offlineFilePathInsideMyS3))
                                                        {
                                                            downloadList.Add(s3ObjectKey);

                                                            if (verboseLogFunc != null)
                                                                verboseLogFunc("Found new S3 object so added it to download queue [" + downloadList.Count + "]");
                                                        }
                                    }

                                    // Give other threads access to locked queues
                                    Thread.Sleep(25);
                                }

                                // ---

                                // 3. Compare and find needed uploads
                                for (int m = 0; m < myS3FileIndexDict.Count; m++)
                                {
                                    numberOfComparisons++;

                                    // Get paths
                                    string offlineFilePathInsideMyS3 = myS3FileIndexDict.Keys.ElementAt(m);
                                    string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                                    string s3ObjectKey = myS3FileIndexDict[offlineFilePathInsideMyS3];

                                    // Ignore busy files
                                    if (Tools.IsFileLocked(offlineFilePath)) continue;

                                    // ---

                                    // Offline file already uploaded?
                                    if (s3ObjectIndexDict.ContainsKey(s3ObjectKey))
                                    {
                                        // S3 object info and metadata
                                        S3Object s3ObjectInfo = s3ObjectIndexDict[s3ObjectKey].Item1;
                                        MetadataCollection s3ObjectMetadata = s3ObjectIndexDict[s3ObjectKey].Item2;

                                        // Newer file locally?
                                        if (File.GetLastWriteTime(offlineFilePath) > s3ObjectInfo.LastModified)
                                            lock (uploadList)
                                                lock (downloadList)
                                                    lock (renameDict)
                                                        lock (removeList)
                                                            if (!uploadList.Contains(offlineFilePathInsideMyS3) &&
                                                                !downloadList.Contains(s3ObjectKey) &&
                                                                !renameDict.ContainsKey(offlineFilePathInsideMyS3) &&
                                                                !removeList.Contains(offlineFilePathInsideMyS3))
                                                            {
                                                                uploadList.Add(offlineFilePathInsideMyS3);

                                                                if (verboseLogFunc != null)
                                                                    verboseLogFunc("Found updated local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                                                            "\" so added it to upload queue [" + uploadList.Count + "]");
                                                            }
                                    }

                                    // New file offline?
                                    else if (File.Exists(offlineFilePath))
                                        lock (uploadList)
                                            lock (downloadList)
                                                lock (renameDict)
                                                    lock (removeList)
                                                        if (!uploadList.Contains(offlineFilePathInsideMyS3) &&
                                                            !downloadList.Contains(s3ObjectKey) &&
                                                            !renameDict.ContainsKey(offlineFilePathInsideMyS3) &&
                                                            !removeList.Contains(offlineFilePathInsideMyS3))
                                                        {
                                                            uploadList.Add(offlineFilePathInsideMyS3);

                                                            if (verboseLogFunc != null)
                                                                verboseLogFunc("Found new local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                                                            "\" so added it to upload queue [" + uploadList.Count + "]");
                                                        }

                                    // Give other threads access to locked queues
                                    Thread.Sleep(25);
                                }
                            }
                            catch (Exception ex)
                            {
                                string problem = "A problem occured when trying to compare S3 and MyS3: " + ex.Message;

                                if (verboseLogFunc != null) verboseLogFunc(problem);
                                errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Comparing.log", problem);
                            }

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
                    lock (uploadList)
                        haveUploads = uploadList.Count > 0;
                    while (!IsComparingFiles && haveUploads && !pauseUploads)
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
                        lock (uploadList)
                            offlineFilePathInsideMyS3 = uploadList[0];
                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                        string s3ObjectKey = MyS3FilePathToS3ObjectKey(offlineFilePathInsideMyS3);
                        string encryptedUploadFilePath = null;

                        // Check access and existence
                        lock (changedList)
                            if (changedList.Contains(offlineFilePath))
                            {
                                // Give other threads access to queue and wait for file to be ready
                                Thread.Sleep(25);

                                continue;
                            }
                        if (!File.Exists(offlineFilePath) || Tools.IsFileLocked(offlineFilePath))
                        {
                            // Remove from queue
                            lock (uploadList)
                            {
                                if (uploadList.Contains(offlineFilePathInsideMyS3))
                                    uploadList.Remove(offlineFilePathInsideMyS3);

                                if (verboseLogFunc != null)
                                    verboseLogFunc("Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" removed from upload queue [" + uploadList.Count + "]");

                                haveUploads = uploadList.Count > 0;
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

                            // Encrypt file data and store in new file
                            File.WriteAllBytes(
                                encryptedUploadFilePath,
                                AesEncryptionWrapper.EncryptWithGCM(
                                    File.ReadAllBytes(offlineFilePath),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                                )
                            );

                            /*
                             * Never had an issue so disabling decryption testing
                             * 
                            // Decryption test = abort work if it's unsuccessful
                            byte[] decryptedUploadFileData = AesEncryptionWrapper.DecryptForGCM(
                                File.ReadAllBytes(encryptedUploadFilePath),
                                EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                            );
                            byte[] shrinkedDecryptedUploadFileData = new byte[new FileInfo(offlineFilePath).Length];
                            Array.Copy(decryptedUploadFileData, 0, shrinkedDecryptedUploadFileData, 0, shrinkedDecryptedUploadFileData.Length);

                            if (!File.ReadAllBytes(offlineFilePath).SequenceEqual(shrinkedDecryptedUploadFileData))
                                throw new CryptographicException(
                                    "Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" fails to be encrypted without corruption");
                            decryptedUploadFileData = null;
                            shrinkedDecryptedUploadFileData = null;
                            */

                            // Encrypt file path
                            string encryptedFilePath = Convert.ToBase64String(
                                AesEncryptionWrapper.EncryptWithGCM(
                                    Encoding.UTF8.GetBytes(offlineFilePathInsideMyS3.Replace("/", @"\")),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                            ));

                            // Custom metadata
                            MetadataCollection uploadS3ObjectMetadata = new MetadataCollection();
                            uploadS3ObjectMetadata.Add("x-amz-meta-decryptedsize", new FileInfo(offlineFilePath).Length + "");
                            uploadS3ObjectMetadata.Add("x-amz-meta-encryptedfilepath", encryptedFilePath); // must be updated when renaming

                            // Set progress info
                            UploadPercent = 0;
                            UploadSize = new FileInfo(encryptedUploadFilePath).Length;
                            uploadSpeedCalc.Start(new FileInfo(offlineFilePath).Length);

                            if (verboseLogFunc != null)
                                lock(uploadList)
                                    verboseLogFunc("Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" starts uploading [" +
                                        uploadCounter + "/" + (uploadCounter + uploadList.Count - 1) + "][" + Tools.GetFileSizeAsText(encryptedUploadFilePath) + "]");

                            // Start upload
                            CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
                            Task uploadTask = s3.UploadAsync(encryptedUploadFilePath, s3ObjectKey, offlineFilePathInsideMyS3,
                                uploadS3ObjectMetadata, cancelTokenSource.Token, OnTransferProgressHandler);
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
                                    if (new FileInfo(encryptedUploadFilePath).Length < S3Wrapper.MIN_MULTIPART_SIZE) // <5 MB upload = no S3Wrapper triggered progress report
                                    {
                                        // Upload percent estimate
                                        double newUploadPercent = UploadPercent +
                                            100 * ((UploadSpeed * 25 / 1000) / new FileInfo(encryptedUploadFilePath).Length); // will be zero first upload
                                        if (newUploadPercent > 100) newUploadPercent = 100;
                                        if (!Double.IsNaN(newUploadPercent))
                                            UploadPercent = newUploadPercent;

                                        // Transfer progress report
                                        if (UploadSpeed != 0 &&
                                            (DateTime.Now - timeTransferProgressShown).TotalMilliseconds >= S3Wrapper.PROGRESS_REPORT_PAUSE)
                                        {
                                            timeTransferProgressShown = DateTime.Now;

                                            OnTransferProgressHandler(offlineFilePathInsideMyS3,
                                                (long)((UploadPercent / 100.0) * new FileInfo(encryptedUploadFilePath).Length), new FileInfo(encryptedUploadFilePath).Length, TransferType.UPLOAD);
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

                                // Metadata
                                GetObjectMetadataResponse uploadS3ObjectMetadataResult = s3.GetMetadata(s3ObjectKey, null).Result;
                                S3Object uploadS3ObjectInfo = new S3Object()
                                {
                                    BucketName = Bucket,
                                    Key = s3ObjectKey,
                                    Size = uploadS3ObjectMetadataResult.Headers.ContentLength,
                                    LastModified = uploadS3ObjectMetadataResult.LastModified.ToLocalTime(),
                                    ETag = uploadS3ObjectMetadataResult.ETag,
                                    StorageClass = uploadS3ObjectMetadataResult.StorageClass                                    
                                };

                                // Set last change locally
                                File.SetLastWriteTime(
                                    offlineFilePath,
                                    uploadS3ObjectInfo.LastModified
                                );

                                // Update S3 object index
                                if (s3ObjectIndexDict.ContainsKey(s3ObjectKey))
                                    s3ObjectIndexDict[s3ObjectKey] = Tuple.Create(s3ObjectIndexDict[s3ObjectKey].Item1, uploadS3ObjectMetadata);
                                else
                                    s3ObjectIndexDict.Add(s3ObjectKey, Tuple.Create(uploadS3ObjectInfo, uploadS3ObjectMetadata));

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
                        lock (uploadList)
                            if (uploadList.Contains(offlineFilePathInsideMyS3))
                                uploadList.Remove(offlineFilePathInsideMyS3);

                        // Give other threads access to queues
                        Thread.Sleep(25);

                        lock (uploadList) haveUploads = uploadList.Count > 0;
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
                    lock (downloadList) haveDownloads = downloadList.Count > 0;
                    while (!IsComparingFiles && haveDownloads && !pauseDownloads)
                    {
                        // Remove old downloads from "log"
                        while (true)
                        {
                            bool foundOldDownload = false;
                            foreach (KeyValuePair<string, DateTime> kvp in downloadedDict)
                                if (kvp.Value.AddSeconds(5) < DateTime.Now)
                                {
                                    downloadedDict.Remove(kvp.Key);
                                    foundOldDownload = true;
                                    break;
                                }
                            if (!foundOldDownload) break;
                        }

                        // ---

                        // Get paths
                        string s3ObjectKey;
                        lock (downloadList)
                            s3ObjectKey = downloadList[0];
                        string offlineFilePathInsideMyS3 = null;
                        string encryptedDownloadFilePath = null;

                        try
                        {
                            // Custom metadata
                            MetadataCollection metadata = s3ObjectIndexDict[s3ObjectKey].Item2;

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
                               (Tools.IsFileLocked(offlineFilePath) || File.GetLastWriteTime(offlineFilePath) > s3ObjectIndexDict[s3ObjectKey].Item1.LastModified)) // local time
                            {
                                // Remove from queue
                                lock (downloadList)
                                {
                                    downloadList.Remove(s3ObjectKey);

                                    if (verboseLogFunc != null)
                                        verboseLogFunc("S3 object for \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" removed from download queue");

                                    haveDownloads = downloadList.Count > 0;
                                }

                                continue;
                            }

                            // Set progress info
                            DownloadPercent = 0;
                            DownloadSize = long.Parse(metadata["x-amz-meta-decryptedsize"]);
                            downloadSpeedCalc.Start(DownloadSize);
                            lock (namedDownloadList) namedDownloadList.Add(offlineFilePathInsideMyS3);

                            if (verboseLogFunc != null)
                                lock (downloadList)
                                    verboseLogFunc("S3 object for \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" starts downloading [" + downloadCounter + "/" + (downloadCounter + downloadList.Count - 1) +
                                            "][" + Tools.GetByteSizeAsText(s3ObjectIndexDict[s3ObjectKey].Item1.Size) + "]");

                            // Start download
                            CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
                            Task<MetadataCollection> downloadTask = s3.DownloadAsync(encryptedDownloadFilePath, s3ObjectKey, null, offlineFilePathInsideMyS3,
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
                                File.SetLastWriteTime(offlineFilePath, s3ObjectIndexDict[s3ObjectKey].Item1.LastModified); // local time

                                // Add to file index
                                lock (myS3FileIndexDict)
                                    if (!myS3FileIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                                        myS3FileIndexDict.Add(offlineFilePathInsideMyS3, s3ObjectKey);

                                downloadCounter++;

                                if (verboseLogFunc != null)
                                    verboseLogFunc("Local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" reconstructed");

                                // Add to block queue - in case of slow file activity handling
                                lock (downloadedDict)
                                    downloadedDict.Add(s3ObjectKey, DateTime.Now);
                            }
                        }
                        catch (CryptographicException)
                        {
                            string problem = "S3 object \"" + s3ObjectKey.Substring(0, 10) + "****" + "\" [" +
                                downloadCounter + "/" + (downloadCounter + downloadList.Count - 1) + "]" + " failed to be decrypted. Wrong encryption/decryption password?";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Decryption.log", problem);
                        }
                        catch (Exception ex) // Happens when trying to download deleted files = thrown when trying to first get metadata
                        {
                            string problem = "S3 object \"" + s3ObjectKey.Substring(0, 10) + "****" + "\" [" +
                                downloadCounter + "/" + (downloadCounter + downloadList.Count - 1) + "]" + " not downloaded: " + ex.Message;

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Download.log", problem);
                        }

                        if (File.Exists(encryptedDownloadFilePath)) File.Delete(encryptedDownloadFilePath);

                        // Remove from work queue = only one try
                        lock (downloadList)
                            lock (namedDownloadList)
                            {
                                downloadList.Remove(s3ObjectKey);
                                namedDownloadList.Remove(offlineFilePathInsideMyS3);
                            }

                        // Give other threads access to queues
                        Thread.Sleep(25);

                        lock (downloadList) haveDownloads = downloadList.Count > 0;
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
                    lock (renameDict) haveRenameWork = renameDict.Count > 0;
                    while (haveRenameWork && !pauseUploads)
                    {
                        // Remove old renames from "log"
                        while (true)
                        {
                            bool foundOldRename = false;
                            foreach (KeyValuePair<string, DateTime> oldRenamedKVP in renamedDict)
                                if (oldRenamedKVP.Value.AddSeconds(5) < DateTime.Now)
                                {
                                    renamedDict.Remove(oldRenamedKVP.Key);
                                    foundOldRename = true;
                                    break;
                                }
                            if (!foundOldRename) break;
                        }

                        // ---

                        // Get paths
                        KeyValuePair<string, string> renameKVP;
                        lock (renameDict)
                            renameKVP = renameDict.First();
                        string newOfflineFilePathInsideMyS3 = renameKVP.Key;
                        string newOfflineFilePath = myS3Path + newOfflineFilePathInsideMyS3;
                        string newS3ObjectKey = MyS3FilePathToS3ObjectKey(newOfflineFilePathInsideMyS3);
                        string oldOfflineFilePathInsideMyS3 = renameKVP.Value;
                        string oldOfflineFilePath = myS3Path + oldOfflineFilePathInsideMyS3;
                        string oldS3ObjectKey = MyS3FilePathToS3ObjectKey(oldOfflineFilePathInsideMyS3);

                        // Start process
                        try
                        {
                            // S3 object info
                            S3Object renameS3ObjectInfo = s3ObjectIndexDict[oldS3ObjectKey].Item1;

                            // Custom metadata
                            MetadataCollection renameS3ObjectMetadata = s3ObjectIndexDict[oldS3ObjectKey].Item2;
                            string newEncryptedFilePath = Convert.ToBase64String(
                                AesEncryptionWrapper.EncryptWithGCM(
                                    Encoding.UTF8.GetBytes(newOfflineFilePathInsideMyS3.Replace("/", @"\")),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                            ));
                            renameS3ObjectMetadata.Add("x-amz-meta-encryptedfilepath", newEncryptedFilePath); // overwrites

                            // Copy S3 object
                            s3.CopyAsync(oldS3ObjectKey, newS3ObjectKey, renameS3ObjectMetadata).Wait();

                            // No change so finish work
                            if (File.Exists(newOfflineFilePath))
                            {
                                // Remove old S3 object
                                s3.RemoveAsync(oldS3ObjectKey, null).Wait();

                                // Custom metadata
                                GetObjectMetadataResponse renameS3ObjectMetadataResult = s3.GetMetadata(newS3ObjectKey, null).Result;
                                renameS3ObjectMetadataResult.LastModified = renameS3ObjectMetadataResult.LastModified.ToLocalTime();

                                // Set last changed time
                                File.SetLastWriteTime(newOfflineFilePath, renameS3ObjectMetadataResult.LastModified);

                                // Update S3 object index
                                lock (s3ObjectIndexDict)
                                {
                                    if (s3ObjectIndexDict.ContainsKey(oldS3ObjectKey))
                                        s3ObjectIndexDict.Remove(oldS3ObjectKey);

                                    renameS3ObjectInfo.Key = newS3ObjectKey;
                                    renameS3ObjectInfo.LastModified = renameS3ObjectMetadataResult.LastModified;

                                    if (s3ObjectIndexDict.ContainsKey(newS3ObjectKey))
                                        s3ObjectIndexDict[newS3ObjectKey] = Tuple.Create(renameS3ObjectInfo, renameS3ObjectMetadata);
                                    else
                                        s3ObjectIndexDict.Add(newS3ObjectKey, Tuple.Create(renameS3ObjectInfo, renameS3ObjectMetadata));
                                }

                                renameCounter++;

                                if (verboseLogFunc != null)
                                    lock (renameDict)
                                        verboseLogFunc("S3 object for local file \"" + newOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" renamed [" +
                                                renameCounter + "/" + (renameCounter + renameDict.Count - 1) + "]");

                                // Add to block queue - in case of slow file activity handling
                                lock (renamedDict)
                                {
                                    if (renamedDict.ContainsKey(newS3ObjectKey))
                                        renamedDict[newS3ObjectKey] = DateTime.Now;
                                    else
                                        renamedDict.Add(newS3ObjectKey, DateTime.Now);
                                }
                            }

                            // Aborted so clean up attempt
                            else
                            {
                                s3.RemoveAsync(newS3ObjectKey, null).Wait();
                            }
                        }
                        catch (Exception ex)
                        {
                            string problem = "Problem renaming S3 object \"" + oldS3ObjectKey.Substring(0, 10) + "****" + "\"" + " for local file \"" +
                                oldOfflineFilePathInsideMyS3 + "\" to " + "\"" + newS3ObjectKey.Substring(0, 10) + "****" + "\" for \"" + newOfflineFilePathInsideMyS3 + "\" - \"" + ex.Message + "\"";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            else errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Rename.log", problem);
                        }

                        // Remove from work queue = only one try
                        lock (renameDict)
                            if (renameDict.ContainsKey(newOfflineFilePathInsideMyS3))
                                renameDict.Remove(newOfflineFilePathInsideMyS3);

                        // Give other threads access to queues
                        Thread.Sleep(25);

                        lock (renameDict) haveRenameWork = renameDict.Count > 0;
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
                    lock (removeList) haveRemoveWork = removeList.Count > 0;
                    while (haveRemoveWork && !pauseUploads)
                    {
                        // Get paths
                        string offlineFilePathInsideMyS3 = null;
                        lock(removeList)
                            offlineFilePathInsideMyS3 = removeList[0];
                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                        string s3ObjectKey = MyS3FilePathToS3ObjectKey(offlineFilePathInsideMyS3);

                        // Remove file from S3
                        try
                        {
                            s3.RemoveAsync(s3ObjectKey, null).Wait();

                            // Remove S3 object from index
                            lock(s3ObjectIndexDict)
                                if (s3ObjectIndexDict.ContainsKey(s3ObjectKey))
                                    s3ObjectIndexDict.Remove(s3ObjectKey);

                            if (verboseLogFunc != null)
                                lock (removeList)
                                verboseLogFunc("S3 object for local file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" removed [" +
                                        removeCounter + "/" + (removeCounter + removeList.Count - 1) + "]");

                            removeCounter++;
                        }
                        catch (Exception ex)
                        {
                            string problem = "Problem removing S3 object \"" + s3ObjectKey.Substring(0, 10) + "****" + "\" for local file \"" +
                                offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" - \"" + ex.Message + "\"";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            else errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Remove.log", problem);
                        }

                        // Remove from work queue = only one try
                        lock (removeList)
                            if (removeList.Contains(offlineFilePathInsideMyS3))
                                removeList.Remove(offlineFilePathInsideMyS3);

                        // Give other threads access to queues
                        Thread.Sleep(25);

                        lock (removeList) haveRemoveWork = removeList.Count > 0;
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

        public void RestoreFiles(DateTime timeEarliestLastModified, bool onlyRestoreLastRemoved)
        {
            if (pauseRestore) return;

            // ---

            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) => {

                // Get all S3 object keys and versions
                List<S3ObjectVersion> s3ObjectVersionsList = s3.GetCompleteObjectVersionsList(null);

                // Restore last removed MyS3 objects
                if (onlyRestoreLastRemoved)
                {
                    // Get all delete markers
                    Dictionary<string, List<S3ObjectVersion>> s3ObjectDeleteMarkersDict = new Dictionary<string, List<S3ObjectVersion>>(); // key ==> delete markers
                    foreach (S3ObjectVersion s3ObjectVersion in s3ObjectVersionsList)
                    {
                        // Found delete marker
                        if (s3ObjectVersion.IsDeleteMarker && s3ObjectVersion.LastModified >= timeEarliestLastModified) // local time
                        {
                            // Add object version (delete marker) to list
                            if (s3ObjectDeleteMarkersDict.ContainsKey(s3ObjectVersion.Key))
                                s3ObjectDeleteMarkersDict[s3ObjectVersion.Key].Add(s3ObjectVersion);

                            // Add new key with list containing new object version (delete marker)
                            else
                                s3ObjectDeleteMarkersDict.Add(s3ObjectVersion.Key, new List<S3ObjectVersion>() { s3ObjectVersion });
                        }
                    }

                    // Find and remove ONLY the newest delete markers
                    foreach (KeyValuePair<string, List<S3ObjectVersion>> kvp in s3ObjectDeleteMarkersDict)
                    {
                        List<S3ObjectVersion> s3ObjectDeleteMarkers = kvp.Value;
                        S3ObjectVersion newestS3ObjectDeleteMarker = null;

                        foreach (S3ObjectVersion s3ObjectDeleteMarker in s3ObjectDeleteMarkers)
                            if (newestS3ObjectDeleteMarker == null || s3ObjectDeleteMarker.LastModified > newestS3ObjectDeleteMarker.LastModified) // local times
                                newestS3ObjectDeleteMarker = s3ObjectDeleteMarker;

                        lock (restoreDownloadDict)
                            if (!restoreDownloadDict.ContainsKey(kvp.Key))
                                restoreDownloadDict.Add(
                                    kvp.Key,
                                    new List<string>() { newestS3ObjectDeleteMarker.VersionId }
                                );
                    }

                    // Do the restoring of last removed S3 objects
                    bool hasRestores = false;
                    lock (restoreDownloadDict) hasRestores = restoreDownloadDict.Count > 0;
                    if (hasRestores)
                    {
                        if (verboseLogFunc != null)
                            lock (restoreDownloadDict)
                                verboseLogFunc("Restoring " + restoreDownloadDict.Count + " removed " + (restoreDownloadDict.Count == 1 ? "file" : "files"));

                        // Remove S3 object delete markers
                        s3.RemoveAsync(restoreDownloadDict).Wait();
                        restoreDownloadDict.Clear();

                        // Trigger download of restored S3 objects
                        newS3AndMyS3ComparisonNeeded = true;
                    }
                }

                // Restore every earlier file version in MyS3 and place in restore folder
                else
                {
                    // Find all S3 object versions that fits the parameters
                    foreach (S3ObjectVersion s3ObjectVersion in s3ObjectVersionsList)
                    {
                        // Found new S3 object version
                        if (!s3ObjectVersion.IsDeleteMarker && !s3ObjectVersion.IsLatest &&
                             s3ObjectVersion.LastModified >= timeEarliestLastModified) // local time
                        {
                            lock (restoreDownloadDict)
                            {
                                // Add object version to list
                                if (restoreDownloadDict.ContainsKey(s3ObjectVersion.Key))
                                    restoreDownloadDict[s3ObjectVersion.Key].Add(s3ObjectVersion.VersionId);

                                // Add new key with list containing the new object version
                                else
                                    restoreDownloadDict.Add(
                                        s3ObjectVersion.Key,
                                        new List<string>() { s3ObjectVersion.VersionId }
                                    );
                            }
                        }
                    }

                    // Now do the restoring of earlier file versions
                    bool hasRestoreWork = false;
                    lock (restoreDownloadDict) hasRestoreWork = restoreDownloadDict.Count > 0;
                    if (hasRestoreWork)
                    {
                        if (verboseLogFunc != null)
                            lock (restoreDownloadDict)
                                verboseLogFunc("Restoring file versions for " + restoreDownloadDict.Count + " " + (restoreDownloadDict.Count == 1 ? "file" : "files"));

                        // Run restore worker
                        RestoreFileVersions();
                    }
                }
            }));
        }

        private void RestoreFileVersions()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                int restoreCounter = 1;

                bool hasRestoreWork = false;
                lock (restoreDownloadDict) hasRestoreWork = restoreDownloadDict.Count > 0;
                while (hasRestoreWork && !pauseRestore)
                {
                    // Get paths and versions
                    string s3ObjectKey = null;
                    string[] s3ObjectVersions = null;
                    lock (restoreDownloadDict)
                    {
                        s3ObjectKey = restoreDownloadDict.First().Key;
                        s3ObjectVersions = restoreDownloadDict.First().Value.ToArray();
                    }
                    string originalOfflineFilePathInsideMyS3 = null;
                    string offlineFilePathInsideMyS3 = null;
                    string encryptedFilePathTemp = null;

                    int versionCounter = 1;
                    foreach (string s3ObjectVersion in s3ObjectVersions)
                    {
                        try
                        {
                            // Get metadata
                            GetObjectMetadataResponse metadataResult = s3.GetMetadata(s3ObjectKey, s3ObjectVersion).Result;
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

                                string problem = "S3 object \"" + s3ObjectKey.Substring(0, 10) + "****" +
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
                            lock (namedRestoreDownloadList) namedRestoreDownloadList.Add(offlineFilePathInsideMyS3);
                            if (verboseLogFunc != null)
                                lock (restoreDownloadDict)
                                    verboseLogFunc("S3 object for restoring \"" + originalOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                            "\" [v." + versionCounter + "] starts downloading [" + restoreCounter + "/" + (restoreCounter + restoreDownloadDict.Count / 2 - 1) +
                                                "][" + Tools.GetByteSizeAsText(metadataResult.Headers.ContentLength) + "]");

                            // Start restore
                            CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
                            Task<MetadataCollection> restoreDownloadTask = s3.DownloadAsync(encryptedFilePathTemp, s3ObjectKey, s3ObjectVersion, originalOfflineFilePathInsideMyS3,
                                cancelTokenSource.Token, OnTransferProgressHandler, TransferType.RESTORE);
                            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
                            {
                                try
                                {
                                    restoreDownloadTask.Wait();
                                }
                                catch (Exception)
                                {
                                    if (verboseLogFunc != null)
                                        verboseLogFunc("S3 object download for restoring \"" + 
                                            originalOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + 
                                                "\" [v." + versionCounter + "] aborted");
                                }
                            }));

                            // Wait and maybe abort
                            while (!restoreDownloadTask.IsCompleted && !restoreDownloadTask.IsCanceled)
                                if (pauseRestore) cancelTokenSource.Cancel();
                                else Thread.Sleep(25);

                            // Restore complete
                            if (restoreDownloadTask.IsCompletedSuccessfully)
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
                                Thread.Sleep(500); // give client time to show downloaded state

                                // Remove from work queue
                                lock (restoreDownloadDict)
                                    restoreDownloadDict.Remove(s3ObjectKey);
                                lock (namedRestoreDownloadList)
                                    namedRestoreDownloadList.Remove(offlineFilePathInsideMyS3);
                            }

                            versionCounter++;
                        }
                        catch (CryptographicException)
                        {
                            lock (restoreDownloadDict)
                            {
                                string problem = "S3 restore file \"" + s3ObjectKey.Substring(0, 10) + "****" + "\" [" +
                                    restoreCounter + "/" + (restoreCounter + restoreDownloadDict.Count - 1) + "]" + " failed to be decrypted. Wrong encryption/decryption password?";

                                if (verboseLogFunc != null) verboseLogFunc(problem);
                                errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Decryption.log", problem);
                            }
                        }
                        catch (Exception ex) // Happens when trying to download deleted files or it's metadata
                        {
                            lock (restoreDownloadDict)
                            {
                                string problem = "S3 restore file \"" + s3ObjectKey.Substring(0, 10) + "****" + "\" [" +
                                restoreCounter + "/" + (restoreCounter + restoreDownloadDict.Count - 1) + "]" + " not downloaded: " + ex.Message;

                                if (verboseLogFunc != null) verboseLogFunc(problem);
                                errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Restore.log", problem);
                            }
                        }

                        if (File.Exists(encryptedFilePathTemp)) File.Delete(encryptedFilePathTemp);
                    }

                    // Give other threads access to queues
                    Thread.Sleep(25);

                    lock (restoreDownloadDict) hasRestoreWork = restoreDownloadDict.Count > 0;
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
