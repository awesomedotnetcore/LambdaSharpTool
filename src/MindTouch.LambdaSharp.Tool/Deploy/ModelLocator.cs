/*
 * MindTouch λ#
 * Copyright (C) 2006-2018 MindTouch, Inc.
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
using System.Linq;
using System.Threading.Tasks;
using Amazon.S3.Model;

namespace MindTouch.LambdaSharp.Tool.Deploy {

    public class ModelLocation {

        //--- Properties ---
        public string BucketName { get; set; }
        public string Path { get; set; }
    }

    public class ModelLocator : AModelProcessor {

        //--- Constructors ---
        public ModelLocator(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public async Task<ModelLocation> LocateAsync(string moduleKey) {
            var searchBuckets = new[] {
                Settings.DeploymentBucketName,
                $"lambdasharp-{Settings.AwsRegion}"
            };

            // module key formats
            // * ModuleName
            // * ModuleName:*
            // * ModuleName:Version
            // * ModuleName@Bucket
            // * ModuleName:*@Bucket
            // * ModuleName:Version@Bucket
            // * s3://bucket-name/Modules/{ModuleName}/{Version}/

            string path = null;
            string bucketName = null;
            if(moduleKey.StartsWith("s3://", StringComparison.Ordinal)) {
                var uri = new Uri(moduleKey);
                bucketName = uri.Host;

                // absolute path always starts with '/', which needs to be removed
                path = uri.AbsolutePath.Substring(1);
                if(!path.EndsWith("/cloudformation.json", StringComparison.Ordinal)) {
                    path = path.TrimEnd('/') + "/cloudformation.json";
                }
            } else {
                VersionInfo requestedVersion = null;
                string moduleName;

                // check if a version suffix is specified
                // NOTE: avoid matching on "C:/" strings!
                if(moduleKey.IndexOf(':', StringComparison.Ordinal) > 1) {
                    var parts = moduleKey.Split(':', 2);
                    moduleName = parts[0];
                    if(parts[1] != "*") {
                        requestedVersion = VersionInfo.Parse(parts[1]);
                    }
                } else {
                    moduleName = moduleKey;
                }

                // attempt to find the module in the deployment bucket and then the regional lambdasharp bucket
                var found = await searchBuckets.Select(async bucket => {
                    var version = await FindNewestVersion(Settings, bucketName, moduleName, requestedVersion);
                    return (version != null)
                        ? new {
                            BucketName = bucket,
                            Version = version
                        }
                        : null;
                }).FirstOrDefault();
                if(found == null) {
                    AddError($"could not find module: {moduleName} (v{requestedVersion})");
                    return null;
                }
                bucketName = found.BucketName;
                path = $"Modules/{moduleName}/Versions/{found.Version}/cloudformation.json";
            }
            return new ModelLocation {
                BucketName = bucketName,
                Path = path
            };
        }

        private async Task<VersionInfo> FindNewestVersion(Settings settings, string bucketName, string moduleName, VersionInfo requestedVersion) {

            // enumerate versions in bucket
            var versions = new List<VersionInfo>();
            var request = new ListObjectsV2Request {
                BucketName = bucketName,
                Prefix = $"Modules/{moduleName}/Versions/",
                Delimiter = "/",
                MaxKeys = 100
            };
            do {
                var response = await settings.S3Client.ListObjectsV2Async(request);
                versions.AddRange(response.CommonPrefixes
                    .Select(prefix => prefix.Substring(request.Prefix.Length).TrimEnd('/'))
                    .Select(found => VersionInfo.Parse(found))
                    .Where(version => (requestedVersion == null) ? !version.IsPreRelease : version.IsCompatibleWith(requestedVersion))
                );
                request.ContinuationToken = response.NextContinuationToken;
            } while(request.ContinuationToken != null);
            if(!versions.Any()) {
                return null;
            }

            // attempt to identify the newest version
            return versions.Max();
        }
    }
}