using System;
using System.IO;

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

        private Action<string> changeFunc;
        private Action<string, string, bool> renameFunc;
        private Action<string, bool> removeFunc;

        public FileMonitor(string rootPath,
            string[] ignoredDirectoriesNames, string[] ignoredFileExtensions,
            Action<string> changeFunc,
            Action<string, string, bool> renameFunc,
            Action<string, bool> removeFunc)
        {
            this.rootPath = rootPath;

            this.ignoredDirectoriesNames = ignoredDirectoriesNames;
            this.ignoredFileExtensions = ignoredFileExtensions;

            this.changeFunc = changeFunc;
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
                fileWatcher.InternalBufferSize = 1024 * 64; // 64 KB

                fileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;

                fileWatcher.Created += OnChange;
                fileWatcher.Changed += OnChange;
                fileWatcher.Renamed += OnRename;
                fileWatcher.Deleted += OnDeleteFile;

                fileWatcher.IncludeSubdirectories = true;
            }
            fileWatcher.EnableRaisingEvents = true;

            if (directoryWatcher == null)
            {
                directoryWatcher = new FileSystemWatcher();

                directoryWatcher.Path = rootPath;
                directoryWatcher.InternalBufferSize = 1024 * 64; // 64 KB

                directoryWatcher.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.LastWrite;

                directoryWatcher.Renamed += OnRename;
                directoryWatcher.Deleted += OnDeleteDirectory;

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

        private void OnChange(object source, FileSystemEventArgs eventArgs)
        {
            string path = eventArgs.FullPath;
            if (Directory.Exists(path) || !File.Exists(path)) return;

            // Ignore directories
            foreach (string directory in ignoredDirectoriesNames)
                if (path.StartsWith(rootPath + directory)) return;

            // Ignore file extensions
            foreach (string fileExtension in ignoredFileExtensions)
                if (Path.GetExtension(path).ToLower() == fileExtension) return;

            // ---

            // Handle change
            changeFunc(path);
        }

        private void OnRename(object source, RenamedEventArgs eventArgs)
        {
            OnRename(eventArgs.FullPath, eventArgs.OldFullPath);
        }

        private void OnRename(string newPath, string oldPath)
        {
            bool isDirectory = Directory.Exists(newPath);

            // Ignore directories
            foreach (string directory in ignoredDirectoriesNames)
                if (newPath.StartsWith(rootPath + directory)) return;

            // Ignore file extensions
            if (!isDirectory)
                foreach (string fileExtension in ignoredFileExtensions)
                    if (Path.GetExtension(newPath).ToLower() == fileExtension) return;

            // ---

            // Handle rename
            renameFunc(newPath, oldPath, isDirectory);
        }

        private void OnDeleteFile(object source, FileSystemEventArgs eventArgs)
        {
            string path = eventArgs.FullPath;

            // Ignore directories
            foreach (string directory in ignoredDirectoriesNames)
                if (path.StartsWith(rootPath + directory))
                    return;

            // Ignore file extensions
            foreach (string fileExtension in ignoredFileExtensions)
                if (Path.GetExtension(path).ToLower() == fileExtension) return;

            // ---

            // Handle removal
            removeFunc(path, false);
        }
        private void OnDeleteDirectory(object source, FileSystemEventArgs eventArgs)
        {
            string path = eventArgs.FullPath;

            // Ignore directories
            foreach (string directory in ignoredDirectoriesNames)
                if (path.StartsWith(rootPath + directory))
                    return;

            // ---

            // Handle removal
            removeFunc(path, true);
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