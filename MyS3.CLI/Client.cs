using System;
using System.Linq;
using System.Globalization;
using System.Threading;

using Amazon;
using Amazon.S3.Model;
using System.Collections.Generic;

namespace MyS3.CLI
{
    public class Client
    {
        static void Main(string[] args)
        {
            Console.Title = "MyS3";

            // Get settings
            bool version = false;
            bool verbose = false;
            bool runTests = false;
            string bucket = null;
            string region = null;
            string awsAccessKeyID = null;
            string awsSecretAccessKey = null;
            string encryptionPassword = null;
            string myS3Path = null;
            bool sharedBucket = false;
            bool pauseDownloads = false;
            bool pauseUploads = false;
            string timeRestoreRemovedFiles = null;
            string timeRestoreFileVersions = null;
            bool emptyBucket = false;
            bool printUse = false;
            foreach (string arg in args)
            {
                if (arg == "--version")
                {
                    version = true;
                }
                else if (arg == "--verbose")
                {
                    verbose = true;
                }
                else if (arg == "--run-tests")
                {
                    runTests = true;
                }
                else if (arg.StartsWith("--bucket="))
                {
                    bucket = arg.Replace("--bucket=", "");
                }
                else if (arg.StartsWith("--region="))
                {
                    region = arg.Replace("--region=", "");
                }
                else if (arg.StartsWith("--aws-access-key="))
                {
                    string[] awsKeyIDAndValue = arg.Replace("--aws-access-key=", "").Split(new char[] { ',' }, StringSplitOptions.None);
                    awsAccessKeyID = awsKeyIDAndValue[0];
                    awsSecretAccessKey = awsKeyIDAndValue[1];
                }
                else if (arg.StartsWith("--encryption-password="))
                {
                    encryptionPassword = arg.Replace("--encryption-password=", "");
                }
                else if (arg.StartsWith("--mys3-folder="))
                {
                    myS3Path = arg.Replace("--mys3-folder=", "") + (Tools.RunningOnWindows() ? @"\" : @"/");
                    myS3Path = myS3Path.Replace(@"\\", @"\").Replace(@"//", @"/");
                }
                else if (arg == "--shared-bucket")
                {
                    sharedBucket = true;
                }
                else if (arg == "--pause-downloads")
                {
                    pauseDownloads = true;
                }
                else if (arg == "--pause-uploads")
                {
                    pauseUploads = true;
                }
                else if (arg.StartsWith("--restore-removed-files="))
                {
                    timeRestoreRemovedFiles = arg.Replace("--restore-removed-files=", "");
                }
                else if (arg.StartsWith("--restore-file-versions="))
                {
                    timeRestoreFileVersions = arg.Replace("--restore-file-versions=", "");
                }
                else if (arg == "--empty-bucket")
                {
                    emptyBucket = true;
                }
                else
                {
                    Console.WriteLine("Unknown startup argument: " + arg);
                    printUse = true;
                }
            }
            if (version)
            {
                Console.WriteLine("MyS3.CLI version " + typeof(Client).Assembly.GetName().Version);
                Console.WriteLine("MyS3 version " + typeof(MyS3Runner).Assembly.GetName().Version);

                return;
            }
            else if (bucket == null || region == null || awsAccessKeyID == null || awsSecretAccessKey == null)
            {
                printUse = true;
            }

            // ---

            // Empty bucket
            if (emptyBucket)
            {
                Console.WriteLine("You have selected to EMPTY your entire S3 bucket \"" + bucket + "\"");
                Console.Write("Type 'y' to continue or something else to abort: ");

                if (Console.ReadKey().KeyChar == 'y')
                {
                    Console.WriteLine("");

                    // Setup S3 interface
                    S3Wrapper s3 = new S3Wrapper(
                        bucket, RegionEndpoint.GetBySystemName(region),
                        awsAccessKeyID, awsSecretAccessKey);

                    // Get object versions
                    Console.WriteLine("Retrieving S3 object versions - please wait");
                    List<S3ObjectVersion> s3ObjectVersionsList = s3.GetCompleteObjectVersionList(null);

                    // Start removal
                    if (s3ObjectVersionsList.Count == 0)
                    {
                        Console.WriteLine("No S3 object versions to remove - bucket already empty");
                    }
                    else
                    {
                        long counter = 0;
                        foreach (S3ObjectVersion s3ObjectVersion in s3ObjectVersionsList)
                        {
                            s3.RemoveAsync(s3ObjectVersion.Key, s3ObjectVersion.VersionId).Wait();
                            counter++;
                            Console.WriteLine("Removed version \"" + s3ObjectVersion.VersionId + "\" of S3 object \"" +
                                s3ObjectVersion.Key + "\" " + "[" + counter + " / " + s3ObjectVersionsList.Count + "]");
                        }

                        Console.WriteLine("Your S3 bucket \"" + bucket + "\" is now empty");
                    }
                }
                else
                {
                    Console.WriteLine("");
                    Console.WriteLine("Aborted emptying bucket \"" + bucket + "\"");
                }

                return;
            }

            // ---

            if (encryptionPassword == null)
            {
                printUse = true;
            }
            else
            {
                // Test settings
                if (runTests)
                {
                    Tools.Log("Running tests to check certain settings");

                    // Test encryption password
                    if (encryptionPassword.Length >= 16 ||
                            (encryptionPassword.Length >= 8 &&
                             encryptionPassword.Any(char.IsUpper) && encryptionPassword.Any(char.IsLower) &&
                             encryptionPassword.Any(char.IsNumber)))
                    {
                        // OK encryption password
                    }
                    else if (encryptionPassword.Length >= 8 ||
                        (encryptionPassword.Length >= 6 &&
                        (encryptionPassword.Any(char.IsUpper) || encryptionPassword.Any(char.IsLower)) &&
                        encryptionPassword.Any(char.IsNumber)))
                    {
                        Tools.Log("Your encryption password is not very strong");
                    }
                    else if (encryptionPassword.Length < 8)
                    {
                        Tools.Log("Aborting - please choose a strong encryption password");
                        printUse = true;
                    }

                    // Run AWS tests
                    if (!printUse)
                    {
                        S3Wrapper s3 = new S3Wrapper(
                            bucket, RegionEndpoint.GetBySystemName(region),
                            awsAccessKeyID, awsSecretAccessKey);
                        ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
                        {
                            s3.RunTests();
                        }));
                        while (!s3.TestsRun) Thread.Sleep(10);
                        if (s3.TestsResult == null)
                        {
                            Tools.Log("Every test succeeded so running MyS3 is proceeding");
                        }
                        else
                        {
                            Tools.Log("Encountered a problem with your AWS settings - \"" + s3.TestsResult + "\"");
                            return;
                        }
                    }
                }

                // ---

                // Setup
                MyS3Runner myS3Runner = new MyS3Runner(
                    bucket, region,
                    awsAccessKeyID, awsSecretAccessKey,
                    myS3Path,
                    encryptionPassword,
                    sharedBucket,
                    null, Tools.Log);
                if (verbose) myS3Runner.VerboseLogFunc = Tools.Log;
                myS3Runner.Setup();

                // Start running
                myS3Runner.Start();
                Tools.Log("Press 'p' to pause or continue MyS3's downloads, uploads and restores");
                Tools.Log("Press 'q' at any time to quit MyS3 gracefully and then wait for work to finish");
                Tools.Log("...............................................................................");

                // Pause MyS3 right now or when missing network connection
                if (pauseDownloads) myS3Runner.PauseDownloadsAndRestores(true);
                if (pauseUploads) myS3Runner.PauseUploads(true);
                ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
                {
                    bool hasInternet = true;

                    while (!myS3Runner.Stopping)
                    {
                        bool hasInternetNew = Tools.HasInternet();

                        // Internet access changed
                        if (hasInternetNew != hasInternet)
                        {
                            hasInternet = hasInternetNew;
                            myS3Runner.Pause(!hasInternet);
                        }

                        // Pause until next check
                        Thread.Sleep(10 * 1000);
                    }
                }));

                // Start by restoring files
                if (timeRestoreRemovedFiles != null)
                    myS3Runner.RestoreFiles(
                        DateTime.ParseExact(timeRestoreRemovedFiles, "yyyy-MM-dd-HH", CultureInfo.InvariantCulture),
                        true);
                if (timeRestoreFileVersions != null)
                    myS3Runner.RestoreFiles(
                        DateTime.ParseExact(timeRestoreFileVersions, "yyyy-MM-dd-HH", CultureInfo.InvariantCulture),
                        false);

                // ---

                // Standby
                while (!myS3Runner.Stopping)
                {
                    switch (Console.ReadKey().KeyChar)
                    {
                        case 'p':
                            Console.Write("\b");
                            myS3Runner.Pause(!myS3Runner.UploadsPaused); // Pause or continue uploads, downloads and restores
                            break;

                        case 'q':
                            Console.Write("\b");
                            Tools.Log("...............................................................................");
                            Tools.Log("Received exit signal so MyS3 is stopping");
                            myS3Runner.Stop();
                            break;

                        default:
                            Console.Write("\b");
                            break;
                    }
                }
            }

            // ---

            if (printUse)
            {
                Console.WriteLine("MyS3.CLI has 2 different run configurations:");
                Console.WriteLine("(1) Encrypt and sync files:");
                Console.WriteLine("  --verbose");
                Console.WriteLine("  --run-tests");
                Console.WriteLine("  --bucket=<name of your bucket>*");
                Console.WriteLine("  --region=<name of bucket region>*");
                Console.WriteLine("  --aws-access-key=<your bucket user's key id>,<your bucket user's secret access key>*");
                Console.WriteLine("  --encryption-password=<your own private file encryption password>*");
                Console.WriteLine("  --mys3-folder=<absolute path to preferred mys3 folder>");
                Console.WriteLine("  --shared-bucket");
                Console.WriteLine("  --pause-downloads");
                Console.WriteLine("  --pause-uploads");
                Console.WriteLine("  --restore-removed-files=<year-month-day-hour>");
                Console.WriteLine("  --restore-file-versions=<year-month-day-hour>");
                Console.WriteLine("(2) Empty entire S3 bucket:");
                Console.WriteLine("  --bucket=<name of your bucket>*");
                Console.WriteLine("  --region=<name of bucket region>*");
                Console.WriteLine("  --aws-access-key=<your bucket user's key id>,<your bucket user's secret access key>*");
                Console.WriteLine("  --empty-bucket     (CAREFUL! This setting can empty your bucket in a split second!");
                Console.WriteLine("");
                Console.WriteLine("To check MyS3 version:");
                Console.WriteLine("  --version");
                Console.WriteLine();
                Console.WriteLine("(All fields marked * are mandatory because MyS3 will not work otherwise)");
                Console.WriteLine();
                Console.WriteLine("Use '--run-tests' to check your settings if this is your first run!");
                Console.WriteLine();
                Console.WriteLine("If S3 bucket is used by multiple clients use '--shared-bucket' to enable extra S3 and MyS3 comparisons");
                Console.WriteLine();
                Console.WriteLine("Windows sync (1) example:");
                Console.WriteLine("MyS3.CLI --verbose --bucket=myfiles --region=eu-west-1 --aws-access-key=AKIA123etc,abc123etc");
                Console.WriteLine("         --encryption-password=\"my password\" --mys3-folder=\"C:\\Users\\Smiley\\Documents\\MyS3\\\"");
                Console.WriteLine("         --restore-removed-files=2020-07-01-15 --restore-file-versions=2020-01-01-12'");
                Console.WriteLine();
                Console.WriteLine("*nix sync (1) example:");
                Console.WriteLine("MyS3.CLI --verbose --bucket=myfiles --region=eu-west-1 --aws-access-key=AKIA123etc,abc123etc");
                Console.WriteLine("         --encryption-password=\"my password\" --mys3-folder=\"/home/Smiley/Documents/MyS3/\"");
                Console.WriteLine("         --restore-removed-files=2020-07-01-15 --restore-file-versions=2020-01-01-12'");

                return;
            }
        }
    }
}