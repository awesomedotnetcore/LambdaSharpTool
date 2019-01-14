/*
 * MindTouch λ#
 * Copyright (C) 2018 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit mindtouch.com;
 * please review the licensing section.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using MindTouch.LambdaSharp;
using MindTouch.LambdaSharp.CustomResource;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace MindTouch.LambdaSharpS3PackageLoader.ResourceHandler {

    public class RequestProperties {

        //--- Properties ---
        public string DestinationBucket { get; set; }
        public string DestinationKeyPrefix { get; set; }
        public string SourceBucket { get; set; }
        public string SourcePackageKey { get; set; }

        public string DestinationBucketName => DestinationBucket.StartsWith("arn:")
            ? AwsConverters.ConvertBucketArnToName(DestinationBucket)
            : DestinationBucket;

        public string SourceBucketName => SourceBucket.StartsWith("arn:")
            ? AwsConverters.ConvertBucketArnToName(SourceBucket)
            : SourceBucket;
    }

    public class ResponseProperties {

        //--- Properties ---
        public string Url { get; set; }
    }

    public class Function : ALambdaCustomResourceFunction<RequestProperties, ResponseProperties> {

        //--- Constants ---
        private const int MAX_BATCH_DELETE_OBJECTS = 1000;

        //--- Fields ---
        private string _manifestBucket;
        private IAmazonS3 _s3Client;
        private TransferUtility _transferUtility;

        //--- Methods ---
        public override Task InitializeAsync(LambdaConfig config) {
            _manifestBucket = config.ReadS3BucketName("ManifestBucket");
            _s3Client = new AmazonS3Client();
            _transferUtility = new TransferUtility(_s3Client);
            return Task.CompletedTask;
        }

        protected override Task<Response<ResponseProperties>> HandleCreateResourceAsync(Request<RequestProperties> request)
            => UploadFiles(request.ResourceProperties);

        protected override Task<Response<ResponseProperties>> HandleDeleteResourceAsync(Request<RequestProperties> request)
            => DeleteFiles(request.ResourceProperties);

        protected override async Task<Response<ResponseProperties>> HandleUpdateResourceAsync(Request<RequestProperties> request) {
            await DeleteFiles(request.OldResourceProperties);
            return await UploadFiles(request.ResourceProperties);
        }

        private async Task<Response<ResponseProperties>> UploadFiles(RequestProperties properties) {
            LogInfo($"uploading package s3://{properties.SourceBucketName}/{properties.SourcePackageKey} to S3 bucket {properties.DestinationBucketName}");

            // download package and copy all files to destination bucket
            var files = new List<string>();
            if(!await ProcessZipFileItemsAsync(properties.SourceBucketName, properties.SourcePackageKey, async entry => {
                using(var stream = entry.Open()) {
                    var memoryStream = new MemoryStream();
                    await stream.CopyToAsync(memoryStream);
                    var destination = Path.Combine(properties.DestinationKeyPrefix, entry.FullName).Replace('\\', '/');
                    LogInfo($"uploading file: {destination}");
                    await _transferUtility.UploadAsync(
                        memoryStream,
                        properties.DestinationBucketName,
                        destination
                    );
                    files.Add(entry.FullName);
                }
            })) {
                throw new FileNotFoundException("Unable to download source package");
            }
            LogInfo($"uploaded {files.Count:N0} files");

            // create package manifest for future deletion
            var manifestStream = new MemoryStream();
            using(var manifest = new ZipArchive(manifestStream, ZipArchiveMode.Create, leaveOpen: true))
            using(var manifestEntryStream = manifest.CreateEntry("manifest.txt").Open())
            using(var manifestEntryWriter = new StreamWriter(manifestEntryStream)) {
                await manifestEntryWriter.WriteAsync(string.Join("\n", files));
            }
            await _transferUtility.UploadAsync(
                manifestStream,
                _manifestBucket,
                $"{properties.DestinationBucketName}/{properties.SourcePackageKey}"
            );
            return new Response<ResponseProperties> {
                PhysicalResourceId = $"s3package:{properties.DestinationBucketName}:{properties.DestinationKeyPrefix}:{properties.SourcePackageKey}",
                Properties = new ResponseProperties {
                    Url = $"s3://{properties.DestinationBucketName}/{properties.DestinationKeyPrefix}"
                }
            };
        }

        private async Task<Response<ResponseProperties>> DeleteFiles(RequestProperties properties) {
            LogInfo($"deleting package {properties.SourcePackageKey} from S3 bucket {properties.DestinationBucketName}");

            // download package manifest
            var files = new List<string>();
            var key = $"{properties.DestinationBucketName}/{properties.SourcePackageKey}";
            if(!await ProcessZipFileItemsAsync(
                _manifestBucket,
                key,
                async entry => {
                    using(var stream = entry.Open())
                    using(var reader = new StreamReader(stream)) {
                        var manifest = await reader.ReadToEndAsync();
                        files.AddRange(manifest.Split('\n'));
                    }
                }
            )) {
                LogWarn($"unable to dowload zip file from s3://{_manifestBucket}/{key}");
            }
            LogInfo($"found {files.Count:N0} files to delete");

            // delete all files from manifest
            while(files.Any()) {
                var batch = files.Take(MAX_BATCH_DELETE_OBJECTS).Select(file => Path.Combine(properties.DestinationKeyPrefix, file).Replace('\\', '/')).ToList();
                LogInfo($"deleting files: {string.Join(", ", batch)}");
                await _s3Client.DeleteObjectsAsync(new DeleteObjectsRequest {
                    BucketName = properties.DestinationBucketName,
                    Objects = batch.Select(filepath => new KeyVersion {
                        Key = filepath
                    }).ToList()
                });
                files = files.Skip(MAX_BATCH_DELETE_OBJECTS).ToList();
            }

            // delete manifest file
            try {
                await _s3Client.DeleteObjectAsync(new DeleteObjectRequest {
                    BucketName = _manifestBucket,
                    Key = key
                });
            } catch {
                LogWarn($"unable to delete manifest file at s3://{_manifestBucket}/{key}");
            }
            return new Response<ResponseProperties>();
        }

        private async Task<bool> ProcessZipFileItemsAsync(string bucketName, string key, Func<ZipArchiveEntry, Task> callbackAsync) {
            var tmpFilename = Path.GetTempFileName() + ".zip";
            try {
                LogInfo($"downloading s3://{bucketName}/{key}");
                await _transferUtility.DownloadAsync(new TransferUtilityDownloadRequest {
                    BucketName = bucketName,
                    Key = key,
                    FilePath = tmpFilename
                });
            } catch(Exception e) {
                LogErrorAsWarning(e, "s3 download failed");
                return false;
            }
            try {
                using(var zip = ZipFile.Open(tmpFilename, ZipArchiveMode.Read)) {
                    foreach(var entry in zip.Entries) {
                        await callbackAsync(entry);
                    }
                }
            } finally {
                try {
                    File.Delete(tmpFilename);
                } catch { }
            }
            return true;
        }
    }
}