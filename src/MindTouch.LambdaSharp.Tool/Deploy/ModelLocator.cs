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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Amazon.S3.Model;

namespace MindTouch.LambdaSharp.Tool.Deploy {

    public class ModelLocation {

        //--- Properties ---
        public string BucketName { get; set; }
        public string Path { get; set; }
    }

    public class ModelLocator : AModelProcessor {

        //--- Class Fields ---
        private static readonly Regex ModuleKeyPattern = new Regex(@"^(?<ModuleName>\w+)(:(?<Version>\*|[\w\.\-]+))?(@(?<BucketName>\w+))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        //--- Constructors ---
        public ModelLocator(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public async Task<ModelLocation> LocateAsync(string moduleKey) {

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
                var match = ModuleKeyPattern.Match(moduleKey);
                if(match.Success) {
                    var requestedModuleName = GetMatchValue("ModuleName");
                    var requestedVersionText = GetMatchValue("Version");
                    var requestedBucketName = GetMatchValue("BucketName");

                    // parse optional version
                    var requestedVersion = ((requestedVersionText != null) && (requestedVersionText != "*"))
                        ? VersionInfo.Parse(requestedVersionText)
                        : null;

                    // by default, attempt to find the module in the deployment bucket and then the regional lambdasharp bucket
                    var searchBuckets = (requestedBucketName != null)
                        ? new[] {
                            requestedBucketName,
                            $"{requestedBucketName}-{Settings.AwsRegion}"
                        }
                        :  new[] {
                            Settings.DeploymentBucketName,

                            // TODO: do we still need to default to the 'lambdasharp` bucket?
                            $"lambdasharp-{Settings.AwsRegion}"
                        };

                    // attempt to find a matching version
                    var found = searchBuckets.Select(bucket => new {
                        BucketName = bucket,
                        Version = FindNewestVersion(Settings, bucketName, requestedModuleName, requestedVersion).Result
                    }).FirstOrDefault(result => result.Version != null);
                    if(found == null) {
                        AddError($"could not find module: {requestedModuleName} ({((requestedVersion != null) ? $"v{requestedVersion}" : "any version")})");
                        return null;
                    }
                    bucketName = found.BucketName;
                    path = $"Modules/{requestedModuleName}/Versions/{found.Version}/cloudformation.json";

                    // local function
                    string GetMatchValue(string groupName) {
                        var group = match.Groups[groupName];
                        return group.Success ? group.Value : null;
                    }
                } else {
                    AddError("invalid module reference");
                    return null;
                }
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