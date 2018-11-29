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

using System.IO;
using System.Text;
using System.Threading.Tasks;
using Amazon.S3.Model;
using MindTouch.LambdaSharp.Tool.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MindTouch.LambdaSharp.Tool.Deploy {

    public class ModelManifestLoader : AModelProcessor {

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
    }
}