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
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using LambdaSharp.CustomResource;

namespace LambdaSharp.Core.S3Writer {

    public class UnzipLogic {

        //--- Constants ---
        private const int MAX_BATCH_DELETE_OBJECTS = 1000;

        //--- Fields ---
        private readonly ILambdaLogger _logger;
        private readonly string _manifestBucket;
        private readonly IAmazonS3 _s3Client;
        private readonly TransferUtility _transferUtility;

        //--- Constructors ---
        public UnzipLogic(ILambdaLogger logger, string manifestBucket, IAmazonS3 s3Client) {
            _logger = logger;
            _manifestBucket = manifestBucket;
            _s3Client = new AmazonS3Client();
            _transferUtility = new TransferUtility(_s3Client);
        }

        //--- Methods ---
        public async Task<Response<ResponseProperties>> Create(RequestProperties properties) {
            _logger.LogInfo($"uploading package s3://{properties.SourceBucketName}/{properties.SourceKey} to S3 bucket {properties.DestinationBucketName}");

            // download package and copy all files to destination bucket
            var files = new List<string>();
            if(!await ProcessZipFileItemsAsync(properties.SourceBucketName, properties.SourceKey, async entry => {
                using(var stream = entry.Open()) {
                    var memoryStream = new MemoryStream();
                    await stream.CopyToAsync(memoryStream);
                    var destination = Path.Combine(properties.DestinationKey, entry.FullName).Replace('\\', '/');
                    _logger.LogInfo($"uploading file: {destination}");
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
            _logger.LogInfo($"uploaded {files.Count:N0} files");

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
                $"{properties.DestinationBucketName}/{properties.SourceKey}"
            );
            return new Response<ResponseProperties> {
                PhysicalResourceId = $"s3unzip:{properties.DestinationBucketName}:{properties.DestinationKey}:{properties.SourceKey}",
                Properties = new ResponseProperties {
                    Url = $"s3://{properties.DestinationBucketName}/{properties.DestinationKey}"
                }
            };
        }

        public async Task<Response<ResponseProperties>> Update(RequestProperties oldProperties, RequestProperties properties) {

            // TODO (2019-01-15, bjorg): only update changed files
            await Delete(oldProperties);
            return await Create(properties);
        }

        public async Task<Response<ResponseProperties>> Delete(RequestProperties properties) {
            _logger.LogInfo($"deleting package {properties.SourceKey} from S3 bucket {properties.DestinationBucketName}");

            // download package manifest
            var files = new List<string>();
            var key = $"{properties.DestinationBucketName}/{properties.SourceKey}";
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
                _logger.LogWarn($"unable to dowload zip file from s3://{_manifestBucket}/{key}");
            }
            _logger.LogInfo($"found {files.Count:N0} files to delete");

            // delete all files from manifest
            while(files.Any()) {
                var batch = files.Take(MAX_BATCH_DELETE_OBJECTS).Select(file => Path.Combine(properties.DestinationKey, file).Replace('\\', '/')).ToList();
                _logger.LogInfo($"deleting files: {string.Join(", ", batch)}");
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
                _logger.LogWarn($"unable to delete manifest file at s3://{_manifestBucket}/{key}");
            }
            return new Response<ResponseProperties>();
        }

        private async Task<bool> ProcessZipFileItemsAsync(string bucketName, string key, Func<ZipArchiveEntry, Task> callbackAsync) {
            var tmpFilename = Path.GetTempFileName() + ".zip";
            try {
                _logger.LogInfo($"downloading s3://{bucketName}/{key}");
                await _transferUtility.DownloadAsync(new TransferUtilityDownloadRequest {
                    BucketName = bucketName,
                    Key = key,
                    FilePath = tmpFilename
                });
            } catch(Exception e) {
                _logger.LogErrorAsWarning(e, "s3 download failed");
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