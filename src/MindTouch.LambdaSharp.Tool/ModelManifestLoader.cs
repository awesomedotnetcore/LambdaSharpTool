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

    public class ModelLocation {

        //--- Properties ---
        public string ModuleName { get; set; }
        public string ModuleVersion { get; set; }
        public string BucketName { get; set; }
        public string TemplatePath { get; set; }

        //--- Methods ---
        public override string ToString() {
            var result = new StringBuilder();
            if(ModuleName != null) {
                result.Append(ModuleName);
                if(ModuleVersion != null) {
                    result.Append($" (v{ModuleVersion})");
                }
                result.Append(" from ");
                result.Append(BucketName);
            } else {
                result.Append($"s3://{BucketName}/{TemplatePath}");
            }
            return result.ToString();
        }
    }

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

        public async Task<ModuleManifest> LoadFromModuleReferenceAsync(string moduleReference) {
            var location = await LocateAsync(moduleReference);
            if(location == null) {
                return null;
            }
            Console.WriteLine($"Loading manifest for {location}");
            return await LoadFromS3Async(location.BucketName, location.TemplatePath);
        }

        public async Task<ModelLocation> LocateAsync(string moduleReference) {

            // module key formats
            // * ModuleName
            // * ModuleName:*
            // * ModuleName:Version
            // * ModuleName@Bucket
            // * ModuleName:*@Bucket
            // * ModuleName:Version@Bucket
            // * s3://bucket-name/Modules/{ModuleName}/{Version}/

            string bucketName = null;
            if(moduleReference.StartsWith("s3://", StringComparison.Ordinal)) {
                var uri = new Uri(moduleReference);
                bucketName = uri.Host;

                // absolute path always starts with '/', which needs to be removed
                var path = uri.AbsolutePath.Substring(1);
                if(!path.EndsWith("/cloudformation.json", StringComparison.Ordinal)) {
                    path = path.TrimEnd('/') + "/cloudformation.json";
                }
                return new ModelLocation {
                    BucketName = bucketName,
                    TemplatePath = path
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

                    // by default, attempt to find the module in the deployment bucket and then the regional lambdasharp bucket
                    var searchBuckets = (requestedBucketName != null)
                        ? new[] {
                            requestedBucketName,
                            $"{requestedBucketName}-{Settings.AwsRegion}"
                        }
                        :  new[] {
                            Settings.DeploymentBucketName,

                            // TODO (2018-12-03, bjorg): do we still need to default to the 'lambdasharp` bucket?
                            $"lambdasharp-{Settings.AwsRegion}"
                        };

                    // attempt to find a matching version
                    string foundVersion = null;
                    foreach(var bucket in searchBuckets) {
                        foundVersion = await FindNewestVersion(Settings, bucket, requestedModuleName, requestedVersion);
                        if(foundVersion != null) {
                            bucketName = bucket;
                            break;
                        }
                    }
                    if(foundVersion == null) {
                        AddError($"could not find module: {requestedModuleName} ({((requestedVersion != null) ? $"v{requestedVersion}" : "any version")})");
                        return null;
                    }
                    return new ModelLocation {
                        ModuleName = requestedModuleName,
                        ModuleVersion = foundVersion,
                        BucketName = bucketName,
                        TemplatePath = $"Modules/{requestedModuleName}/Versions/{foundVersion}/cloudformation.json"
                    };

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

        private async Task<string> FindNewestVersion(Settings settings, string bucketName, string moduleName, VersionInfo requestedVersion) {

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
            return versions.Max().ToString();
        }
    }
}