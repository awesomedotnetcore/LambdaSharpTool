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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Amazon.S3.Model;
using MindTouch.LambdaSharp.Tool.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MindTouch.LambdaSharp.Tool {

    public class ModelManifestLoader : AModelProcessor {

        //--- Class Fields ---
        private static readonly Regex ModuleKeyPattern = new Regex(@"^(?<ModuleName>\w+)(:(?<Version>\*|[\w\.\-]+))?(@(?<BucketName>\w+))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        //--- Constructors --
        public ModelManifestLoader(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public async Task<ModuleManifest> LoadFromFileAsync(string filepath) {

            // load cloudformation template
            var template = await File.ReadAllTextAsync(filepath);
            var cloudformation = JsonConvert.DeserializeObject<JObject>(template);

            // extract manifest
            var manifest = GetManifest(cloudformation);
            if(manifest == null) {
                AddError("CloudFormation file does not contain a LambdaSharp manifest");
                return null;
            }

            // validate manifest
            if(manifest.Version != ModuleManifest.CurrentVersion) {
                AddError($"Incompatible LambdaSharp manifest version (found: {manifest.Version ?? "<null>"}, expected: {ModuleManifest.CurrentVersion})");
                return null;
            }
            return manifest;
        }

        public async Task<ModuleManifest> LoadFromS3Async(string bucketName, string templatePath) {

            // download cloudformation template
            var cloudformationText = await GetS3ObjectContents(bucketName, templatePath);
            if(cloudformationText == null) {
                AddError($"could not load CloudFormation template from s3://{bucketName}/{templatePath}");
                return null;
            }

            // extract manifest
            var cloudformation = JsonConvert.DeserializeObject<JObject>(cloudformationText);
            var manifest = GetManifest(cloudformation);
            if(manifest == null) {
                AddError("CloudFormation file does not contain a LambdaSharp manifest");
                return null;
            }

            // validate manifest
            if(manifest.Version != ModuleManifest.CurrentVersion) {
                AddError($"Incompatible LambdaSharp manifest version (found: {manifest.Version ?? "<null>"}, expected: {ModuleManifest.CurrentVersion})");
                return null;
            }
            return manifest;
        }

        public async Task<ModuleLocation> LocateAsync(string moduleReference) {

            // module reference formats:
            // * ModuleName
            // * ModuleName:*
            // * ModuleName:Version
            // * ModuleName@Bucket
            // * ModuleName:*@Bucket
            // * ModuleName:Version@Bucket
            // * s3://bucket-name/Modules/{ModuleName}/Versions/{Version}/
            // * s3://bucket-name/Modules/{ModuleName}/Versions/{Version}/cloudformation.json

            if(moduleReference.StartsWith("s3://", StringComparison.Ordinal)) {
                var uri = new Uri(moduleReference);

                // absolute path always starts with '/', which needs to be removed
                var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
                if((pathSegments.Count < 4) || (pathSegments[0] != "Modules") || (pathSegments[2] != "Versions")) {
                    return null;
                }
                if(pathSegments.Last() != "cloudformation.json") {
                    pathSegments.Add("cloudformation.json");
                }
                return new ModuleLocation {
                    ModuleName = pathSegments[1],
                    ModuleVersion = VersionInfo.Parse(pathSegments[3]),
                    BucketName = uri.Host,
                    TemplatePath = string.Join("/", pathSegments)
                };
            } else {
                var match = ModuleKeyPattern.Match(moduleReference);
                if(match.Success) {
                    var requestedModuleName = GetMatchValue("ModuleName");
                    var requestedVersionText = GetMatchValue("Version");
                    var requestedBucketName = GetMatchValue("BucketName");

                    // parse optional version
                    var requestedVersion = ((requestedVersionText != null) && (requestedVersionText != "*"))
                        ? VersionInfo.Parse(requestedVersionText)
                        : null;

                    // find compatible module version
                    return await LocateAsync(requestedModuleName, requestedVersion, requestedVersion, requestedBucketName);

                    // local function
                    string GetMatchValue(string groupName) {
                        var group = match.Groups[groupName];
                        return group.Success ? group.Value : null;
                    }
                }
            }
            return null;
        }

        public async Task<ModuleLocation> LocateAsync(string moduleName, VersionInfo minVersion, VersionInfo maxVersion, string bucketName) {

            // by default, attempt to find the module in the deployment bucket and then the regional lambdasharp bucket
            var searchBuckets = (bucketName != null)
                ? new[] {
                    bucketName,
                    $"{bucketName}-{Settings.AwsRegion}"
                }
                :  new[] {
                    Settings.DeploymentBucketName,

                    // TODO (2018-12-03, bjorg): do we still need to default to the 'lambdasharp` bucket?
                    $"lambdasharp-{Settings.AwsRegion}"
                };

            // attempt to find a matching version
            VersionInfo foundVersion = null;
            string foundBucketName = null;
            foreach(var bucket in searchBuckets) {
                foundVersion = await FindNewestVersion(Settings, bucket, moduleName, minVersion, maxVersion);
                if(foundVersion != null) {
                    foundBucketName = bucket;
                    break;
                }
            }
            if(foundVersion == null) {
                var versionConstraint = "any version";
                if((minVersion != null) && (maxVersion != null)) {
                    if(minVersion == maxVersion) {
                        versionConstraint = $"v{minVersion}";
                    } else {
                        versionConstraint = $"v{minVersion}..v{maxVersion}";
                    }
                } else if(minVersion != null) {
                    versionConstraint = $"v{minVersion} or later";
                } else if(maxVersion != null) {
                    versionConstraint = $"v{maxVersion} or earlier";
                }
                AddError($"could not find module: {moduleName} ({versionConstraint})");
                return null;
            }
            return new ModuleLocation {
                ModuleName = moduleName,
                ModuleVersion = foundVersion,
                BucketName = foundBucketName,
                TemplatePath = $"Modules/{moduleName}/Versions/{foundVersion}/cloudformation.json"
            };
        }

        private async Task<string> GetS3ObjectContents(string bucketName, string key) {
            try {
                var response = await Settings.S3Client.GetObjectAsync(new GetObjectRequest {
                    BucketName = bucketName,
                    Key = key
                });
                using(var stream = new MemoryStream()) {
                    await response.ResponseStream.CopyToAsync(stream);
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            } catch {
                return null;
            }
        }

        private ModuleManifest GetManifest(JObject cloudformation) {
            if(
                cloudformation.TryGetValue("Metadata", out JToken metadataToken)
                && (metadataToken is JObject metadata)
                && metadata.TryGetValue("LambdaSharp::Manifest", out JToken manifestToken)
            ) {
                return manifestToken.ToObject<ModuleManifest>();
            }
            return null;
        }

        private async Task<VersionInfo> FindNewestVersion(Settings settings, string bucketName, string moduleName, VersionInfo minVersion, VersionInfo maxVersion) {

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
                    .Where(IsVersionMatch)
                );
                request.ContinuationToken = response.NextContinuationToken;
            } while(request.ContinuationToken != null);
            if(!versions.Any()) {
                return null;
            }

            // attempt to identify the newest version
            return versions.Max();

            // local function
            bool IsVersionMatch(VersionInfo version) {
                if((minVersion == null) && (maxVersion == null)) {
                    return !version.IsPreRelease;
                }
                if(maxVersion == minVersion) {
                    return version.IsCompatibleWith(minVersion);
                }
                if((minVersion != null) && (version < minVersion)) {
                    return false;
                }
                if((maxVersion != null) && (version > maxVersion)) {
                    return false;
                }
                return true;
            }
        }
    }
}