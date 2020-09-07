using System;
using System.IO;
using System.Text;

namespace MyS3
{
    public class FileMonitor : IDisposable
    {
        private FileSystemWatcher fileWatcher;
        private FileSystemWatcher directoryWatcher;

        // ---

        private readonly string rootPath;
        private readonly string[] ignoredDirectoriesNames;
        private readonly string[] ignoredFileExtensions;

        private Action<string> uploadFunc;
        private Action<string, string> renameFunc;
        private Action<string, bool> removeFunc;

        public FileMonitor(string rootPath,
            string[] ignoredDirectoriesNames, string[] ignoredFileExtensions,
            Action<string> uploadFunc,
            Action<string, string> renameFunc,
            Action<string, bool> removeFunc)
        {
            this.rootPath = rootPath;

            this.ignoredDirectoriesNames = ignoredDirectoriesNames;
            this.ignoredFileExtensions = ignoredFileExtensions;

            this.uploadFunc = uploadFunc;
            this.renameFunc = renameFunc;
            this.removeFunc = removeFunc;
        }

        // ---

        public void Start()
        {
            if (fileWatcher == null)
            {
                fileWatcher = new FileSystemWatcher();

                fileWatcher.Path = rootPath;

                fileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;

                fileWatcher.Created += OnChange;
                fileWatcher.Renamed += OnRename;
                fileWatcher.Changed += OnChange;
                fileWatcher.Deleted += OnRemoveFile;

                fileWatcher.IncludeSubdirectories = true;
            }
            fileWatcher.EnableRaisingEvents = true;

            if (directoryWatcher == null)
            {
                directoryWatcher = new FileSystemWatcher();

                directoryWatcher.Path = rootPath;

                directoryWatcher.NotifyFilter = NotifyFilters.DirectoryName;

                directoryWatcher.Renamed += OnRename;
                directoryWatcher.Deleted += OnRemoveDirectory;

                directoryWatcher.IncludeSubdirectories = true;
            }
            directoryWatcher.EnableRaisingEvents = true;
        }

        public void Stop()
        {
            fileWatcher.EnableRaisingEvents = false;
            directoryWatcher.EnableRaisingEvents = false;
        }

        // ---

        public void OnChange(object source, FileSystemEventArgs eventArgs) // = upload
        {
            string offlinePath = eventArgs.FullPath;

            // Ignore directories
            if (Directory.Exists(offlinePath)) return;
            else
                foreach (string directory in ignoredDirectoriesNames)
                    if (offlinePath.StartsWith(rootPath + directory)) return;

            // Ignore file extensions
            foreach (string fileExtension in ignoredFileExtensions)
                if (Path.GetExtension(offlinePath) == fileExtension) return;

            // Execute
            uploadFunc(offlinePath);
        }

        private void OnRename(object source, RenamedEventArgs eventArgs)
        {
            OnRename(eventArgs.OldFullPath, eventArgs.FullPath);
        }
        private void OnRename(string oldOfflinePath, string newOfflinePath)
        {
            // Ignore directories
            foreach (string directory in ignoredDirectoriesNames)
                if (newOfflinePath.StartsWith(rootPath + directory)) return;

            // Ignore file extensions
            foreach (string fileExtension in ignoredFileExtensions)
                if (Path.GetExtension(newOfflinePath) == fileExtension) return;

            // Directory rename
            if (Directory.Exists(newOfflinePath))
            {
                foreach (string filePath in Directory.GetFiles(newOfflinePath))
                    OnRename(filePath.Replace(newOfflinePath, oldOfflinePath), filePath);

                return;
            }
            // File rename
            renameFunc(oldOfflinePath, newOfflinePath);
        }

        private void OnRemoveFile(object source, FileSystemEventArgs eventArgs)
        {
            string offlineFilePath = eventArgs.FullPath;

            // Ignore directories
            foreach (string directory in ignoredDirectoriesNames)
                if (offlineFilePath.StartsWith(rootPath + directory))
                    return;

            // Ignore file extensions
            foreach (string fileExtension in ignoredFileExtensions)
                if (Path.GetExtension(offlineFilePath) == fileExtension) return;

            // Handle removal
            removeFunc(offlineFilePath, false);
        }
        private void OnRemoveDirectory(object source, FileSystemEventArgs eventArgs)
        {
            string offlinePath = eventArgs.FullPath;

            // Ignore directories
            foreach (string directory in ignoredDirectoriesNames)
                if (offlinePath.StartsWith(rootPath + directory))
                    return;

            // Handle removal
            removeFunc(offlinePath, true);
        }

        // ---

        private bool disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                Stop();

                fileWatcher.Dispose();
                directoryWatcher.Dispose();

                disposed = true;
            }
        }
    }
}