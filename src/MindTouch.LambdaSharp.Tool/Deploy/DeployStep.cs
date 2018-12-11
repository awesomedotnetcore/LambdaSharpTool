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
using System.Linq;
using System.Threading.Tasks;
using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using MindTouch.LambdaSharp.Tool.Model;

namespace MindTouch.LambdaSharp.Tool.Deploy {

    public class DeployStep : AModelProcessor {

        //--- Fields ---
        private ModelManifestLoader _loader;

        //--- Constructors ---
        public DeployStep(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public async Task<bool> DoAsync(
            DryRunLevel? dryRun,
            string moduleReference,
            string instanceName,
            bool allowDataLoos,
            bool protectStack,
            Dictionary<string, string> inputs,
            bool forceDeploy
        ) {
            _loader = new ModelManifestLoader(Settings, moduleReference);

            // determine location of cloudformation template from module key
            var location = await _loader.LocateAsync(moduleReference);
            if(location == null) {
                return false;
            }

            // download module manifest
            var manifest = await _loader.LoadFromS3Async(location.BucketName, location.TemplatePath);
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
                var stackName = ToStackName(manifest.ModuleName, instanceName);

                // check version of previously deployed module
                if(!forceDeploy && !await IsValidModuleUpdateAsync(stackName, manifest)) {
                    return false;
                }

                // discover dependencies for deployment
                var dependencies = await DiscoverDependenciesAsync(manifest);
                if(HasErrors) {
                    return false;
                }
                foreach(var dependency in dependencies) {
                    if(!await new ModelUpdater(Settings, dependency.Item2.ToModuleReference()).DeployChangeSetAsync(
                        dependency.Item1,
                        dependency.Item2,
                        ToStackName(dependency.Item1.ModuleName),
                        allowDataLoos,
                        protectStack,

                        // TODO (2018-12-11, bjorg): allow interactive mode to deploy dependencies with parameters
                        new Dictionary<string, string>(),
                        forceDeploy
                    )) {
                        break;
                    }
                }
                return await new ModelUpdater(Settings, moduleReference).DeployChangeSetAsync(
                    manifest,
                    location,
                    stackName,
                    allowDataLoos,
                    protectStack,
                    inputs,
                    forceDeploy
                );
            }
            return true;
        }

        private async Task<bool> IsValidModuleUpdateAsync(string stackName, ModuleManifest manifest) {
            try {
                var describe = await Settings.CfClient.DescribeStacksAsync(new DescribeStacksRequest {
                    StackName = stackName
                });
                var deployedOutputs = describe.Stacks.FirstOrDefault()?.Outputs;
                var deployedName = deployedOutputs?.FirstOrDefault(output => output.OutputKey == "ModuleName")?.OutputValue;
                var deployedVersionText = deployedOutputs?.FirstOrDefault(output => output.OutputKey == "ModuleVersion")?.OutputValue;
                if(deployedName == null) {
                    AddError("unable to determine the name of the deployed module; use --force-deploy to proceed anyway");
                    return false;
                }
                if(deployedName != manifest.ModuleName) {
                    AddError($"deployed module name ({deployedName}) does not match {manifest.ModuleName}; use --force-deploy to proceed anyway");
                    return false;
                }
                if(
                    (deployedVersionText == null)
                    || !VersionInfo.TryParse(deployedVersionText, out VersionInfo deployedVersion)
                ) {
                    AddError("unable to determine the version of the deployed module; use --force-deploy to proceed anyway");
                    return false;
                }
                if(deployedVersion > VersionInfo.Parse(manifest.ModuleVersion)) {
                    AddError($"deployed module version (v{deployedVersionText}) is newer than v{manifest.ModuleVersion}; use --force-deploy to proceed anyway");
                    return false;
                }
            } catch(AmazonCloudFormationException) {

                // stack doesn't exist
            }
            return true;
        }

        private async Task<IEnumerable<Tuple<ModuleManifest, ModuleLocation>>> DiscoverDependenciesAsync(ModuleManifest manifest) {

            // NOTE (2018-12-11, bjorg): the 'Key' is the name of the module that introduced the dependency, or 'null' if already deployed
            var dependencies = new List<KeyValuePair<string, ModuleManifestDependency>>(manifest.Dependencies.Select(d => new KeyValuePair<string, ModuleManifestDependency>(manifest.ModuleName, d)));
            var deployments = new List<Tuple<string, ModuleManifest, ModuleLocation>>();
            var existing = new List<Tuple<string, ModuleManifest, ModuleLocation>>();
            while(dependencies.Any()) {
                var ownedDependency = dependencies[0];
                var dependency = ownedDependency.Value;
                dependencies.RemoveAt(0);

                // check if we have already discovered this dependency
                if(IsDependencyInList(ownedDependency.Key, dependency, existing) || IsDependencyInList(ownedDependency.Key, dependency, deployments))  {
                    continue;
                }

                // check if this dependency needs to be deployed
                var deployed = await FindExistingDependencyAsync(dependency);
                if(deployed != null) {
                    existing.Add(Tuple.Create<string, ModuleManifest, ModuleLocation>(null, null, deployed));
                } else {
                    Console.WriteLine($"=> Resolving dependencies for {dependency.ModuleName}");

                    // resolve dependencies for dependency module
                    var dependencyLocation = await _loader.LocateAsync(dependency.ModuleName, dependency.MinVersion, dependency.MaxVersion, dependency.BucketName);
                    if(dependencyLocation == null) {

                        // error has already been reported
                        continue;
                    }

                    // load manifest of dependency and add its dependencies
                    var dependencyManifest = await _loader.LoadFromS3Async(dependencyLocation.BucketName, dependencyLocation.TemplatePath);
                    if(dependencyManifest == null) {

                        // error has already been reported
                        continue;
                    }
                    deployments.Add(Tuple.Create(ownedDependency.Key, dependencyManifest, dependencyLocation));
                    dependencies.AddRange(dependencyManifest.Dependencies.Select(d => new KeyValuePair<string, ModuleManifestDependency>(ownedDependency.Key, d)));
                }
            }

            // the last discovered deployment is the first one that needs to be deployed
            return deployments.Select(tuple => Tuple.Create(tuple.Item2, tuple.Item3)).Reverse().ToList();
        }

        private bool IsDependencyInList(string owner, ModuleManifestDependency dependency, IEnumerable<Tuple<string, ModuleManifest, ModuleLocation>> modules) {
            var deployed = modules.FirstOrDefault(module => module.Item3.ModuleName == dependency.ModuleName);
            if(deployed.Item3 == null) {
                return false;
            }
            var deployedOwner = (deployed.Item1 == null)
                ? "existing module"
                : $"module '{deployed.Item1}'";

            // confirm that the dependency version is in a valid range
            var deployedVersion = VersionInfo.Parse(deployed.Item3.ModuleVersion);
            if((dependency.MaxVersion != null) && (deployedVersion > dependency.MaxVersion)) {
                AddError($"version conflict for module '{dependency.ModuleName}': module '{owner}' requires max version v{dependency.MaxVersion}, but {deployedOwner} uses v{deployedVersion})");
            }
            if((dependency.MinVersion != null) && (deployedVersion < dependency.MinVersion)) {
                AddError($"version conflict for module '{dependency.ModuleName}': module '{owner}' requires min version v{dependency.MinVersion}, but {deployedOwner} uses v{deployedVersion})");
            }
            return true;
        }

        private async Task<ModuleLocation> FindExistingDependencyAsync(ModuleManifestDependency dependency) {
            try {
                var describe = await Settings.CfClient.DescribeStacksAsync(new DescribeStacksRequest {
                    StackName = ToStackName(dependency.ModuleName)
                });
                var deployedOutputs = describe.Stacks.FirstOrDefault()?.Outputs;
                var deployedName = deployedOutputs?.FirstOrDefault(output => output.OutputKey == "ModuleName")?.OutputValue;
                var deployedVersionText = deployedOutputs?.FirstOrDefault(output => output.OutputKey == "ModuleVersion")?.OutputValue;
                var deployed = new ModuleLocation {
                    ModuleName = deployedName ?? dependency.ModuleName,
                    ModuleVersion = deployedVersionText ?? "0.0",
                    BucketName = null,
                    TemplatePath = null
                };
                if(deployedName == null) {
                    AddError($"unable to determine the name of the deployed dependent module");
                    return deployed;
                }
                if((deployedVersionText == null) || !VersionInfo.TryParse(deployedVersionText, out VersionInfo _)) {
                    AddError($"unable to determine the version of the deployed dependent module");
                    return deployed;
                }

                // confirm that the module name matches
                if(deployed.ModuleName != dependency.ModuleName) {
                    AddError($"deployed dependent module name ({deployed.ModuleName}) does not match {dependency.ModuleName}");
                    return deployed;
                }

                // confirm that the module version is in a valid range
                var deployedVersion = VersionInfo.Parse(deployed.ModuleVersion);
                if((dependency.MaxVersion != null) && (deployedVersion > dependency.MaxVersion)) {
                    AddError($"deployed dependent module version (v{deployedVersion}) is newer than max version constraint v{dependency.MaxVersion}");
                    return deployed;
                }
                if((dependency.MinVersion != null) && (deployedVersion < dependency.MinVersion)) {
                    AddError($"deployed dependent module version (v{deployedVersion}) is older than min version constraint v{dependency.MinVersion}");
                    return deployed;
                }
                return deployed;
            } catch(AmazonCloudFormationException) {

                // stack doesn't exist
                return null;
            }
        }

        private string ToStackName(string moduleName, string instanceName = null) => $"{Settings.Tier}-{instanceName ?? moduleName}";
    }
}