using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace MyS3
{
    public class S3Wrapper
    {
        private static string APP_DATA_TEST_DIRECTORY_PATH
        {
            get
            {
                return Tools.RunningOnWindows() ?
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\" + "MyS3" + @"\" :
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"/" + "MyS3" + @"/";
            }
        }

        public static readonly long MIN_MULTIPART_SIZE = 5 * (long)Math.Pow(2, 20); // 5 MB
                                                                                    // No progress report when lower

        public static readonly int PROGRESS_REPORT_PAUSE = 1000;

        // ---

        private readonly string bucket;
        private readonly RegionEndpoint endpoint;

        private readonly string awsAccessKeyID;
        private readonly string awsSecretAccessKey;

        private readonly IAmazonS3 client;

        public bool TestsRun { get { return testsRun; } }
        private bool testsRun;

        public string TestsResult { get { return testsResult; } }
        private string testsResult;

        public S3Wrapper(string bucket, RegionEndpoint endpoint, string awsAccessKeyID, string awsSecretAccessKey)
        {
            this.bucket = bucket;
            this.endpoint = endpoint;
            this.awsAccessKeyID = awsAccessKeyID;
            this.awsSecretAccessKey = awsSecretAccessKey;

            client = new AmazonS3Client(this.awsAccessKeyID, this.awsSecretAccessKey, this.endpoint);
        }

        // ---

        public void RunTests() // try every method
        {
            testsRun = false;
            testsResult = null;

            ThreadPool.QueueUserWorkItem(new WaitCallback((object callback) =>
            {
                try
                {
                    // Setup
                    if (!Directory.Exists(APP_DATA_TEST_DIRECTORY_PATH)) Directory.CreateDirectory(APP_DATA_TEST_DIRECTORY_PATH);
                    string uploadTestFilePath = APP_DATA_TEST_DIRECTORY_PATH + "uploaded test file";
                    string downloadTestFilePath = APP_DATA_TEST_DIRECTORY_PATH + "downloaded test file";
                    string s3ObjectPath = "MyS3 test file - can be deleted";
                    string renamedS3FilePath = "MyS3 test file - can be deleted - renamed";

                    // 0. Enable versioning
                    if (SetVersioningAsync().HttpStatusCode != HttpStatusCode.OK)
                        throw new Exception("Unable to set versioning on bucket");

                    // 1. Upload
                    File.WriteAllText(uploadTestFilePath, "MyS3 test file content");
                    UploadAsync(uploadTestFilePath, s3ObjectPath, "", null, new CancellationToken(), null).Wait();

                    // 2. Get metadata
                    GetMetadata(s3ObjectPath, null).Wait();

                    // 3. Get file list
                    GetObjectListAsync(null, null).Wait(); // root list

                    // 4. Download
                    DownloadAsync(downloadTestFilePath, s3ObjectPath, null, "", new CancellationToken(), null, MyS3Runner.TransferType.DOWNLOAD).Wait();
                    bool uploadAndDownloadSuccess = File.ReadAllBytes(uploadTestFilePath).SequenceEqual(File.ReadAllBytes(downloadTestFilePath));
                    File.Delete(uploadTestFilePath);
                    File.Delete(downloadTestFilePath);
                    if (!uploadAndDownloadSuccess) throw new Exception("Downloaded test file differs from uploaded test file");

                    // 5. Copy
                    CopyAsync(s3ObjectPath, renamedS3FilePath, null).Wait();

                    // 6. Clean up
                    List<S3ObjectVersion> testFileVersions = GetCompleteObjectVersionsList(s3ObjectPath);
                    foreach (S3ObjectVersion version in testFileVersions)
                        RemoveAsync(s3ObjectPath, version.VersionId).Wait();
                    testFileVersions = GetCompleteObjectVersionsList(renamedS3FilePath);
                    foreach (S3ObjectVersion version in testFileVersions)
                        RemoveAsync(renamedS3FilePath, version.VersionId).Wait();
                }
                catch (Exception ex)
                {
                    testsResult = ex.Message;
                }

                testsRun = true;
            }));
        }

        // ---

        public GetBucketVersioningResponse GetVersioningAsync()
        {
            GetBucketVersioningRequest versionRequest = new GetBucketVersioningRequest
            {
                BucketName = bucket
            };

            return client.GetBucketVersioningAsync(versionRequest).Result;
        }

        public PutBucketVersioningResponse SetVersioningAsync()
        {
            PutBucketVersioningRequest setVersionRequest = new PutBucketVersioningRequest
            {
                BucketName = bucket,
                VersioningConfig = new S3BucketVersioningConfig
                {
                    Status = VersionStatus.Enabled
                }
            };

            return client.PutBucketVersioningAsync(setVersionRequest).Result;
        }

        // ---

        public async Task<ListObjectsResponse> GetObjectListAsync(string s3ObjectPath, string marker)
        {
            ListObjectsRequest request = new ListObjectsRequest
            {
                BucketName = bucket,
            };
            if (s3ObjectPath != null) request.Prefix = s3ObjectPath;
            if (marker != null) request.Marker = marker;

            return await client.ListObjectsAsync(request);
        }

        public List<S3Object> GetCompleteObjectList()
        {
            List<S3Object> s3ObjectsInfo = new List<S3Object>();

            string marker = null;
            while (true)
            {
                ListObjectsResponse listResult = GetObjectListAsync(null, marker).Result;
                if (listResult.HttpStatusCode == HttpStatusCode.OK)
                {
                    s3ObjectsInfo.AddRange(listResult.S3Objects);

                    if (listResult.IsTruncated)
                        marker = listResult.NextMarker;
                    else
                        break;
                }
                else
                {
                    break;
                }
            }

            return s3ObjectsInfo;
        }

        // ---

        public ListVersionsResponse GetObjectVersionsListAsync(string s3ObjectPath, string marker)
        {
            ListVersionsRequest request = new ListVersionsRequest
            {
                BucketName = bucket,
            };
            if (s3ObjectPath != null) request.Prefix = s3ObjectPath;
            if (marker != null) request.KeyMarker = marker;

            return client.ListVersionsAsync(request).Result;
        }

        public List<S3ObjectVersion> GetCompleteObjectVersionsList(string s3ObjectPath)
        {
            List<S3ObjectVersion> s3ObjectVersions = new List<S3ObjectVersion>();

            string marker = null;
            while (true)
            {
                ListVersionsResponse listResult = GetObjectVersionsListAsync(s3ObjectPath, marker);
                if (listResult.HttpStatusCode == HttpStatusCode.OK)
                {
                    s3ObjectVersions.AddRange(listResult.Versions.ToList<S3ObjectVersion>());

                    if (listResult.IsTruncated)
                        marker = listResult.NextKeyMarker;
                    else
                        break;
                }
                else
                {
                    break;
                }
            }

            return s3ObjectVersions;
        }

        // ---

        public Dictionary<string, DateTime> GetCompleteRemovedObjectList()
        {
            List<S3ObjectVersion> s3ObjectVersions = GetCompleteObjectVersionsList(null);

            Dictionary<string, DateTime> removedS3Objects = new Dictionary<string, DateTime>(); // key ==> datetime
            foreach (S3ObjectVersion s3ObjectVersion in s3ObjectVersions)
            {
                if (s3ObjectVersion.IsDeleteMarker)
                {
                    if (removedS3Objects.ContainsKey(s3ObjectVersion.Key))
                    {
                        if (removedS3Objects[s3ObjectVersion.Key] < s3ObjectVersion.LastModified)
                            removedS3Objects[s3ObjectVersion.Key] = s3ObjectVersion.LastModified;
                    }
                    else
                    {
                        removedS3Objects.Add(s3ObjectVersion.Key, s3ObjectVersion.LastModified);
                    }
                }
                else
                {
                    if (removedS3Objects.ContainsKey(s3ObjectVersion.Key) && removedS3Objects[s3ObjectVersion.Key] < s3ObjectVersion.LastModified)
                        removedS3Objects.Remove(s3ObjectVersion.Key);
                }
            }

            return removedS3Objects;
        }

        public int CleanUpRemovedObjects(DateTime timeRemoved)
        {
            // Get lists
            List<S3ObjectVersion> s3ObjectVersions = GetCompleteObjectVersionsList(null);
            Dictionary<string, DateTime> removedS3Objects = GetCompleteRemovedObjectList();

            // Get oldest versions
            List<KeyVersion> oldS3ObjectVersions = new List<KeyVersion>();
            foreach (KeyValuePair<string, DateTime> kvp in removedS3Objects)
            {
                if (kvp.Value <= timeRemoved)
                {
                    foreach (S3ObjectVersion s3ObjectVersion in s3ObjectVersions)
                    {
                        if (kvp.Key == s3ObjectVersion.Key)
                        {
                            oldS3ObjectVersions.Add(new KeyVersion()
                            {
                                Key = kvp.Key,
                                VersionId = s3ObjectVersion.VersionId
                            });
                        }
                    }
                }
            }

            // Remove versions (and delete markers)
            if (oldS3ObjectVersions.Count > 0)
            {
                DeleteObjectsRequest removeRequest = new DeleteObjectsRequest()
                {
                    BucketName = bucket,
                    Objects = oldS3ObjectVersions
                };
                client.DeleteObjectsAsync(removeRequest).Wait();
            }

            if (oldS3ObjectVersions.Count == 1000)
                return oldS3ObjectVersions.Count + CleanUpRemovedObjects(timeRemoved);
            else
                return oldS3ObjectVersions.Count;
        }

        // ---

        public async Task<GetObjectMetadataResponse> GetMetadata(string s3ObjectPath, string version)
        {
            GetObjectMetadataRequest request = new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = s3ObjectPath
            };
            if (version != null) request.VersionId = version;

            return await client.GetObjectMetadataAsync(request);
        }

        // ---

        public async Task UploadAsync(string localFilePath, string s3ObjectPath, string shownFilePath,
            MetadataCollection metadata, CancellationToken cancelToken, Action<string, long, long, MyS3Runner.TransferType> progressEventHandler)
        {
            // Info
            FileInfo uploadFileInfo = new FileInfo(localFilePath);

            // Small upload (<5MB)
            if (new FileInfo(localFilePath).Length <= 5 * 1024 * 1024)
            {
                TransferUtility transferUtility = new TransferUtility(client);
                TransferUtilityUploadRequest transferRequest = new TransferUtilityUploadRequest
                {
                    BucketName = bucket,
                    Key = s3ObjectPath,
                    FilePath = localFilePath
                };
                if (metadata != null)
                    foreach (string key in metadata.Keys)
                        transferRequest.Metadata.Add(key, metadata[key]);

                await transferUtility.UploadAsync(transferRequest, cancelToken);
            }

            // Big upload
            else
            {
                InitiateMultipartUploadRequest initiateRequest = new InitiateMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = s3ObjectPath
                };
                if (metadata != null)
                    foreach (string key in metadata.Keys)
                        initiateRequest.Metadata.Add(key, metadata[key]);

                InitiateMultipartUploadResponse initResponse = await client.InitiateMultipartUploadAsync(initiateRequest, cancelToken);

                try
                {
                    // Upload parts
                    List<UploadPartResponse> uploadPartResponses = new List<UploadPartResponse>();
                    DateTime timeNextProgressReport = DateTime.Now.AddMilliseconds(PROGRESS_REPORT_PAUSE);
                    long transferredBytes = 0;
                    long filePosition = 0;
                    for (int i = 1; filePosition < uploadFileInfo.Length; i++)
                    {
                        UploadPartRequest uploadRequest = new UploadPartRequest
                        {
                            BucketName = bucket,
                            Key = s3ObjectPath,
                            UploadId = initResponse.UploadId,
                            PartNumber = i,
                            PartSize = MIN_MULTIPART_SIZE,
                            FilePosition = filePosition,
                            FilePath = localFilePath
                        };

                        if (progressEventHandler != null)
                        {
                            uploadRequest.StreamTransferProgress += (source, args) => {
                                transferredBytes += args.IncrementTransferred;
                                if (DateTime.Now > timeNextProgressReport)
                                {
                                    progressEventHandler(shownFilePath, transferredBytes, uploadFileInfo.Length, MyS3Runner.TransferType.UPLOAD);
                                    timeNextProgressReport = DateTime.Now.AddMilliseconds(PROGRESS_REPORT_PAUSE);
                                }
                            };
                        }

                        filePosition += MIN_MULTIPART_SIZE;

                        UploadPartResponse uploadPartResponse = await client.UploadPartAsync(uploadRequest, cancelToken);
                        uploadPartResponses.Add(uploadPartResponse);

                        Thread.Sleep(1000);
                    }

                    // Combine parts
                    CompleteMultipartUploadRequest completeRequest = new CompleteMultipartUploadRequest
                    {
                        BucketName = bucket,
                        Key = s3ObjectPath,
                        UploadId = initResponse.UploadId
                    };
                    completeRequest.AddPartETags(uploadPartResponses);
                    await client.CompleteMultipartUploadAsync(completeRequest, cancelToken);
                }
                catch (Exception ex)
                {
                    AbortMultipartUploadRequest abortMPURequest = new AbortMultipartUploadRequest
                    {
                        BucketName = bucket,
                        Key = s3ObjectPath,
                        UploadId = initResponse.UploadId
                    };
                    await client.AbortMultipartUploadAsync(abortMPURequest);

                    throw ex;
                }
            }
        }

        public async Task CopyAsync(string oldS3Path, string newS3Path, MetadataCollection metadata)
        {
            CopyObjectRequest copyRequest = new CopyObjectRequest
            {
                SourceBucket = bucket,
                DestinationBucket = bucket,
                SourceKey = oldS3Path,
                DestinationKey = newS3Path,
                MetadataDirective = S3MetadataDirective.REPLACE
            };
            if (metadata != null)
                foreach (string key in metadata.Keys)
                    copyRequest.Metadata.Add(key, metadata[key]);

            await client.CopyObjectAsync(copyRequest);
        }

        public async Task DownloadAsync(string localFilePath, string s3ObjectPath, string version, string shownFilePath, CancellationToken cancelToken, Action<string, long, long, MyS3Runner.TransferType> progressEventHandler, MyS3Runner.TransferType transferType)
        {
            GetObjectRequest request = new GetObjectRequest
            {
                BucketName = bucket,
                Key = s3ObjectPath
            };
            if (version != null) request.VersionId = version;

            GetObjectResponse getResult = await client.GetObjectAsync(request, cancelToken);
            
            using (Stream responseStream = getResult.ResponseStream)
            using (FileStream fileStream = File.OpenWrite(localFilePath))
            {
                DateTime timeNextProgressReport = DateTime.Now.AddMilliseconds(PROGRESS_REPORT_PAUSE);

                byte[] buffer = new byte[1024]; // 1 KB
                long downloadedBytes = 0;
                using (MemoryStream ms = new MemoryStream())
                {
                    int read;
                    while ((read = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (cancelToken.IsCancellationRequested) throw new TaskCanceledException();

                        ms.Write(buffer, 0, read);
                        downloadedBytes += read;

                        if (progressEventHandler != null)
                        {
                            if (DateTime.Now > timeNextProgressReport)
                            {
                                progressEventHandler(shownFilePath, downloadedBytes, getResult.Headers.ContentLength, transferType);
                                timeNextProgressReport = DateTime.Now.AddMilliseconds(PROGRESS_REPORT_PAUSE);
                            }
                        }
                    }

                    fileStream.Write(ms.ToArray());
                }
            }
        }

        public async Task RemoveAsync(string s3ObjectPath, string version)
        {
            DeleteObjectRequest request = new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = s3ObjectPath
            };
            if (version != null) request.VersionId = version;

            await client.DeleteObjectAsync(request);
        }

        public async Task RemoveAsync(Dictionary<string, List<string>> s3Objects)
        {
            DeleteObjectsRequest deleteRequest = new DeleteObjectsRequest
            {
                BucketName = bucket,
            };

            foreach (KeyValuePair<string, List<string>> s3Object in s3Objects)
            {
                if (s3Object.Value == null)
                    deleteRequest.Objects.Add(new KeyVersion() { Key = s3Object.Key });
                else
                    foreach (string versionId in s3Object.Value)
                        deleteRequest.Objects.Add(new KeyVersion() { Key = s3Object.Key, VersionId = versionId });

                if (deleteRequest.Objects.Count == 1000)
                {
                    await client.DeleteObjectsAsync(deleteRequest);
                    deleteRequest.Objects.Clear();
                }
            }

            if (deleteRequest.Objects.Count > 0)
                await client.DeleteObjectsAsync(deleteRequest);
        }
    }
}