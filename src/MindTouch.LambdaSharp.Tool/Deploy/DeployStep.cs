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
using System.Threading.Tasks;

namespace MindTouch.LambdaSharp.Tool.Deploy {

    public class DeployStep : AModelProcessor {

        //--- Constructors ---
        public DeployStep(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Class Methods ---
        public async Task<bool> DoAsync(
            DryRunLevel? dryRun,
            string moduleReference,
            string instanceName,
            bool allowDataLoos,
            bool protectStack,
            Dictionary<string, string> inputs,
            bool forceDeploy
        ) {

            // determine location of cloudformation template from module key
            var loader = new ModelManifestLoader(Settings, moduleReference);
            var location = await loader.LocateAsync(moduleReference);
            if(location == null) {
                return false;
            }

            // download module manifest
            var manifest = await loader.LoadFromS3Async(location.BucketName, location.TemplatePath);
            if(manifest == null) {
                return false;
            }

            // check that the LambdaSharp runtime & CLI versions match
            if(Settings.RuntimeVersion == null) {

                // runtime module doesn't expect a deployment tier to exist
                if(!forceDeploy && manifest.RuntimeCheck) {
                    AddError("could not determine the LambdaSharp runtime version; use --force-deploy to proceed anyway", new LambdaSharpDeploymentTierSetupException(Settings.Tier));
                    return false;
                }
            } else if(!Settings.ToolVersion.IsCompatibleWith(Settings.RuntimeVersion)) {
                if(!forceDeploy) {
                    AddError($"LambdaSharp CLI (v{Settings.ToolVersion}) and runtime (v{Settings.RuntimeVersion}) versions do not match; use --force-deploy to proceed anyway");
                    return false;
                }
            }

            // deploy module
            if(dryRun == null) {
                try {
                    return await new ModelUpdater(Settings, sourceFilename: null).DeployChangeSetAsync(
                        manifest,
                        location,
                        instanceName,
                        allowDataLoos,
                        protectStack,
                        inputs,
                        forceDeploy
                    );
                } catch(Exception e) {
                    AddError(e);
                    return false;
                }
            }
            return true;
        }
    }
}