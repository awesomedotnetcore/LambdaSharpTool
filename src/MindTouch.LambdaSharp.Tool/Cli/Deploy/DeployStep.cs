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

namespace MindTouch.LambdaSharp.Tool.Cli.Deploy {

    public class DeployStep : AModelProcessor {

        //--- Types ---
        private class DependencyRecord {

            //--- Properties ---
            public string Owner { get; set; }
            public ModuleManifest Manifest { get; set; }
            public ModuleLocation Location { get; set; }
        }


        //--- Fields ---
        private ModelManifestLoader _loader;

        //--- Constructors ---
        public DeployStep(Settings settings, string sourceFilename) : base(settings, sourceFilename) {
            _loader = new ModelManifestLoader(Settings, sourceFilename);
        }

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
            Console.WriteLine($"Resolving module reference: {moduleReference}");

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
                if(!forceDeploy) {
                    Console.WriteLine($"=> Validating module for deployment tier");
                    if(!await IsValidModuleUpdateAsync(stackName, manifest)) {
                        return false;
                    }
                }

                // discover dependencies for deployment
                IEnumerable<Tuple<ModuleManifest, ModuleLocation>> dependencies = Enumerable.Empty<Tuple<ModuleManifest, ModuleLocation>>();
                if(manifest.Dependencies.Any()) {
                    dependencies = await DiscoverDependenciesAsync(manifest);
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
                            new Dictionary<string, string>()
                        )) {
                            return false;
                        }
                    }
                }
                return await new ModelUpdater(Settings, moduleReference).DeployChangeSetAsync(
                    manifest,
                    location,
                    stackName,
                    allowDataLoos,
                    protectStack,
                    inputs
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
            var deployments = new List<DependencyRecord>();
            var existing = new List<DependencyRecord>();
            var inProgress = new List<DependencyRecord>();

            // create a topological sort of dependencies
            await Recurse(manifest);
            return deployments.Select(tuple => Tuple.Create(tuple.Manifest, tuple.Location)).ToList();

            // local functions
            async Task Recurse(ModuleManifest current) {
                foreach(var dependency in current.Dependencies) {

                    // check if we have already discovered this dependency
                    if(IsDependencyInList(current.ModuleName, dependency, existing) || IsDependencyInList(current.ModuleName, dependency, deployments))  {
                        continue;
                    }

                    // check if this dependency needs to be deployed
                    var deployed = await FindExistingDependencyAsync(dependency);
                    if(deployed != null) {
                        existing.Add(new DependencyRecord {
                            Location = deployed
                        });
                    } else if(inProgress.Any(d => d.Manifest.ModuleName == dependency.ModuleName)) {

                        // circular dependency detected
                        AddError($"circular dependency detected: {string.Join(" -> ", inProgress.Select(d => d.Manifest.ModuleName))}");
                        return;
                    } else {

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
                        var nestedDependency = new DependencyRecord {
                            Owner = current.ModuleName,
                            Manifest = dependencyManifest,
                            Location = dependencyLocation
                        };

                        // keep marker for in-progress resolutions so that circular errors can be detected
                        inProgress.Add(nestedDependency);
                        await Recurse(dependencyManifest);
                        inProgress.Remove(nestedDependency);

                        // append dependency now that all nested dependencies have been resolved
                        Console.WriteLine($"=> Resolved dependency '{dependency.ModuleName}' to module reference: {dependencyLocation}");
                        deployments.Add(nestedDependency);
                    }
                }
            }
        }

        private bool IsDependencyInList(string owner, ModuleManifestDependency dependency, IEnumerable<DependencyRecord> modules) {
            var deployed = modules.FirstOrDefault(module => module.Location.ModuleName == dependency.ModuleName);
            if(deployed == null) {
                return false;
            }
            var deployedOwner = (deployed.Owner == null)
                ? "existing module"
                : $"module '{deployed.Owner}'";

            // confirm that the dependency version is in a valid range
            var deployedVersion = deployed.Location.ModuleVersion;
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
                    ModuleVersion = VersionInfo.Parse(deployedVersionText ?? "0.0"),
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
                var deployedVersion = deployed.ModuleVersion;
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