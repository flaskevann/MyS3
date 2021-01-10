using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using EncryptionAndHashingLibrary;

using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace MyS3
{
    public class MyS3Runner : IDisposable
    {
        public static string DEFAULT_RELATIVE_LOCAL_MYS3_DIRECTORY_PATH
        {
            get
            {
                return Tools.RunningOnWindows() ?
                    @"%userprofile%\Documents\MyS3\" : // on Windows
                    @"%HOME%/Documents/MyS3/";         // on *nix
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
            ".________________________" // use this if creating test files
        };

        private static readonly int MILLISECONDS_EXTRA_TIME_FOR_FINISHING_WRITING_FILE_BEFORE_UPLOAD_OR_AFTER_DOWNLOAD = 10;
        private static readonly int SECONDS_MIN_PAUSE_BETWEEN_OPERATIONS_ON_SAME_FILE = 3; // Min pause until next upload or download of the same file,
                                                                                           // in case of slow file activity handling
        private static readonly int SECONDS_PAUSE_BETWEEN_EACH_S3_AND_MYS3_COMPARISON_WHEN_SHARED_BUCKET = 60;
        private static readonly int INACTIVITY_PAUSE = 3000;

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

        private void ReadyWorkDirectories()
        {
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
                }
                if (!Directory.Exists(myS3Path + directory))
                    throw new DirectoryNotFoundException(
                        "Aborting. Unable to use path \"" + (myS3Path + directory) + "\" for MyS3 operations");

                if (RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH.StartsWith(directory))
                    File.SetAttributes(myS3Path + directory,
                        File.GetAttributes(myS3Path + directory) | FileAttributes.Hidden);
            }
        }

        public void Setup() // call before Start()
        {
            // Setup directory
            if (myS3Path == null)
                myS3Path = Environment.ExpandEnvironmentVariables(DEFAULT_RELATIVE_LOCAL_MYS3_DIRECTORY_PATH);
            ReadyWorkDirectories();
                        
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

            // Make sure S3 bucket has versioning
            if (s3.GetVersioningAsync().VersioningConfig.Status != VersionStatus.Enabled &&
                s3.SetVersioningAsync().HttpStatusCode != HttpStatusCode.OK)
                throw new Exception("Unable to set versioning on bucket and saving removed or overwritten files");

            // Clean up old removed S3 objects
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                int numberOfFiles = s3.CleanUpRemovedObjects(DateTime.Now.AddYears(-1));
                if (numberOfFiles > 0 && verboseLogFunc != null)
                    verboseLogFunc("Cleaned up after some very old removed S3 objects");
            }));

            // Start watching files
            fileMonitor = new FileMonitor(myS3Path,
                IGNORED_DIRECTORIES_NAMES, IGNORED_FILE_EXTENSIONS,
                OnChangeFileHandler, OnRenameFileOrDirectoryHandler, OnRemoveFileOrDirectoryHandler);
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                fileMonitor.Start();
            }));
            if (verboseLogFunc != null)
                verboseLogFunc("Started monitoring MyS3 folder for new activity");

            // Start workers
            StartComparingS3AndMyS3();
            StartDownloadWorker();                    
            StartUploadWorker();                  
            StartRenameWorker();
            StartRemoveWorker();
        }

        public bool WrongEncryptionPassword { get { return wrongEncryptionPassword; } }
        private bool wrongEncryptionPassword = false;

        public bool DownloadsPaused { get { return pauseDownloads; } }
        private bool pauseDownloads = false;

        public bool RestorePaused { get { return pauseRestores; } }
        private bool pauseRestores = false;

        public void PauseDownloadsAndRestores(bool pause)
        {
            // No point in continuing if wrong password:
            // S3 object keys and content can't be decrypted
            if (!pause && wrongEncryptionPassword) return;

            this.pauseDownloads = pause;
            this.pauseRestores = pause; // when restoring, MyS3 is actually downloading

            // Pausing also stopped S3 object indexing and comparisons of S3 objects and files, so run again now
            if (!pause) TriggerS3AndMyS3Comparison();

            string text = pauseDownloads ? "MyS3 downloads and restores set to paused" : "MyS3 downloads and restores set to continue";
            if (verboseLogFunc != null) verboseLogFunc(text);
        }

        public bool UploadsPaused { get { return pauseUploads; } }
        private bool pauseUploads = false;

        public void PauseUploads(bool pause)
        {
            // Can't continue if wrong password:
            // S3 objects encrypted with different passwords would get uploaded
            if (!pause && wrongEncryptionPassword) return;

            this.pauseUploads = pause;

            string text = pauseUploads ? "MyS3 uploads set to paused" : "MyS3 uploads set to continue";
            if (verboseLogFunc != null) verboseLogFunc(text);
        }

        public void Pause(bool pause)
        {
            PauseUploads(pause);
            PauseDownloadsAndRestores(pause);

            string text = pause ? "All MyS3 activity set to pause" : "All MyS3 activity set to continue";
            if (verboseLogFunc != null) verboseLogFunc(text);
        }

        public bool Stopping { get { return stop; } }
        private volatile bool stop = false;

        public void Stop()
        {
            // Stop monitoring files
            fileMonitor.Stop();

            // Terminate
            Pause(true);
            stop = true;

            if (verboseLogFunc != null)
                verboseLogFunc("MyS3 terminated, goodbye!");
        }

        // ---

        private Dictionary<string, DateTime> myS3FileIndexDict = new Dictionary<string, DateTime>(); // MyS3 file path ==> time last modified UTC
        private Dictionary<string, S3ObjectMetadata> s3ObjectIndexDict = new Dictionary<string, S3ObjectMetadata>(); // S3 object key ==> S3 object metadata

        public ImmutableList<string> UploadList
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(uploadListHashSet, 10, ref acquiredLock);
                    if (acquiredLock)
                        return uploadListHashSet.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(uploadListHashSet);
                }
            }
        }
        private HashSet<string> uploadListHashSet = new HashSet<string>(); // MyS3 file paths
        private Dictionary<string, DateTime> uploadedListDict = new Dictionary<string, DateTime>(); // MyS3 file path ==> time uploaded

        public ImmutableList<string> DownloadList
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(downloadListHashSet, 10, ref acquiredLock);
                    if (acquiredLock)
                        return downloadListHashSet.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(downloadListHashSet);
                }
            }
        }
        private HashSet<string> downloadListHashSet = new HashSet<string>(); // MyS3 file paths
        private Dictionary<string, DateTime> downloadedListDict = new Dictionary<string, DateTime>(); // MyS3 file path ==> time downloaded

        private Dictionary<string, string> renameListDict = new Dictionary<string, string>(); // MyS3 new file path ==> old file path
        private Dictionary<string, DateTime> renamedListDict = new Dictionary<string, DateTime>(); // MyS3 new file path ==> time renamed

        private HashSet<string> removeListHashSet = new HashSet<string>(); // MyS3 file paths
        private Dictionary<string, DateTime> removedListDict = new Dictionary<string, DateTime>(); // MyS3 new file path ==> time renamed

        public ImmutableList<string> RestoreDownloadList
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(restoreDownloadListDict, 10, ref acquiredLock);
                    if (acquiredLock)
                        return restoreDownloadListDict.Keys.ToImmutableList<string>();
                    else
                        return null;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(restoreDownloadListDict);
                }
            }
        }
        private Dictionary<string, HashSet<S3ObjectVersion>> restoreDownloadListDict
            = new Dictionary<string, HashSet<S3ObjectVersion>>(); // MyS3 file path ==> list of S3 object versions

        // ---

        public bool IsIndexingMyS3Files { get { return isIndexingMyS3Files; } }
        private bool isIndexingMyS3Files = false;

        //

        public int NumberOfS3Files
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(s3ObjectIndexDict, 10, ref acquiredLock);
                    if (acquiredLock)
                        lastNumberOfS3Files = s3ObjectIndexDict.Count;
                    return lastNumberOfS3Files;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(s3ObjectIndexDict);
                }
            }
        }
        private int lastNumberOfS3Files = 0;

        public int NumberOfMyS3Files
        {
            get
            {
                bool acquiredLock = false;
                try
                {
                    Monitor.TryEnter(myS3FileIndexDict, 10, ref acquiredLock);
                    if (acquiredLock)
                        lastNumberOfMyS3Files = myS3FileIndexDict.Count;
                    return lastNumberOfMyS3Files;
                }
                finally
                {
                    if (acquiredLock)
                        Monitor.Exit(myS3FileIndexDict);
                }
            }
        }
        private int lastNumberOfMyS3Files = 0;

        public long GetSumMyS3FilesSize()
        {
            bool acquiredLock = false;
            try
            {
                Monitor.TryEnter(myS3FileIndexDict, 10, ref acquiredLock);
                if (acquiredLock)
                {
                    long size = 0;
                    foreach (string offlineFilePathInsideMyS3 in myS3FileIndexDict.Keys)
                        if (File.Exists(myS3Path + offlineFilePathInsideMyS3))
                            size += (new FileInfo(myS3Path + offlineFilePathInsideMyS3)).Length;

                    sumFileSize = size;
                }
                return sumFileSize;
            }
            finally
            {
                if (acquiredLock)
                    Monitor.Exit(myS3FileIndexDict);
            }
        }
        private long sumFileSize = 0;

        //

        public void TriggerS3AndMyS3Comparison()
        {
            newS3AndMyS3ComparisonNeeded = true;
        }
        private bool newS3AndMyS3ComparisonNeeded;
        private DateTime timeLastActivity;

        //

        public bool IsIndexingS3Objects { get { return isIndexingS3Objects; } }
        private bool isIndexingS3Objects = false;

        public bool IsComparingS3AndMyS3 { get { return isComparingS3AndMyS3; } }
        private bool isComparingS3AndMyS3 = false;

        // ---

        public void OnChangeFileHandler(string offlineFilePath) // on file change
        {
            // Get path and last modified time
            string offlineFilePathInsideMyS3 = offlineFilePath.Replace(myS3Path, "");

            // ---

            // Reacting to and handling different (hopefully rare) situations ...:

            bool abort = false;

            lock (uploadListHashSet)
                if (uploadListHashSet.Contains(offlineFilePathInsideMyS3))
                    abort = true; // recent change so file path already in upload queue and no need to do anything
            lock (uploadedListDict)
                if (uploadedListDict.ContainsKey(offlineFilePathInsideMyS3) &&
                    uploadedListDict[offlineFilePathInsideMyS3].AddSeconds(SECONDS_MIN_PAUSE_BETWEEN_OPERATIONS_ON_SAME_FILE) > DateTime.Now)
                    abort = true; // file already uploaded a few seconds ago so can't upload again

            lock (renameListDict)
                if (renameListDict.ContainsKey(offlineFilePathInsideMyS3))
                    abort = true; // file renamed or its path changed so can't upload yet

            lock (downloadListHashSet)
                if (downloadListHashSet.Contains(offlineFilePathInsideMyS3))
                    downloadListHashSet.Remove(offlineFilePathInsideMyS3); // stop planned download which will overwrite the newly changed local file
            lock (downloadedListDict)
                if (Tools.IsFileLocked(offlineFilePath) || (downloadedListDict.ContainsKey(offlineFilePathInsideMyS3) &&
                    downloadedListDict[offlineFilePathInsideMyS3].AddSeconds(SECONDS_MIN_PAUSE_BETWEEN_OPERATIONS_ON_SAME_FILE) > DateTime.Now))
                    abort = true; // recently downloaded and possibly the trigger so upload aborted

            lock (removeListHashSet)
                if (removeListHashSet.Contains(offlineFilePathInsideMyS3))
                    removeListHashSet.Remove(offlineFilePathInsideMyS3); // file undeleted OR file removed and then new file with same file path added
                                                                         // not removing entry could remove the new S3 object _after_ this upload

            if (abort) return;

            // ---

            // Add file to upload queue
            lock (uploadListHashSet)
                uploadListHashSet.Add(offlineFilePathInsideMyS3);

            if (verboseLogFunc != null)
                verboseLogFunc("File \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                    "\" added to upload queue [" + uploadListHashSet.Count + "]");
        }

        public void OnRenameFileOrDirectoryHandler(string newOfflinePath, string oldOfflinePath, bool isDirectory)
        {
            // Get paths
            string newOfflinePathInsideMyS3 = newOfflinePath.Replace(myS3Path, ""); // file or directory
            string oldOfflinePathInsideMyS3 = oldOfflinePath.Replace(myS3Path, ""); // file or directory

            // Handle directory
            if (isDirectory)
            {
                // Get paths
                Dictionary<string, string> renamedFileDict = new Dictionary<string, string>();
                lock (myS3FileIndexDict)
                    foreach (string offlineFilePathInsideMyS3 in myS3FileIndexDict.Keys)
                        if (offlineFilePathInsideMyS3.StartsWith(oldOfflinePathInsideMyS3+(Tools.RunningOnWindows() ? @"\" : "/")))
                            renamedFileDict.Add(
                                newOfflinePathInsideMyS3 + offlineFilePathInsideMyS3.Substring(oldOfflinePathInsideMyS3.Length), // new path
                                offlineFilePathInsideMyS3); // old path

                // Trigger renaming
                foreach (KeyValuePair<string, string> offlineFilePathInsideMyS3KVP in renamedFileDict)
                    OnRenameFileOrDirectoryHandler(
                        offlineFilePathInsideMyS3KVP.Key,
                        offlineFilePathInsideMyS3KVP.Value,
                        false
                    );

                return;
            }

            // ---

            // Update index no matter what else is done
            lock (myS3FileIndexDict)
            {
                myS3FileIndexDict.Add(newOfflinePathInsideMyS3, myS3FileIndexDict[oldOfflinePathInsideMyS3]);
                myS3FileIndexDict.Remove(oldOfflinePathInsideMyS3);
            }

            // ---

            // Reacting and handling different (hopefully rare) situations ...:

            bool abort = false;

            lock (uploadListHashSet)
            {
                if (uploadListHashSet.Contains(oldOfflinePathInsideMyS3))
                    uploadListHashSet.Remove(oldOfflinePathInsideMyS3); // stop upload attempt, the file doesn't exist anymore

                if (uploadListHashSet.Contains(newOfflinePathInsideMyS3))
                    uploadListHashSet.Remove(newOfflinePathInsideMyS3); // stop upload attempt because the file was moved - it's not new
            }

            lock (renameListDict)
            {
                if (renameListDict.ContainsKey(oldOfflinePathInsideMyS3))
                    renameListDict.Remove(oldOfflinePathInsideMyS3); // stop a previous rename attempt
                                                                     // no need for middle man: original_name ==> new_name1 ==> new_name2
                                                                     // instead, in S3: original_name ==> new_name2
                if (renamedListDict.ContainsKey(newOfflinePath))
                    abort = true; // already in rename queue so do nothing
            }

            lock (downloadListHashSet)
                if (downloadListHashSet.Contains(oldOfflinePathInsideMyS3))
                    downloadListHashSet.Remove(oldOfflinePathInsideMyS3); // stop planned download because file name will be wrong

            lock (removeListHashSet)
            {
                if (removeListHashSet.Contains(oldOfflinePathInsideMyS3))
                    removeListHashSet.Remove(oldOfflinePathInsideMyS3); // stop a remove attempt, S3 object has to be renamed instead

                if (removeListHashSet.Contains(newOfflinePathInsideMyS3))
                    removeListHashSet.Remove(newOfflinePathInsideMyS3); // older file removed and then different file renamed to the same name
            }                                                           // not removing entry could remove the renamed S3 object afterwards

            if (abort) return;

            // ---

            lock (s3ObjectIndexDict)
                if (!s3ObjectIndexDict.ContainsKey(oldOfflinePathInsideMyS3))
                    return; // file not in S3 so can't rename it

            // Add file to rename queue
            lock (s3ObjectIndexDict)
                lock (renameListDict)
                {
                    renameListDict.Add(newOfflinePathInsideMyS3, oldOfflinePathInsideMyS3);

                    if (verboseLogFunc != null)
                        verboseLogFunc("S3 object for file \"" + oldOfflinePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                            "\" added to copy and rename queue [" + renameListDict.Count + "]");
                }
        }

        public void OnRemoveFileOrDirectoryHandler(string offlinePath, bool isDirectory)
        {
            // Get paths
            string offlinePathInsideMyS3 = offlinePath.Replace(myS3Path, ""); // file or directory

            // Handle directory
            if (isDirectory)
            {
                // Get paths
                HashSet<string> removedFileHashSet = new HashSet<string>();
                lock (myS3FileIndexDict)
                    foreach (string offlineFilePathInsideMyS3 in myS3FileIndexDict.Keys)
                        if (offlineFilePathInsideMyS3.StartsWith(offlinePathInsideMyS3 + (Tools.RunningOnWindows() ? @"\" : "/")))
                            removedFileHashSet.Add(offlineFilePathInsideMyS3);

                // Trigger removal
                foreach (string offlineFilePathInsideMyS3 in removedFileHashSet)
                    OnRemoveFileOrDirectoryHandler(myS3Path + offlineFilePathInsideMyS3, false);

                return;
            }

            // ---

            // Reacting and handling different (hopefully rare) situations ...:
            
            lock (uploadListHashSet)
                if (uploadListHashSet.Contains(offlinePathInsideMyS3))
                    uploadListHashSet.Remove(offlinePathInsideMyS3); // stop attempt to upload file that doesn't exist anymore

            lock (renameListDict)              // below: a new file path of a file
                if (renameListDict.ContainsKey(offlinePathInsideMyS3)) // S3 object not "renamed" yet ..
                                                                       // (renamed = copied and old object removed)
                {
                    // Trigger removal of S3 object that belonged to the now renamed local file
                    OnRemoveFileOrDirectoryHandler(myS3Path + renameListDict[offlinePathInsideMyS3], false);

                    renameListDict.Remove(offlinePathInsideMyS3); // stop rename attempt for file that doesn't exist anymore
                }

            lock (downloadListHashSet)
                if (downloadListHashSet.Contains(offlinePathInsideMyS3))
                    downloadListHashSet.Remove(offlinePathInsideMyS3); // stop planned download now that local older file is removed

            // ---

            lock (myS3FileIndexDict)
                if (myS3FileIndexDict.ContainsKey(offlinePathInsideMyS3))
                    myS3FileIndexDict.Remove(offlinePathInsideMyS3);

            lock (s3ObjectIndexDict)
                if (!s3ObjectIndexDict.ContainsKey(offlinePathInsideMyS3))
                    return; // S3 object doesn't exist so nothing to remove

            // Add to removal queue
            lock (removeListHashSet)
            {
                removeListHashSet.Add(offlinePathInsideMyS3);

                if (verboseLogFunc != null)
                    verboseLogFunc("S3 object for file \"" + offlinePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                        "\" added to removal queue [" + removeListHashSet.Count + "]");
            }
        }

        // ---

        public enum TransferType { DOWNLOAD, UPLOAD, RESTORE }

        public void OnTransferProgressHandler(string shownFilePath, long transferredBytes, long totalBytes, TransferType transferType)
        {
            double percentDone = (((double) transferredBytes / (double) totalBytes) * 100);
            percentDone = Math.Round(percentDone, 2);
            if (percentDone > 100) percentDone = 100;
            else if (percentDone < 0) percentDone = 0;

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

            if (verboseLogFunc != null)
            {
                string text = "File \"" + shownFilePath.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" " + typeText + " [" +
//                  Tools.GetByteSizeAsText(transferredBytes) + " / " + Tools.GetByteSizeAsText(totalBytes) + "][" +
                    Tools.ReplaceCommaAndAddTrailingZero(percentDone + "") + " %]";

                verboseLogFunc(text);
            }
        }

        // ---

        private void StartComparingS3AndMyS3()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) => {

                DateTime timeLastCompare = DateTime.UnixEpoch; // Triggers comparison when starting

                while (!stop)
                {
                    // Wait for the right moment to run comparisons
                    bool activityDone = false;
                    lock (uploadListHashSet)
                        lock (downloadListHashSet)
                            lock (renameListDict)
                                lock (removeListHashSet)
                                {
                                    if (uploadListHashSet.Count != 0 || downloadListHashSet.Count != 0 ||
                                        renameListDict.Count != 0 || removeListHashSet.Count != 0)
                                            timeLastActivity = DateTime.Now;

                                    activityDone =
                                        uploadListHashSet.Count == 0 && downloadListHashSet.Count == 0 &&
                                        renameListDict.Count == 0 && removeListHashSet.Count == 0;
                                }

                    // ---

                    // Ready to start work
                    if (activityDone && timeLastActivity.AddSeconds(3) < DateTime.Now) // Give S3 a little time to finish last activity
                    {
                        // S3 and MyS3 comparison also runs on a schedule if bucket is shared
                        if (sharedBucketWithMoreComparisons &&
                            timeLastCompare.AddSeconds(SECONDS_PAUSE_BETWEEN_EACH_S3_AND_MYS3_COMPARISON_WHEN_SHARED_BUCKET) < DateTime.Now)
                                newS3AndMyS3ComparisonNeeded = true;

                        // ---

                        // New change in MyS3 directory or comparison was requested
                        if (Directory.GetLastWriteTime(myS3Path) > timeLastCompare || newS3AndMyS3ComparisonNeeded)
                        {
                            newS3AndMyS3ComparisonNeeded = false;

                            // Clean up work directory
                            foreach (string path in Directory.GetFiles(myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH))
                                try { File.Delete(path); } catch (Exception) { }

                            // ---

                            try
                            {
                                // 1. Index MyS3 files (again)

                                isIndexingMyS3Files = true;

                                if (verboseLogFunc != null)
                                    verboseLogFunc("Indexing all accessible files in MyS3");

                                // Find new files and add to index
                                HashSet<string> newMyS3FileIndexHashSet = new HashSet<string>();
                                foreach (string offlineFilePath in Directory.GetFiles(myS3Path, "*", SearchOption.AllDirectories))
                                {
                                    // Get paths
                                    string offlineFilePathInsideMyS3 = offlineFilePath.Replace(myS3Path, "");

                                    // Skip log and work folders
                                    if (offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH) ||
                                        offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH) ||
                                        offlineFilePath.StartsWith(myS3Path + RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH)) continue;

                                    // Ignore certain file extensions
                                    if (IGNORED_FILE_EXTENSIONS.Contains(Path.GetExtension(offlineFilePath).ToLower())) continue;

                                    // Ignore files without access
                                    if (Tools.IsFileLocked(offlineFilePath))
                                        continue;

                                    // Add to index
                                    newMyS3FileIndexHashSet.Add(offlineFilePathInsideMyS3);
                                }
                                lock (myS3FileIndexDict)
                                    foreach (string offlineFilePathInsideMyS3 in newMyS3FileIndexHashSet)
                                        if (!myS3FileIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                                            myS3FileIndexDict.Add(
                                                offlineFilePathInsideMyS3,
                                                File.GetLastWriteTimeUtc(myS3Path + offlineFilePathInsideMyS3));

                                isIndexingMyS3Files = false;

                                // ---

                                // 2. Index S3 objects (again)

                                // Abort if downloads paused = network connection might be missing
                                if (pauseDownloads)
                                {
                                    timeLastCompare = DateTime.Now;

                                    continue;
                                }

                                isIndexingS3Objects = true;

                                if (verboseLogFunc != null)
                                    verboseLogFunc("Indexing S3 objects and retrieving metadata");

                                // Get list of removed S3 objects
                                List<S3ObjectMetadata> removedS3ObjectsList = new List<S3ObjectMetadata>();
                                if (sharedBucketWithMoreComparisons)
                                    removedS3ObjectsList = s3.GetCompleteRemovedObjectList().Select(
                                        x => new S3ObjectMetadata(x.Key, encryptionPassword)).ToList();

                                // Build fresh S3 object index
                                List<S3Object> s3ObjectInfoList = s3.GetCompleteObjectList();
                                if (s3ObjectInfoList.Count > 0)
                                {
                                    Dictionary<string, S3ObjectMetadata> newS3ObjectIndexDict = new Dictionary<string, S3ObjectMetadata>();

                                    // Build index from S3 object keys (which has metadata)
                                    foreach (S3Object s3ObjectInfo in s3ObjectInfoList)
                                    {
                                        // Found wrong type of S3 object = skipping
                                        if (!S3ObjectMetadata.IsValidS3ObjectKeyWithMetadata(s3ObjectInfo.Key)) continue; // perhaps left over test object, woops

                                        // ---

                                        // Get metadata
                                        S3ObjectMetadata anotherS3ObjectMetadata =
                                            new S3ObjectMetadata(s3ObjectInfo.Key, encryptionPassword); // throws CryptographicException if decryption fails

                                        // S3 object just now deleted, skipping
                                        if (removedS3ObjectsList.Contains(anotherS3ObjectMetadata)) continue;

                                        // Get paths
                                        string anotherOfflineFilePathInsideMyS3 = anotherS3ObjectMetadata.OfflineFilePathInsideMyS3;

                                        // Replace or add metadata
                                        if (newS3ObjectIndexDict.ContainsKey(anotherOfflineFilePathInsideMyS3)) // already added
                                            if (anotherS3ObjectMetadata.LastModifiedUTC > newS3ObjectIndexDict[anotherOfflineFilePathInsideMyS3].LastModifiedUTC)
                                            {
                                                // Remove older
                                                string oldS3ObjectKeyWithMetadata = newS3ObjectIndexDict[anotherOfflineFilePathInsideMyS3].ToString();
                                                s3.RemoveAsync(oldS3ObjectKeyWithMetadata, null).Wait(); // older version that should have been removed when updated

                                                // Add new
                                                newS3ObjectIndexDict[anotherOfflineFilePathInsideMyS3] = anotherS3ObjectMetadata;
                                            }
                                            else
                                            {
                                                // Remove older
                                                string oldS3ObjectKeyWithMetadata = anotherS3ObjectMetadata.ToString();
                                                s3.RemoveAsync(oldS3ObjectKeyWithMetadata, null).Wait(); // older version that should have been removed when updated
                                            }
                                        else
                                            newS3ObjectIndexDict.Add(anotherS3ObjectMetadata.OfflineFilePathInsideMyS3, anotherS3ObjectMetadata);
                                    }
                                    lock (s3ObjectIndexDict)
                                        foreach (KeyValuePair<string, S3ObjectMetadata> newS3ObjectMetadataKVP in newS3ObjectIndexDict)
                                            if (s3ObjectIndexDict.ContainsKey(newS3ObjectMetadataKVP.Key))
                                                s3ObjectIndexDict[newS3ObjectMetadataKVP.Key] = newS3ObjectMetadataKVP.Value;
                                            else
                                                s3ObjectIndexDict.Add(newS3ObjectMetadataKVP.Key, newS3ObjectMetadataKVP.Value);
                                }

                                isIndexingS3Objects = false;

                                // ---

                                // 3. Run comparisons

                                isComparingS3AndMyS3 = true;

                                if (verboseLogFunc != null)
                                    verboseLogFunc("Comparing S3 objects [" + NumberOfS3Files + "]" +
                                        " and MyS3 files [" + NumberOfMyS3Files + " = " + Tools.GetByteSizeAsText(GetSumMyS3FilesSize()) + "]");

                                // 3.1 Find files locally that should be removed when already removed in S3 from elsewhere (shared bucket only)
                                if (removedS3ObjectsList.Count > 0)
                                {
                                    // Copy file index
                                    Dictionary<string, DateTime> copiedMyS3FileIndexDict;
                                    lock (myS3FileIndexDict)
                                        copiedMyS3FileIndexDict = myS3FileIndexDict.ToDictionary(x => x.Key, x => x.Value);

                                    // Check every removed S3 object (delete marker) against file index
                                    foreach (S3ObjectMetadata removedS3ObjectWithMetadata in removedS3ObjectsList)
                                    {
                                        // Get paths
                                        string offlineFilePathInsideMyS3 = removedS3ObjectWithMetadata.OfflineFilePathInsideMyS3;
                                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;

                                        // Skip if file doesn't exist (perhaps already removed)
                                        if (!File.Exists(offlineFilePath)) continue;

                                        // Get last modified and file hashes
                                        DateTime offlineFileLastModified = copiedMyS3FileIndexDict[offlineFilePathInsideMyS3];
                                        string offlineFileHash = Tools.DataToHash(File.ReadAllBytes(offlineFilePath));
                                        DateTime removedS3ObjectLastModifiedUTC = removedS3ObjectWithMetadata.LastModifiedUTC;
                                        string removedS3ObjectFileHash = removedS3ObjectWithMetadata.FileHash;

                                        // Remove local file
                                        if (!s3ObjectIndexDict.ContainsKey(offlineFilePathInsideMyS3) &&                // File not in index = file removed and not overwritten (overwritten == DON'T DELETE!)
                                            removedS3ObjectLastModifiedUTC.AddSeconds(5) >= offlineFileLastModified &&  // Same time stamp (more or less)
                                            removedS3ObjectFileHash == offlineFileHash)                                 // Same file contents
                                        {                                                                               // = should be removed locally                                            
                                            // Remove file
                                            try { File.Delete(offlineFilePath); } catch (Exception) { } // trigger file remove handler with necessary actions

                                            // Remove empty directories
                                            string path = Directory.GetParent(myS3Path + offlineFilePathInsideMyS3).FullName;
                                            while ((path + (Tools.RunningOnWindows() ? @"\" : @"/")) != myS3Path) // not root path for removal attempt
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
                                }

                                // ---

                                // 3.2 Compare and find needed downloads
                                lock (s3ObjectIndexDict)
                                    foreach (KeyValuePair<string, S3ObjectMetadata> s3ObjectIndexEntryKVP in s3ObjectIndexDict)
                                    {
                                        // Get paths
                                        string offlineFilePathInsideMyS3 = s3ObjectIndexEntryKVP.Key;
                                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;

                                        // ---

                                        // Offline file exists
                                        if (myS3FileIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                                        {
                                            // Hash and last modified
                                            DateTime offlineFileLastModifiedUTC;
                                            string offlineFileHash = Tools.DataToHash(File.ReadAllBytes(offlineFilePath));
                                            lock (myS3FileIndexDict)
                                                offlineFileLastModifiedUTC = myS3FileIndexDict[offlineFilePathInsideMyS3];
                                            DateTime s3ObjectLastModifiedUTC = s3ObjectIndexEntryKVP.Value.LastModifiedUTC;
                                            string s3ObjectFileHash = s3ObjectIndexEntryKVP.Value.FileHash;

                                            // When S3 object is new(er)
                                            if (s3ObjectLastModifiedUTC > offlineFileLastModifiedUTC && s3ObjectFileHash != offlineFileHash)
                                            {
                                                // Skip if in use
                                                if (Tools.IsFileLocked(offlineFilePath)) continue;

                                                // Add to download queue
                                                lock (uploadListHashSet)
                                                    lock (downloadListHashSet)
                                                        lock (renameListDict)
                                                            lock (removeListHashSet)
                                                                if (!uploadListHashSet.Contains(offlineFilePathInsideMyS3) && // not set to upload
                                                                    !downloadListHashSet.Contains(offlineFilePathInsideMyS3) && // not already set to download
                                                                    !renameListDict.Values.Contains(offlineFilePathInsideMyS3) && // not set to be renamed
                                                                    !removeListHashSet.Contains(offlineFilePathInsideMyS3)) // not set to be removed
                                                                {
                                                                    downloadListHashSet.Add(offlineFilePathInsideMyS3);

                                                                    if (verboseLogFunc != null)
                                                                        verboseLogFunc("Found updated S3 object so added it to download queue [" +
                                                                            downloadListHashSet.Count + "]");
                                                                }
                                            }
                                        }

                                        // Offline file doesn't exist
                                        else
                                        {
                                            lock (uploadListHashSet)
                                                lock (downloadListHashSet)
                                                    lock (renameListDict)
                                                        lock (removeListHashSet)
                                                            if (!uploadListHashSet.Contains(offlineFilePathInsideMyS3) && // not set to upload
                                                                !downloadListHashSet.Contains(offlineFilePathInsideMyS3) && // not already set to download
                                                                !renameListDict.ContainsKey(offlineFilePathInsideMyS3) && // not set to be renamed
                                                                !removeListHashSet.Contains(offlineFilePathInsideMyS3)) // not set to be removed
                                                            {
                                                                // Add to download queue
                                                                downloadListHashSet.Add(offlineFilePathInsideMyS3);

                                                                if (verboseLogFunc != null)
                                                                    verboseLogFunc("Found new S3 object so added it to download queue [" +
                                                                        downloadListHashSet.Count + "]");
                                                            }
                                        }
                                    }

                                // ---

                                // 3.3 Compare and find needed uploads
                                lock (myS3FileIndexDict)
                                    foreach (KeyValuePair<string, DateTime> myS3FileIndexEntryKVP in myS3FileIndexDict)
                                    {
                                        // Get paths
                                        string offlineFilePathInsideMyS3 = myS3FileIndexEntryKVP.Key;
                                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;

                                        // Ignore busy files
                                        if (Tools.IsFileLocked(offlineFilePath)) continue;

                                        // ---

                                        // S3 object already exists 
                                        if (s3ObjectIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                                        {
                                            // Hash and last modified
                                            DateTime offlineFileLastModifiedUTC = myS3FileIndexEntryKVP.Value;
                                            string offlineFileHash = Tools.DataToHash(File.ReadAllBytes(offlineFilePath));
                                            DateTime s3ObjectLastModifiedUTC = s3ObjectIndexDict[offlineFilePathInsideMyS3].LastModifiedUTC;
                                            string s3ObjectFileHash = s3ObjectIndexDict[offlineFilePathInsideMyS3].FileHash;

                                            // Newer file locally
                                            if (offlineFileLastModifiedUTC > s3ObjectLastModifiedUTC && offlineFileHash != s3ObjectFileHash)
                                                lock (uploadListHashSet)
                                                    lock (downloadListHashSet)
                                                        lock (renameListDict)
                                                            lock (removeListHashSet)
                                                                if (!uploadListHashSet.Contains(offlineFilePathInsideMyS3) && // not already set to upload
                                                                    !downloadListHashSet.Contains(offlineFilePathInsideMyS3) &&  // not set to download
                                                                    !renameListDict.Values.Contains(offlineFilePathInsideMyS3) && // not set to be renamed
                                                                    !removeListHashSet.Contains(offlineFilePathInsideMyS3)) // not set to be removed
                                                                {
                                                                    verboseLogFunc(offlineFileLastModifiedUTC.ToLongDateString() + " " + offlineFileLastModifiedUTC.ToLongTimeString() + " is newer than " +
                                                                        s3ObjectLastModifiedUTC.ToLongDateString() + " " + s3ObjectLastModifiedUTC.ToLongTimeString() + ". Also " + offlineFileHash + " is not equal to " + s3ObjectFileHash);

                                                                    // Add to upload queue
                                                                    uploadListHashSet.Add(offlineFilePathInsideMyS3);

                                                                    if (verboseLogFunc != null)
                                                                        verboseLogFunc("Found updated file \"" +
                                                                            offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                                                                "\" so added it to upload queue [" + uploadListHashSet.Count + "]");
                                                                }
                                        }

                                        // New file locally
                                        else
                                            lock (uploadListHashSet)
                                                lock (downloadListHashSet)
                                                    lock (renameListDict)
                                                        lock (removeListHashSet)
                                                            if (!uploadListHashSet.Contains(offlineFilePathInsideMyS3) && // not already set to upload
                                                                !downloadListHashSet.Contains(offlineFilePathInsideMyS3) && // not set to download
                                                                !renameListDict.ContainsKey(offlineFilePathInsideMyS3) && // not set to be renamed
                                                                !removeListHashSet.Contains(offlineFilePathInsideMyS3)) // not set to be removed
                                                            {
                                                                // Add to upload queue
                                                                uploadListHashSet.Add(offlineFilePathInsideMyS3);

                                                                if (verboseLogFunc != null)
                                                                    verboseLogFunc("Found new file \"" +
                                                                        offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                                                            "\" so added it to upload queue [" + uploadListHashSet.Count + "]");
                                                            }
                                    }

                                // ---

                                timeLastCompare = DateTime.Now;
                            }
                            catch (CryptographicException)
                            {
                                Pause(true);
                                wrongEncryptionPassword = true;

                                // ---

                                string problem = "Could not decrypt encrypted S3 object key name - wrong encryption/decryption password?";

                                if (verboseLogFunc != null) verboseLogFunc(problem);
                                errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Decryption.log", problem);
                            }
                            catch (Exception ex)
                            {
                                string problem = "Problem trying to compare S3 objects and files - \"" + ex.Message + "\"";

                                if (verboseLogFunc != null) verboseLogFunc(problem);
                                errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Comparison.log", problem);
                            }

                            // ---

                            isIndexingS3Objects = false;
                            isComparingS3AndMyS3 = false;
                        }
                    }

                    Thread.Sleep(INACTIVITY_PAUSE);
                }
            }));
        }

        // ---

        private NetworkSpeedCalculator uploadSpeedCalc = new NetworkSpeedCalculator();

        public double UploadPercent;
        public double UploadSpeed; // bytes per sec
        public long UploadSize; // bytes
        public long UploadedTotalBytes; // bytes

        private void StartUploadWorker()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                while (!stop)
                {
                    int uploadCounter = 1;

                    bool haveUploads = false;
                    lock (uploadListHashSet)
                        haveUploads = uploadListHashSet.Count > 0;
                    while (!isIndexingMyS3Files && !isIndexingS3Objects && haveUploads && !pauseUploads)
                    {
                        // Make sure directories exist in case user removed some
                        ReadyWorkDirectories();

                        // ---

                        // Remove old uploads from "log"
                        while (true)
                        {
                            bool foundOldUploadLogEntry = false;
                            foreach (KeyValuePair<string, DateTime> uploadedFileAndTimeKVP in uploadedListDict)
                                if (uploadedFileAndTimeKVP.Value.AddSeconds(SECONDS_MIN_PAUSE_BETWEEN_OPERATIONS_ON_SAME_FILE) < DateTime.Now)
                                {
                                    uploadedListDict.Remove(uploadedFileAndTimeKVP.Key);
                                    foundOldUploadLogEntry = true;
                                    break;
                                }
                            if (!foundOldUploadLogEntry) break;
                        }

                        // ---

                        // Get paths and remaining uploads
                        string offlineFilePathInsideMyS3;
                        int remainingUploads = 0;
                        lock (uploadListHashSet) {
                            offlineFilePathInsideMyS3 = uploadListHashSet.First();
                            remainingUploads = uploadListHashSet.Count;
                        }
                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;

                        // Check access and existence
                        if (!File.Exists(offlineFilePath))
                        {
                            // Remove from queue
                            lock (uploadListHashSet)
                            {
                                if (verboseLogFunc != null)
                                    verboseLogFunc("File \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" removed from upload queue [" + uploadCounter + "/" + (uploadCounter + remainingUploads - 1) + "]");

                                if (uploadListHashSet.Contains(offlineFilePathInsideMyS3))
                                    uploadListHashSet.Remove(offlineFilePathInsideMyS3);

                                haveUploads = uploadListHashSet.Count > 0;
                            }

                            continue;
                        }
                        if (Tools.IsFileLocked(offlineFilePath))
                        {
                            if (verboseLogFunc != null)
                                verboseLogFunc("Waiting because file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                    "\" in upload queue [" + uploadCounter + "/" + (uploadCounter + remainingUploads - 1) + "] was locked");

                            Thread.Sleep(MILLISECONDS_EXTRA_TIME_FOR_FINISHING_WRITING_FILE_BEFORE_UPLOAD_OR_AFTER_DOWNLOAD);

                            continue;
                        }

                        // Last modified time and indexing
                        File.SetLastWriteTimeUtc(offlineFilePath, DateTime.UtcNow); // Set new last modified time = makes file look new = won't be deleted and or overwritten by MyS3 later
                        DateTime offlineFileLastModifiedUTC = File.GetLastWriteTimeUtc(offlineFilePath);
                        lock (myS3FileIndexDict)
                            if (myS3FileIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                                myS3FileIndexDict[offlineFilePathInsideMyS3] = offlineFileLastModifiedUTC;
                            else
                                myS3FileIndexDict.Add(offlineFilePathInsideMyS3, offlineFileLastModifiedUTC);

                        string encryptedUploadFilePath =
                            myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH + // complete directory path
                            Path.GetFileName(offlineFilePath) + "." + DateTime.Now.Ticks + ".ENCRYPTED"; // temp file with encrypted data for S3 upload

                        // ---

                        // Get to work
                        try
                        {
                            // 1. S3 object file data (encrypted)
                            byte[] fileData = File.ReadAllBytes(offlineFilePath);
                            File.WriteAllBytes(
                                encryptedUploadFilePath,
                                AesEncryptionWrapper.EncryptWithGCM(
                                    fileData,
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(
                                        AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                                )
                            );

                            /*
                             * Below: A decryption test for each encrypted file before upload.
                             * Will consume more memory and pause longer between each upload.
                             * Can be enabled if testing.
                             */
                            #pragma warning disable CS0162
                            if (false)
                            {
                                // Decryption test
                                byte[] decryptedUploadFileData = AesEncryptionWrapper.DecryptForGCM(
                                    File.ReadAllBytes(encryptedUploadFilePath),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                                );
                                byte[] shrinkedDecryptedUploadFileData = new byte[new FileInfo(offlineFilePath).Length];
                                Array.Copy(decryptedUploadFileData, 0, shrinkedDecryptedUploadFileData, 0, shrinkedDecryptedUploadFileData.Length);

                                // Abort work if unsuccessful test
                                if (!File.ReadAllBytes(offlineFilePath).SequenceEqual(shrinkedDecryptedUploadFileData))
                                    throw new CryptographicException(
                                        "File \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" fails to be encrypted without corruption");

                                decryptedUploadFileData = null;
                                shrinkedDecryptedUploadFileData = null;
                            }
                            #pragma warning restore CS0162

                            // 2. S3 object metadata
                            S3ObjectMetadata newS3ObjectMetadata = new S3ObjectMetadata(
                                offlineFilePathInsideMyS3,
                                Tools.DataToHash(fileData),
                                fileData.Length,
                                offlineFileLastModifiedUTC,
                                encryptionPassword);

                            // Set progress info
                            UploadPercent = 0;
                            UploadSize = new FileInfo(encryptedUploadFilePath).Length;
                            uploadSpeedCalc.Start(new FileInfo(offlineFilePath).Length);

                            if (verboseLogFunc != null)
                                verboseLogFunc("File \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                    "\" [" + Tools.GetFileSizeAsText(encryptedUploadFilePath) + "] starts uploading [" +
                                    uploadCounter + "/" + (uploadCounter + remainingUploads - 1) + "]");

                            // 3. Start upload
                            CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
                            Task uploadTask = s3.UploadAsync(encryptedUploadFilePath, newS3ObjectMetadata.ToString(),
                                offlineFilePathInsideMyS3,
                                null, cancelTokenSource.Token, OnTransferProgressHandler);
                            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
                            {
                                try
                                {
                                    uploadTask.Wait();
                                }
                                catch (Exception)
                                {
                                    string problem = "File \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" upload failed or was aborted [" + uploadCounter + "/" +
                                        (uploadCounter + uploadListHashSet.Count - 1) + "]";

                                    if (verboseLogFunc != null) verboseLogFunc(problem);
                                }
                            }));

                            // Wait and maybe abort
                            DateTime timeTransferProgressShown = DateTime.Now;
                            while (!uploadTask.IsCompleted && !uploadTask.IsCanceled)
                            {
                                // Get upload status
                                bool uploadStillPlanned = false;
                                lock (uploadListHashSet)
                                    uploadStillPlanned = uploadListHashSet.Contains(offlineFilePathInsideMyS3);

                                // Last modified
                                bool myS3FileModified = true;
                                lock (myS3FileIndexDict)
                                    if (myS3FileIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                                        myS3FileModified = !File.Exists(offlineFilePath) || offlineFileLastModifiedUTC != File.GetLastWriteTimeUtc(offlineFilePath);

                                // ---

                                if (!uploadStillPlanned || myS3FileModified || pauseUploads) // Abort if certain changes
                                {
                                    cancelTokenSource.Cancel();
                                }
                                else
                                {
                                    Thread.Sleep(25);

                                    // Manual progress estimation
                                    if (new FileInfo(encryptedUploadFilePath).Length < S3Wrapper.MIN_MULTIPART_SIZE) // <5 MB upload = no progress report
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
                                                (long)((UploadPercent / 100.0) * new FileInfo(encryptedUploadFilePath).Length),
                                                new FileInfo(encryptedUploadFilePath).Length, TransferType.UPLOAD);
                                        }
                                    }
                                }
                            }

                            // 4. Upload complete, finish work locally
                            if (uploadTask.IsCompletedSuccessfully)
                            {
                                // Add to "log" list
                                lock (uploadedListDict)
                                {
                                    if (uploadedListDict.ContainsKey(offlineFilePathInsideMyS3))
                                        uploadedListDict[offlineFilePathInsideMyS3] = DateTime.Now;
                                    else
                                        uploadedListDict.Add(offlineFilePathInsideMyS3, DateTime.Now);
                                }

                                // ---

                                // Update S3 index
                                lock (s3ObjectIndexDict)
                                {
                                    if (s3ObjectIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                                    {
                                        // Very important: Remove older S3 object with same offline file path inside MyS3
                                        try
                                        {
                                            s3.RemoveAsync(s3ObjectIndexDict[offlineFilePathInsideMyS3].ToString(), null).Wait();
                                        }
                                        catch (Exception) { }

                                        s3ObjectIndexDict[offlineFilePathInsideMyS3] = newS3ObjectMetadata;
                                    }
                                    else
                                    {
                                        s3ObjectIndexDict.Add(offlineFilePathInsideMyS3, newS3ObjectMetadata);
                                    }
                                }

                                // Set progress info
                                double newUploadSpeed = uploadSpeedCalc.Stop();
                                if (newUploadSpeed != -1) UploadSpeed = newUploadSpeed;
                                UploadPercent = 100;
                                UploadedTotalBytes += new FileInfo(encryptedUploadFilePath).Length;

                                if (verboseLogFunc != null)
                                    verboseLogFunc("File \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" uploaded [" +
                                        uploadCounter + "/" + (uploadCounter + remainingUploads - 1) + "]");

                                // ---

                                // Remove from work queue
                                lock (uploadListHashSet)
                                    if (uploadListHashSet.Contains(offlineFilePathInsideMyS3))
                                        uploadListHashSet.Remove(offlineFilePathInsideMyS3);

                                uploadCounter++;
                            }
                        }
                        catch (IOException)
                        {
                            // Exception: "The process cannot access the file '<file path>' because it is being used by another process.
                            // Basically just ignore it and let MyS3 retry later.
                        }
                        catch (Exception ex)
                        {
                            string problem = "File \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                "\" could not be uploaded [" + uploadCounter + "/" + (uploadCounter + remainingUploads - 1) +
                                "] - \"" + ex.Message + "\"";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Upload.log", problem);
                        }

                        // Clean up
                        try { if (File.Exists(encryptedUploadFilePath)) File.Delete(encryptedUploadFilePath); } catch (Exception) { }

                        // Continue with the next upload ..
                        lock (uploadListHashSet) haveUploads = uploadListHashSet.Count > 0;
                    }

                    Thread.Sleep(INACTIVITY_PAUSE);
                }
            }));
        }

        private NetworkSpeedCalculator downloadSpeedCalc = new NetworkSpeedCalculator();

        public double DownloadPercent;
        public double DownloadSpeed; // bytes per sec
        public long DownloadSize; // bytes
        public long DownloadedTotalBytes; // bytes

        private void StartDownloadWorker()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                while (!stop)
                {
                    int downloadCounter = 1;

                    bool haveDownloads = false;
                    lock (downloadListHashSet)
                        haveDownloads = downloadListHashSet.Count > 0;
                    while (!isIndexingMyS3Files && !isIndexingS3Objects && haveDownloads && !pauseDownloads)
                    {
                        // Make sure directories exist in case user removed one
                        ReadyWorkDirectories();

                        // ---

                        // Remove old downloads from "log"
                        while (true)
                        {
                            bool foundOldDownloadLogEntry = false;
                            foreach (KeyValuePair<string, DateTime> downloadedFileAndTimeKVP in downloadedListDict)
                                if (downloadedFileAndTimeKVP.Value.AddSeconds(SECONDS_MIN_PAUSE_BETWEEN_OPERATIONS_ON_SAME_FILE) < DateTime.Now)
                                {
                                    downloadedListDict.Remove(downloadedFileAndTimeKVP.Key);
                                    foundOldDownloadLogEntry = true;
                                    break;
                                }
                            if (!foundOldDownloadLogEntry) break;
                        }

                        // ---

                        // Get paths, time modified and number of remaining downloads
                        string offlineFilePathInsideMyS3 = null;
                        int remainingDownloads = 0;
                        lock (downloadListHashSet) {
                            offlineFilePathInsideMyS3 = downloadListHashSet.First();
                            remainingDownloads = downloadListHashSet.Count;
                        }
                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                        DateTime offlineFileTimeLastModifiedUTC = DateTime.MinValue;
                        lock (myS3FileIndexDict)
                            if (myS3FileIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                                offlineFileTimeLastModifiedUTC = myS3FileIndexDict[offlineFilePathInsideMyS3];
                        string encryptedDownloadFilePath =
                            myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH + // complete directory path
                            Path.GetFileName(offlineFilePath) + "." + DateTime.Now.Ticks + ".ENCRYPTED"; // temp file for downloaded encrypted S3 object data

                        // Metadata
                        S3ObjectMetadata s3ObjectMetadata = null;
                        DateTime s3ObjectlastModifiedUTC = DateTime.MinValue;
                        lock (s3ObjectIndexDict)
                            if (s3ObjectIndexDict.ContainsKey(offlineFilePathInsideMyS3)) {
                                s3ObjectMetadata = s3ObjectIndexDict[offlineFilePathInsideMyS3];
                                s3ObjectlastModifiedUTC = s3ObjectMetadata.LastModifiedUTC;
                            }

                        // Abort if changed or busy
                        if ((File.Exists(offlineFilePath) && ((offlineFileTimeLastModifiedUTC != DateTime.MinValue && offlineFileTimeLastModifiedUTC > s3ObjectlastModifiedUTC) || Tools.IsFileLocked(offlineFilePath)))
                            || s3ObjectMetadata == null)
                            lock (downloadListHashSet)
                            {
                                if (verboseLogFunc != null)
                                    verboseLogFunc("S3 object for file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" removed from download queue [" + downloadCounter + "/" + (downloadCounter + remainingDownloads - 1) + "]");

                                if (downloadListHashSet.Contains(offlineFilePathInsideMyS3))
                                    downloadListHashSet.Remove(offlineFilePathInsideMyS3);

                                haveDownloads = downloadListHashSet.Count > 0;

                                continue;
                            }

                        // Set progress info
                        DownloadPercent = 0;
                        DownloadSize = s3ObjectIndexDict[offlineFilePathInsideMyS3].DecryptedSize;
                        downloadSpeedCalc.Start(DownloadSize);

                        if (verboseLogFunc != null)
                            verboseLogFunc("S3 object for file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                "\" [" + Tools.GetByteSizeAsText(s3ObjectIndexDict[offlineFilePathInsideMyS3].DecryptedSize) + 
                                "] starts downloading [" + downloadCounter + "/" + (downloadCounter + remainingDownloads - 1) + "]");
                        // ---

                        // Get to work
                        try
                        {
                            // 1. Start download
                            CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
                            Task<MetadataCollection> downloadTask = s3.DownloadAsync(
                                encryptedDownloadFilePath,
                                s3ObjectIndexDict[offlineFilePathInsideMyS3].ToString(), null,
                                offlineFilePathInsideMyS3,
                                cancelTokenSource.Token, OnTransferProgressHandler, TransferType.DOWNLOAD);
                            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
                            {
                                try
                                {
                                    downloadTask.Wait();
                                }
                                catch (Exception)
                                {
                                    string problem = "S3 object download for file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" failed or was aborted [" + downloadCounter + "/" + (downloadCounter + remainingDownloads - 1) + "]";

                                    if (verboseLogFunc != null) verboseLogFunc(problem);
                                }
                            }));

                            // Wait and maybe abort
                            while (!downloadTask.IsCompleted && !downloadTask.IsCanceled)
                            {
                                // Get download status
                                bool downloadStillPlanned = false;
                                lock (downloadListHashSet)
                                    downloadStillPlanned = downloadListHashSet.Contains(offlineFilePathInsideMyS3);

                                // Last modified
                                lock (myS3FileIndexDict)
                                    if (myS3FileIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                                        offlineFileTimeLastModifiedUTC = myS3FileIndexDict[offlineFilePathInsideMyS3];

                                // ---

                                if (!downloadStillPlanned || pauseDownloads || // Abort if certain changes
                                   ((offlineFileTimeLastModifiedUTC != DateTime.MinValue) && offlineFileTimeLastModifiedUTC > s3ObjectlastModifiedUTC))
                                    cancelTokenSource.Cancel();
                                else
                                    Thread.Sleep(25);
                            }

                            // 2. Download complete
                            if (downloadTask.IsCompletedSuccessfully)
                            {
                                // Decrypt file and resize its array
                                byte[] decryptedFileData = AesEncryptionWrapper.DecryptForGCM(
                                    File.ReadAllBytes(encryptedDownloadFilePath),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                                );
                                long offlineFileCorrectNumberOfBytes = s3ObjectIndexDict[offlineFilePathInsideMyS3].DecryptedSize;
                                byte[] fileData = new byte[offlineFileCorrectNumberOfBytes];
                                Array.Copy(decryptedFileData, 0, fileData, 0, fileData.Length);

                                // ---

                                // Create necessary directories
                                string directories = Tools.RunningOnWindows() ?
                                    offlineFilePath.Substring(0, offlineFilePath.LastIndexOf(@"\") + 1) :
                                    offlineFilePath.Substring(0, offlineFilePath.LastIndexOf(@"/") + 1);
                                if (!Directory.Exists(directories)) Directory.CreateDirectory(directories);

                                // ---

                                // Add to "log" - in case of slow file activity handling
                                lock (downloadedListDict)
                                {
                                    if (downloadedListDict.ContainsKey(offlineFilePathInsideMyS3))
                                        downloadedListDict[offlineFilePathInsideMyS3] = DateTime.Now;
                                    else
                                        downloadedListDict.Add(offlineFilePathInsideMyS3, DateTime.Now);
                                }

                                // ---

                                // Finally create the file and attempt to set correct last modified time
                                File.WriteAllBytes(offlineFilePath, fileData);
                                Thread.Sleep(MILLISECONDS_EXTRA_TIME_FOR_FINISHING_WRITING_FILE_BEFORE_UPLOAD_OR_AFTER_DOWNLOAD); // Give file system extra time to finish
                                File.SetLastWriteTimeUtc(offlineFilePath, s3ObjectlastModifiedUTC);

                                // Update file index
                                lock (myS3FileIndexDict)
                                    if (myS3FileIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                                        myS3FileIndexDict[offlineFilePathInsideMyS3] = s3ObjectlastModifiedUTC;
                                    else
                                        myS3FileIndexDict.Add(offlineFilePathInsideMyS3, s3ObjectlastModifiedUTC);

                                // Set progress info
                                double newDownloadSpeed = downloadSpeedCalc.Stop();
                                if (newDownloadSpeed != -1) DownloadSpeed = newDownloadSpeed;
                                DownloadPercent = 100;
                                DownloadedTotalBytes += new FileInfo(encryptedDownloadFilePath).Length;

                                if (verboseLogFunc != null)
                                    verboseLogFunc("File \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" re-constructed [" +
                                        downloadCounter + "/" + (downloadCounter + remainingDownloads - 1) + "]");

                                // ---

                                // Remove from work queue
                                lock (downloadListHashSet)
                                    downloadListHashSet.Remove(offlineFilePathInsideMyS3);

                                downloadCounter++;
                            }
                        }
                        catch (CryptographicException)
                        {
                            Pause(true);
                            wrongEncryptionPassword = true;

                            // ---

                            string problem = "S3 object for file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" [" +
                                downloadCounter + "/" + (downloadCounter + remainingDownloads - 1) + "]" +
                                " failed to be decrypted - wrong encryption/decryption password?";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Decryption.log", problem);
                        }
                        catch (IOException)
                        {
                            // Exception: "The process cannot access the file '<file path>' because it is being used by another process.
                            // Basically just ignore it and let MyS3 retry later.
                        }
                        catch (Exception ex)
                        {
                            string problem = "S3 object for file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                "\" could not be downloaded [" + downloadCounter + "/" + (downloadCounter + remainingDownloads - 1) + "] - \"" + ex.Message + "\"";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Download.log", problem);
                        }

                        // Clean up
                        try { if (File.Exists(encryptedDownloadFilePath)) File.Delete(encryptedDownloadFilePath); } catch (Exception) { }

                        // Continue with the next download ..
                        lock (downloadListHashSet) haveDownloads = downloadListHashSet.Count > 0;
                    }

                    Thread.Sleep(INACTIVITY_PAUSE);
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
                    lock (renameListDict)
                        haveRenameWork = renameListDict.Count > 0;
                    while (haveRenameWork && !pauseUploads)
                    {
                        // Remove old renames from "log"
                        while (true)
                        {
                            bool foundOldRenameLogEntry = false;
                            foreach (KeyValuePair<string, DateTime> renamedFileAndTimeKVP in renamedListDict)
                                if (renamedFileAndTimeKVP.Value.AddSeconds(SECONDS_MIN_PAUSE_BETWEEN_OPERATIONS_ON_SAME_FILE) < DateTime.Now)
                                {
                                    renamedListDict.Remove(renamedFileAndTimeKVP.Key);
                                    foundOldRenameLogEntry = true;
                                    break;
                                }
                            if (!foundOldRenameLogEntry) break;
                        }

                        // ---

                        // Get paths and remaining rename operations
                        string newOfflineFilePathInsideMyS3 = null;
                        string oldOfflineFilePathInsideMyS3 = null;
                        int remainingRenames = 0;
                        lock (renameListDict)
                        {
                            newOfflineFilePathInsideMyS3 = renameListDict.First().Key;
                            oldOfflineFilePathInsideMyS3 = renameListDict.First().Value;
                            remainingRenames = renameListDict.Count;
                        }
                        string newOfflineFilePath = myS3Path + newOfflineFilePathInsideMyS3;
                        string oldOfflineFilePath = myS3Path + oldOfflineFilePathInsideMyS3;

                        // S3 object metadata
                        S3ObjectMetadata oldS3ObjectMetadata = s3ObjectIndexDict[oldOfflineFilePathInsideMyS3];
                        S3ObjectMetadata newS3ObjectMetadata = new S3ObjectMetadata(
                            newOfflineFilePathInsideMyS3,
                            oldS3ObjectMetadata.FileHash, // file contents not changed when file renamed = same hash
                            oldS3ObjectMetadata.DecryptedSize, // and also the same size
                            oldS3ObjectMetadata.LastModifiedUTC, // and also the same modified time
                            encryptionPassword
                        );

                        // ---

                        // Get to work
                        bool copySuccess = false;
                        try
                        {
                            // Copy S3 object
                            s3.CopyAsync(oldS3ObjectMetadata.ToString(), newS3ObjectMetadata.ToString(), null).Wait();
                            copySuccess = true;
                        }
                        catch (Exception ex)
                        {
                            string problem = "S3 object for file \"" +
                                newOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                    "\" could not be copied and given new name [" + renameCounter + "/" + (renameCounter + remainingRenames - 1) +
                                        "] - \"" + ex.Message + "\"";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            else errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Rename.log", problem);
                        }
                        if (copySuccess)
                        {
                            // No change locally so finish work
                            if (File.Exists(newOfflineFilePath))
                            {
                                // Remove old S3 object
                                try {
                                    s3.RemoveAsync(oldS3ObjectMetadata.ToString(), null).Wait();
                                }
                                catch (Exception ex)
                                {
                                    string problem = "Copied S3 object for renamed file \"" +
                                        oldOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                            "\" could not be removed [" + renameCounter + "/" + (renameCounter + remainingRenames - 1) + "] - \"" + ex.Message + "\"";

                                    if (verboseLogFunc != null) verboseLogFunc(problem);
                                    else errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Rename.log", problem);
                                }

                                // Update S3 object index
                                lock (s3ObjectIndexDict)
                                {
                                    // Add new object
                                    if (s3ObjectIndexDict.ContainsKey(newOfflineFilePathInsideMyS3))
                                        s3ObjectIndexDict[newOfflineFilePathInsideMyS3] = newS3ObjectMetadata;
                                    else
                                        s3ObjectIndexDict.Add(newOfflineFilePathInsideMyS3, newS3ObjectMetadata);

                                    // Remove old object
                                    if (s3ObjectIndexDict.ContainsKey(oldOfflineFilePathInsideMyS3))
                                        s3ObjectIndexDict.Remove(oldOfflineFilePathInsideMyS3);
                                }

                                // ---

                                if (verboseLogFunc != null)
                                    verboseLogFunc("S3 object for file \"" + newOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" was copied and given new name [" + renameCounter + "/" + (renameCounter + remainingRenames - 1) + "]");

                                renameCounter++;
                            }

                            // File rename reversed or file removed = clean up
                            else
                            {
                                // Remove newly created S3 object
                                Thread.Sleep(25); // Give S3 some time before request = could return NotFound exception if not
                                try {
                                    s3.RemoveAsync(newS3ObjectMetadata.ToString(), null).Wait();
                                }
                                catch (Exception ex)
                                {
                                    string problem = "New S3 object for file \"" +
                                        newOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                            "\" could not be removed [" + renameCounter + "/" + (renameCounter + remainingRenames - 1) + "] - \"" + ex.Message + "\"";

                                    if (verboseLogFunc != null) verboseLogFunc(problem);
                                    else errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Rename.log", problem);
                                }

                                // File removed
                                if (!File.Exists(oldOfflineFilePath))
                                {
                                    bool fileStillIndexed = false;
                                    lock (myS3FileIndexDict)
                                        fileStillIndexed = myS3FileIndexDict.ContainsKey(oldOfflineFilePathInsideMyS3);

                                    // File doesn't exist but is still indexed = file removed _after_ local renaming but _before_ S3 object was copied
                                    // The file removal handling is unable to solve this because the S3 object that needs removal is unknown
                                    // The old offline path has to be used to trigger removal
                                    if (fileStillIndexed)
                                        OnRemoveFileOrDirectoryHandler(oldOfflineFilePath, false);
                                }

                                if (verboseLogFunc != null)
                                    verboseLogFunc("Copying and giving new name to S3 object for file \"" + newOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") 
                                        + "\" failed or was aborted [" + renameCounter + "/" + (renameCounter + remainingRenames - 1) + "]");
                            }
                        }

                        // ---

                        // Add to "log" - in case of slow file activity handling
                        lock (renamedListDict)
                        {
                            if (renamedListDict.ContainsKey(newOfflineFilePath))
                                renamedListDict[newOfflineFilePath] = DateTime.Now;
                            else
                                renamedListDict.Add(newOfflineFilePath, DateTime.Now);
                        }

                        // Remove from work queue = only one attempt
                        lock (renameListDict)
                            if (renameListDict.ContainsKey(newOfflineFilePathInsideMyS3))
                                renameListDict.Remove(newOfflineFilePathInsideMyS3);

                        lock (renameListDict) haveRenameWork = renameListDict.Count > 0;
                    }

                    // Continue with the next rename operation ..
                    Thread.Sleep(INACTIVITY_PAUSE);
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
                    lock (removeListHashSet)
                        haveRemoveWork = removeListHashSet.Count > 0;
                    while (haveRemoveWork && !pauseUploads)
                    {
                        // Remove old removes from "log"
                        while (true)
                        {
                            bool foundOldRemoveLogEntry = false;
                            foreach (KeyValuePair<string, DateTime> removedFileAndTimeKVP in removedListDict)
                                if (removedFileAndTimeKVP.Value.AddSeconds(SECONDS_MIN_PAUSE_BETWEEN_OPERATIONS_ON_SAME_FILE) < DateTime.Now)
                                {
                                    removedListDict.Remove(removedFileAndTimeKVP.Key);
                                    foundOldRemoveLogEntry = true;
                                    break;
                                }
                            if (!foundOldRemoveLogEntry) break;
                        }

                        // ---

                        // Get paths and remaining remove operations
                        string offlineFilePathInsideMyS3 = null;
                        int remainingRemoves = 0;
                        lock (removeListHashSet) {
                            offlineFilePathInsideMyS3 = removeListHashSet.First();
                            remainingRemoves = removeListHashSet.Count;
                        }
                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;

                        // Get to work
                        try
                        {
                            s3.RemoveAsync(s3ObjectIndexDict[offlineFilePathInsideMyS3].ToString(), null).Wait();

                            // Remove S3 object from index
                            lock(s3ObjectIndexDict)
                                if (s3ObjectIndexDict.ContainsKey(offlineFilePathInsideMyS3))
                                    s3ObjectIndexDict.Remove(offlineFilePathInsideMyS3);

                            // ---

                            if (verboseLogFunc != null)
                                verboseLogFunc("S3 object for file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" removed [" +
                                        removeCounter + "/" + (removeCounter + remainingRemoves - 1) + "]");

                            // Add to "log" - in case of slow file activity handling
                            lock (removedListDict)
                            {
                                if (removedListDict.ContainsKey(offlineFilePath))
                                    removedListDict[offlineFilePath] = DateTime.Now;
                                else
                                    removedListDict.Add(offlineFilePath, DateTime.Now);
                            }

                            removeCounter++;
                        }
                        catch (Exception ex)
                        {
                            string problem = "S3 object for file \"" + offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                "\" could not be removed [" + removeCounter + "/" + (removeCounter + remainingRemoves - 1) + "] - \"" + ex.Message + "\"";

                            if (verboseLogFunc != null) verboseLogFunc(problem);
                            else errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Remove.log", problem);
                        }

                        // ---

                        // Remove from work queue = only one try
                        lock (removeListHashSet)
                            if (removeListHashSet.Contains(offlineFilePathInsideMyS3))
                                removeListHashSet.Remove(offlineFilePathInsideMyS3);

                        // Continue with the next removal ..
                        lock (removeListHashSet) haveRemoveWork = removeListHashSet.Count > 0;
                    }

                    Thread.Sleep(INACTIVITY_PAUSE);
                }
            }));
        }

        // ---

        private NetworkSpeedCalculator restoreSpeedCalc = new NetworkSpeedCalculator();

        public double RestoreDownloadPercent;
        public double RestoreDownloadSpeed; // bytes per sec
        public long RestoreDownloadSize; // bytes
        public long RestoreDownloadedTotalBytes; // bytes

        public void RestoreFiles(DateTime earliestLastModifiedUTC, bool onlyRestoreLastRemoved)
        {
            // Abort if paused or busy
            lock(restoreDownloadListDict)
                if (pauseRestores || restoreDownloadListDict.Count > 0) return;

            // ---

            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) => {

                // Get info about ALL S3 object versions
                List<S3ObjectVersion> s3ObjectVersionList = s3.GetCompleteObjectVersionList(null);
                Dictionary<string, HashSet<S3ObjectVersion>> allS3ObjectInfoDict = // MyS3 file path ==> list of S3 object versions
                    new Dictionary<string, HashSet<S3ObjectVersion>>();

                // Group together S3 object versions for each MyS3 file path
                foreach (S3ObjectVersion s3ObjectVersion in s3ObjectVersionList)
                {
                    // S3 object metadata
                    S3ObjectMetadata s3ObjectMetadata = new S3ObjectMetadata(s3ObjectVersion.Key, encryptionPassword);

                    // Add to list
                    if (allS3ObjectInfoDict.ContainsKey(s3ObjectMetadata.OfflineFilePathInsideMyS3))
                        allS3ObjectInfoDict[s3ObjectMetadata.OfflineFilePathInsideMyS3].Add(s3ObjectVersion);
                    else
                        allS3ObjectInfoDict.Add(
                            s3ObjectMetadata.OfflineFilePathInsideMyS3,
                            new HashSet<S3ObjectVersion>(){ s3ObjectVersion }
                        );
                }

                // ---

                // Restore last removed MyS3 objects
                if (onlyRestoreLastRemoved) // = Find fresh delete markers and remove them
                {
                    int restoreCounter = 0;

                    // Go through each S3 object collection of metadata and versions
                    foreach (KeyValuePair<string, HashSet<S3ObjectVersion>> s3ObjectInfoKVP in allS3ObjectInfoDict)
                    {
                        // S3 object version
                        string offlineFilePathInsideMyS3 = s3ObjectInfoKVP.Key;
                        HashSet<S3ObjectVersion> s3ObjectVersionHashSet = s3ObjectInfoKVP.Value;

                        // Latest removed S3 object version delete marker
                        S3ObjectVersion latestS3ObjectVersionDeleteMarker = null;

                        // 1. Go through collections of versions
                        foreach (S3ObjectVersion s3ObjectVersion in s3ObjectVersionHashSet)
                        {                                                                                 
                            // Delete marker inside given time period
                            if (s3ObjectVersion.IsDeleteMarker && s3ObjectVersion.LastModified >= earliestLastModifiedUTC)
                            {
                                // Found (newer) delete marker
                                if (latestS3ObjectVersionDeleteMarker == null || s3ObjectVersion.LastModified > latestS3ObjectVersionDeleteMarker.LastModified)
                                    latestS3ObjectVersionDeleteMarker = s3ObjectVersion;
                            }
                        }

                        // 2. Remove S3 object version delete marker
                        if (latestS3ObjectVersionDeleteMarker != null)
                        {
                            if (verboseLogFunc != null)
                                verboseLogFunc("Restoring S3 object for \"" +
                                    offlineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") + "\" [" + restoreCounter + "]");

                            // Remove S3 object version delete marker
                            s3.RemoveAsync(latestS3ObjectVersionDeleteMarker.Key, latestS3ObjectVersionDeleteMarker.VersionId).Wait();

                            restoreCounter++;
                        }
                    }

                    // Trigger download of restored S3 objects
                    if (restoreCounter > 0)
                        TriggerS3AndMyS3Comparison();
                }

                // Restore every earlier S3 object version and place in restore folder
                else
                {
                    // Go through each S3 object collection of metadata and versions
                    foreach (KeyValuePair<string, HashSet<S3ObjectVersion>> s3ObjectInfoKVP in allS3ObjectInfoDict)
                    {
                        // S3 object version
                        string offlineFilePathInsideMyS3 = s3ObjectInfoKVP.Key;
                        HashSet<S3ObjectVersion> s3ObjectVersionHashSet = s3ObjectInfoKVP.Value;

                        // 1. Go through collections of versions
                        foreach (S3ObjectVersion s3ObjectVersion in s3ObjectVersionHashSet)
                        {
                            // Earlier S3 object version and inside given time period
                            if (!s3ObjectVersion.IsDeleteMarker && !s3ObjectVersion.IsLatest &&
                                 s3ObjectVersion.LastModified >= earliestLastModifiedUTC)
                            {
                                // Add earlier S3 object to list
                                if (restoreDownloadListDict.ContainsKey(offlineFilePathInsideMyS3))
                                    restoreDownloadListDict[offlineFilePathInsideMyS3].Add(s3ObjectVersion);
                                else
                                    restoreDownloadListDict.Add(offlineFilePathInsideMyS3, new HashSet<S3ObjectVersion>() { s3ObjectVersion });
                            }
                        }

                        // Order by time changed
                        if (restoreDownloadListDict.ContainsKey(offlineFilePathInsideMyS3))
                            restoreDownloadListDict[offlineFilePathInsideMyS3] =
                                restoreDownloadListDict[offlineFilePathInsideMyS3].OrderBy(x => x.LastModified).ToHashSet(); // oldest first
                    }

                    // 2. Now do the restoring of earlier file versions
                    if (restoreDownloadListDict.Count > 0)
                    {
                        if (verboseLogFunc != null)
                            lock (restoreDownloadListDict)
                                verboseLogFunc("Restoring earlier S3 object versions for " + 
                                    restoreDownloadListDict.Count + " " + (restoreDownloadListDict.Count == 1 ? "file" : "files"));

                        RestoreFileVersions();
                    }
                }
            }));
        }

        private void RestoreFileVersions()
        {
            int restoreCounter = 1;

            while (restoreDownloadListDict.Count > 0)
            {
                while (!pauseRestores && restoreDownloadListDict.Count > 0)
                {
                    // Get paths
                    string originalOfflineFilePathInsideMyS3 = null;
                    lock (restoreDownloadListDict)
                        originalOfflineFilePathInsideMyS3 = restoreDownloadListDict.First().Key;
                    string originalOfflineFilePath = myS3Path + originalOfflineFilePathInsideMyS3;

                    // S3 object version
                    HashSet<S3ObjectVersion> s3ObjectVersionHashSet = restoreDownloadListDict[originalOfflineFilePathInsideMyS3];

                    // Restore each version
                    int versionCounter = 1;
                    foreach (S3ObjectVersion s3ObjectVersion in s3ObjectVersionHashSet)
                    {
                        // S3 object metadata
                        S3ObjectMetadata s3ObjectMetadata = new S3ObjectMetadata(s3ObjectVersion.Key, encryptionPassword);

                        // Get paths
                        string versionFilename = Path.GetFileNameWithoutExtension(originalOfflineFilePath) + 
                            " [" + versionCounter + "]" + Path.GetExtension(originalOfflineFilePath);
                        string offlineFilePathInsideMyS3 =
                            RELATIVE_LOCAL_MYS3_RESTORE_DIRECTORY_PATH + 
                            originalOfflineFilePathInsideMyS3.Substring(0, originalOfflineFilePathInsideMyS3.LastIndexOf(Path.GetFileName(originalOfflineFilePath))) +
                            versionFilename;
                        string offlineFilePath = myS3Path + offlineFilePathInsideMyS3;
                        string encryptedDownloadFilePath =
                            myS3Path + RELATIVE_LOCAL_MYS3_WORK_DIRECTORY_PATH + // complete directory path
                            Path.GetFileName(offlineFilePath) + "." + DateTime.Now.Ticks + ".ENCRYPTED"; // temp file for downloaded encrypted S3 object data

                        // Get to work
                        try
                        {
                            // Set progress info
                            RestoreDownloadPercent = 0;
                            RestoreDownloadSize = s3ObjectMetadata.DecryptedSize;
                            restoreSpeedCalc.Start(RestoreDownloadSize);

                            if (verboseLogFunc != null)
                                lock (restoreDownloadListDict)
                                    verboseLogFunc("S3 object for restoring \"" + originalOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" [" + versionCounter + "] starts downloading [" + restoreCounter + "/" +
                                        (restoreCounter + restoreDownloadListDict.Count / 2 - 1) + "][" + Tools.GetByteSizeAsText(s3ObjectMetadata.DecryptedSize) + "]");

                            // Start restore
                            CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
                            Task<MetadataCollection> restoreDownloadTask = s3.DownloadAsync(encryptedDownloadFilePath,
                                s3ObjectMetadata.ToString(), s3ObjectVersion.VersionId, originalOfflineFilePathInsideMyS3,
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
                                        verboseLogFunc("S3 object download for restoring \"" + originalOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                            "\" [" + versionCounter + "] failed or was aborted [" + restoreCounter + "/" + (restoreCounter + restoreDownloadListDict.Count - 1) + "]");
                                }
                            }));

                            // Wait and maybe abort
                            while (!restoreDownloadTask.IsCompleted && !restoreDownloadTask.IsCanceled)
                                if (pauseRestores)
                                    cancelTokenSource.Cancel();
                                else
                                    Thread.Sleep(25);

                            // Restore complete
                            if (restoreDownloadTask.IsCompletedSuccessfully)
                            {
                                // Set progress info
                                double newRestoreDownloadSpeed = restoreSpeedCalc.Stop();
                                if (newRestoreDownloadSpeed != -1) RestoreDownloadSpeed = newRestoreDownloadSpeed;
                                RestoreDownloadPercent = 100;
                                RestoreDownloadedTotalBytes += new FileInfo(encryptedDownloadFilePath).Length;

                                // ---

                                // Decrypt file and resize array
                                byte[] decryptedFileData = AesEncryptionWrapper.DecryptForGCM(
                                    File.ReadAllBytes(encryptedDownloadFilePath),
                                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, encryptionPassword)
                                );
                                long offlineFileCorrectNumberOfBytes = s3ObjectMetadata.DecryptedSize;
                                byte[] fileData = new byte[offlineFileCorrectNumberOfBytes];
                                Array.Copy(decryptedFileData, 0, fileData, 0, fileData.Length);

                                // Create necessary directories
                                string directories = offlineFilePath.Substring(0, offlineFilePath.LastIndexOf(@"\") + 1);
                                if (!Directory.Exists(directories)) Directory.CreateDirectory(directories);

                                // Finish file work
                                File.WriteAllBytes(offlineFilePath, fileData);
                                Thread.Sleep(MILLISECONDS_EXTRA_TIME_FOR_FINISHING_WRITING_FILE_BEFORE_UPLOAD_OR_AFTER_DOWNLOAD);
                                File.SetLastWriteTimeUtc(offlineFilePath, s3ObjectMetadata.LastModifiedUTC);

                                // ---

                                if (verboseLogFunc != null)
                                    verboseLogFunc("File \"" + originalOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" [" + versionCounter + "] restored [" + restoreCounter + "/" + (restoreCounter + restoreDownloadListDict.Count - 1) + "]");

                                versionCounter++;
                                restoreCounter++;

                                // Remove from work queue
                                lock (restoreDownloadListDict)
                                    restoreDownloadListDict.Remove(originalOfflineFilePathInsideMyS3);
                            }
                        }
                        catch (CryptographicException)
                        {
                            lock (restoreDownloadListDict)
                            {
                                string problem = "S3 object for restoring file \"" + originalOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" [" + versionCounter + "] failed to be decrypted [" + restoreCounter + "/" + (restoreCounter + restoreDownloadListDict.Count - 1) + "]" +
                                    " - wrong encryption/decryption password?";

                                if (verboseLogFunc != null) verboseLogFunc(problem);
                                errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Decryption.log", problem);
                            }
                        }
                        catch (Exception ex) // Happens when trying to download deleted files
                        {
                            lock (restoreDownloadListDict)
                            {
                                string problem = "S3 object for restoring file \"" + originalOfflineFilePathInsideMyS3.Replace(@"\", @" \ ").Replace(@"/", @" / ") +
                                        "\" [" + versionCounter + "] not downloaded [" + restoreCounter + "/" + (restoreCounter + restoreDownloadListDict.Count - 1) + "]" +
                                    " - \"" + ex.Message + "\"";

                                if (verboseLogFunc != null) verboseLogFunc(problem);
                                errorLogFunc(myS3Path + RELATIVE_LOCAL_MYS3_LOG_DIRECTORY_PATH + "Restore.log", problem);
                            }
                        }

                        // Clean up
                        try { if (File.Exists(encryptedDownloadFilePath)) File.Delete(encryptedDownloadFilePath); } catch (Exception) { }
                    }
                }

                Thread.Sleep(INACTIVITY_PAUSE);
            }
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