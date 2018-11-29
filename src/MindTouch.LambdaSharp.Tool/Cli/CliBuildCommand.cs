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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Amazon.S3.Model;
using Amazon.SimpleSystemsManagement;
using Humidifier.Json;
using McMaster.Extensions.CommandLineUtils;
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MindTouch.LambdaSharp.Tool.Cli {

    public class CliBuildPublishDeployCommand : ACliCommand {

        //--- Class Methods ---
        public static CommandOption AddSkipAssemblyValidationOption(CommandLineApplication cmd)
            => cmd.Option("--skip-assembly-validation", "(optional) Disable validating LambdaSharp assembly references in function project files", CommandOptionType.NoValue);

        public static CommandOption AddBuildConfigurationOption(CommandLineApplication cmd)
            => cmd.Option("-c|--configuration <CONFIGURATION>", "(optional) Build configuration for function projects (default: \"Release\")", CommandOptionType.SingleValue);

        public static CommandOption AddGitShaOption(CommandLineApplication cmd)
            => cmd.Option("--gitsha <VALUE>", "(optional) GitSha of most recent git commit (default: invoke `git rev-parse HEAD` command)", CommandOptionType.SingleValue);

        public static CommandOption AddOutputPathOption(CommandLineApplication cmd)
            => cmd.Option("-o|--output <DIRECTORY>", "(optional) Path to output directory (default: bin)", CommandOptionType.SingleValue);

        public static CommandOption AddSelectorOption(CommandLineApplication cmd)
            => cmd.Option("--selector <NAME>", "(optional) Selector for resolving conditional compilation choices in module", CommandOptionType.SingleValue);

        public static CommandOption AddCloudFormationOutputOption(CommandLineApplication cmd)
            => cmd.Option("--cf-output <FILE>", "(optional) Name of generated CloudFormation template file (default: bin/cloudformation.json)", CommandOptionType.SingleValue);

        public static CommandOption AddDryRunOption(CommandLineApplication cmd)
            => cmd.Option("--dryrun:<LEVEL>", "(optional) Generate output assets without deploying (0=everything, 1=cloudformation)", CommandOptionType.SingleOrNoValue);

        public static Dictionary<string, string> ReadInputParametersFiles(Settings settings, string filename) {
            if(!File.Exists(filename)) {
                AddError("cannot find inputs file");
                return null;
            }
            switch(Path.GetExtension(filename).ToLowerInvariant()) {
            case ".yml":
            case ".yaml":
                try {
                    var inputs = new DeserializerBuilder()
                        .WithNamingConvention(new PascalCaseNamingConvention())
                        .Build()
                        .Deserialize<Dictionary<string, object>>(File.ReadAllText(filename));

                    // resolve 'alias/' key names to key arns
                    if(inputs.TryGetValue("Secrets", out object keys)) {
                        if(keys is string key) {
                            inputs["Secrets"] = key.Split(',').Select(item => ConvertAliasToKeyArn(item.Trim())).ToList();
                        } else if(keys is IList<object> list) {
                            inputs["Secrets"] = list.Select(item => ConvertAliasToKeyArn(item as string)).ToList();
                        }

                        // assume key name is an alias and resolve it to its ARN
                        string ConvertAliasToKeyArn(string keyId) {
                            if(keyId == null) {
                                return null;
                            }
                            if(keyId.StartsWith("arn:")) {
                                return keyId;
                            }
                            if(keyId.StartsWith("alias/", StringComparison.Ordinal)) {
                                try {
                                    return settings.KmsClient.DescribeKeyAsync(keyId).Result.KeyMetadata.Arn;
                                } catch(Exception e) {
                                    AddError($"failed to resolve key alias: {keyId}", e);
                                    return null;
                                }
                            }
                            try {
                                return settings.KmsClient.DescribeKeyAsync($"alias/{keyId}").Result.KeyMetadata.Arn;
                            } catch(Exception e) {
                                AddError($"failed to resolve key alias: {keyId}", e);
                                return null;
                            }
                        }
                    }

                    // create final dictionary of input values
                    var result = inputs.ToDictionary(
                        kv => kv.Key.Replace("::", ""),
                        kv => (kv.Value is string text)
                            ? text
                            : string.Join(",", (IList<object>)kv.Value)
                    );
                    return result;
                } catch(YamlDotNet.Core.YamlException e) {
                    AddError($"parsing error near {e.Message}");
                } catch(Exception e) {
                    AddError(e);
                }
                return null;
            default:
                AddError("incompatible inputs file format");
                return null;
            }
        }

        //--- Methods ---
        public void Register(CommandLineApplication app) {

            // NOTE (2018-10-16, bjorg): we're keeping the build/publish/deploy commands in a single
            //  class to make it easier to chain these commands consistently.

            // add 'build' command
            app.Command("build", cmd => {
                cmd.HelpOption();
                cmd.Description = "Build LambdaSharp module";

                // build options
                var modulesArgument = cmd.Argument("<NAME>", "(optional) Path to module definition/folder (default: Module.yml)", multipleValues: true);
                var skipAssemblyValidationOption = AddSkipAssemblyValidationOption(cmd);
                var buildConfigurationOption = AddBuildConfigurationOption(cmd);
                var gitShaOption = AddGitShaOption(cmd);
                var outputDirectoryOption = AddOutputPathOption(cmd);
                var selectorOption = AddSelectorOption(cmd);
                var outputCloudFormationFilePathOption = AddCloudFormationOutputOption(cmd);

                // misc options
                var dryRunOption = AddDryRunOption(cmd);
                var initSettingsCallback = CreateSettingsInitializer(cmd, requireAwsProfile: false, requireDeploymentTier: false);
                cmd.OnExecute(async () => {
                    Console.WriteLine($"{app.FullName} - {cmd.Description}");

                    // read settings and validate them
                    var settings = await initSettingsCallback();
                    if(settings == null) {
                        return;
                    }
                    DryRunLevel? dryRun = null;
                    if(dryRunOption.HasValue()) {
                        DryRunLevel value;
                        if(!TryParseEnumOption(dryRunOption, DryRunLevel.Everything, DryRunLevel.Everything, out value)) {

                            // NOTE (2018-08-04, bjorg): no need to add an error message since it's already added by `TryParseEnumOption`
                            return;
                        }
                        dryRun = value;
                    }

                    // check if one or more arguments have been specified
                    var arguments = modulesArgument.Values.Any()
                        ? modulesArgument.Values
                        : new List<string> { Directory.GetCurrentDirectory() };

                    // run build step
                    foreach(var argument in arguments) {
                        string moduleSource;
                        if(Directory.Exists(argument)) {

                            // append default module filename
                            moduleSource = Path.Combine(Path.GetFullPath(argument), "Module.yml");
                        } else {
                            moduleSource = Path.GetFullPath(argument);
                        }
                        settings.WorkingDirectory = Path.GetDirectoryName(moduleSource);
                        settings.OutputDirectory = outputDirectoryOption.HasValue()
                            ? Path.GetFullPath(outputDirectoryOption.Value())
                            : Path.Combine(settings.WorkingDirectory, "bin");
                        if(!await BuildStepAsync(
                            settings,
                            outputCloudFormationFilePathOption.Value() ?? Path.Combine(settings.OutputDirectory, "cloudformation.json"),
                            skipAssemblyValidationOption.HasValue(),
                            dryRun == DryRunLevel.CloudFormation,
                            gitShaOption.Value() ?? GetGitShaValue(settings.WorkingDirectory),
                            buildConfigurationOption.Value() ?? "Release",
                            selectorOption.Value(),
                            moduleSource
                        )) {
                            break;
                        }
                    }
                });
            });

            // add 'publish' command
            app.Command("publish", cmd => {
                cmd.HelpOption();
                cmd.Description = "Publish LambdaSharp module";

                // build options
                var compiledModulesArgument = cmd.Argument("<NAME>", "(optional) Path to assets folder or module definition/folder (default: Module.yml)", multipleValues: true);
                var skipAssemblyValidationOption = AddSkipAssemblyValidationOption(cmd);
                var buildConfigurationOption = AddBuildConfigurationOption(cmd);
                var gitShaOption = AddGitShaOption(cmd);
                var outputDirectoryOption = AddOutputPathOption(cmd);
                var selectorOption = AddSelectorOption(cmd);
                var outputCloudFormationFilePathOption = AddCloudFormationOutputOption(cmd);

                // misc options
                var dryRunOption = AddDryRunOption(cmd);
                var initSettingsCallback = CreateSettingsInitializer(cmd, requireDeploymentTier: false);
                cmd.OnExecute(async () => {
                    Console.WriteLine($"{app.FullName} - {cmd.Description}");

                    // read settings and validate them
                    var settings = await initSettingsCallback();
                    if(settings == null) {
                        return;
                    }
                    DryRunLevel? dryRun = null;
                    if(dryRunOption.HasValue()) {
                        DryRunLevel value;
                        if(!TryParseEnumOption(dryRunOption, DryRunLevel.Everything, DryRunLevel.Everything, out value)) {

                            // NOTE (2018-08-04, bjorg): no need to add an error message since it's already added by `TryParseEnumOption`
                            return;
                        }
                        dryRun = value;
                    }

                    // check if one or more arguments have been specified
                    var arguments = compiledModulesArgument.Values.Any()
                        ? compiledModulesArgument.Values
                        : new List<string> { Directory.GetCurrentDirectory() };

                    // run build & publish steps
                    foreach(var argument in arguments) {
                        string moduleSource = null;
                        if(Directory.Exists(argument)) {

                            // check if argument is pointing to a folder containing a cloudformation file
                            if(File.Exists(Path.Combine(argument, "cloudformation.json"))) {
                                settings.WorkingDirectory = Path.GetFullPath(argument);
                                settings.OutputDirectory = settings.WorkingDirectory;
                            } else {
                                moduleSource = Path.Combine(Path.GetFullPath(argument), "Module.yml");
                            }
                        } else if((Path.GetExtension(argument) == ".yml") || (Path.GetExtension(argument) == ".yaml")) {
                            moduleSource = Path.GetFullPath(argument);
                        } else if(Path.GetFileName(argument) == "cloudformation.json") {
                            settings.WorkingDirectory = Path.GetDirectoryName(argument);
                            settings.OutputDirectory = settings.WorkingDirectory;
                        } else {
                            AddError($"unrecognized argument: {argument}");
                            break;
                        }
                        if(moduleSource != null) {
                            settings.WorkingDirectory = Path.GetDirectoryName(moduleSource);
                            settings.OutputDirectory = outputDirectoryOption.HasValue()
                                ? Path.GetFullPath(outputDirectoryOption.Value())
                                : Path.Combine(settings.WorkingDirectory, "bin");
                            if(!await BuildStepAsync(
                                settings,
                                outputCloudFormationFilePathOption.Value() ?? Path.Combine(settings.OutputDirectory, "cloudformation.json"),
                                skipAssemblyValidationOption.HasValue(),
                                dryRun == DryRunLevel.CloudFormation,
                                gitShaOption.Value() ?? GetGitShaValue(settings.WorkingDirectory),
                                buildConfigurationOption.Value() ?? "Release",
                                selectorOption.Value(),
                                moduleSource
                            )) {
                                break;
                            }
                        }
                        if(dryRun == null) {
                            if(await PublishStepAsync(settings) == null) {
                                break;
                            }
                        }
                    }
                });
            });

            // add 'deploy' command
            app.Command("deploy", cmd => {
                cmd.HelpOption();
                cmd.Description = "Deploy LambdaSharp module";

                // deploy options
                var publishedModulesArgument = cmd.Argument("<NAME>", "(optional) Published module name, or path to assets folder, or module definition/folder (default: Module.yml)", multipleValues: true);
                var alternativeNameOption = cmd.Option("--name <NAME>", "(optional) Specify an alternative module name for the deployment (default: module name)", CommandOptionType.SingleValue);
                var inputsFileOption = cmd.Option("--inputs|-I <FILE>", "(optional) Specify filename to read module inputs from (default: none)", CommandOptionType.SingleValue);
                var inputOption = cmd.Option("--input|-KV <KEY>=<VALUE>", "(optional) Specify module input key-value pair (can be used multiple times)", CommandOptionType.MultipleValue);
                var allowDataLossOption = cmd.Option("--allow-data-loss", "(optional) Allow CloudFormation resource update operations that could lead to data loss", CommandOptionType.NoValue);
                var protectStackOption = cmd.Option("--protect", "(optional) Enable termination protection for the deployed module", CommandOptionType.NoValue);
                var forceDeployOption = cmd.Option("--force-deploy", "(optional) Force module deployment", CommandOptionType.NoValue);

                // build options
                var skipAssemblyValidationOption = AddSkipAssemblyValidationOption(cmd);
                var buildConfigurationOption = AddBuildConfigurationOption(cmd);
                var gitShaOption = AddGitShaOption(cmd);
                var outputDirectoryOption = AddOutputPathOption(cmd);
                var selectorOption = AddSelectorOption(cmd);

                // misc options
                var dryRunOption = AddDryRunOption(cmd);
                var outputCloudFormationFilePathOption = AddCloudFormationOutputOption(cmd);
                var initSettingsCallback = CreateSettingsInitializer(cmd);
                cmd.OnExecute(async () => {
                    Console.WriteLine($"{app.FullName} - {cmd.Description}");

                    // read settings and validate them
                    var settings = await initSettingsCallback();
                    if(settings == null) {
                        return;
                    }
                    DryRunLevel? dryRun = null;
                    if(dryRunOption.HasValue()) {
                        DryRunLevel value;
                        if(!TryParseEnumOption(dryRunOption, DryRunLevel.Everything, DryRunLevel.Everything, out value)) {

                            // NOTE (2018-08-04, bjorg): no need to add an error message since it's already added by `TryParseEnumOption`
                            return;
                        }
                        dryRun = value;
                    }

                    // reading module inputs
                    var inputs = new Dictionary<string, string>();
                    if(inputsFileOption.HasValue()) {
                        inputs = ReadInputParametersFiles(settings, inputsFileOption.Value());
                        if(HasErrors) {
                            return;
                        }
                    }
                    foreach(var inputKeyValue in inputOption.Values) {
                        var keyValue = inputKeyValue.Split('=', 2);
                        if(keyValue.Length != 2) {
                            AddError($"bad format for input parameter: {inputKeyValue}");
                        } else {
                            inputs[keyValue[0]] = keyValue[1];
                        }
                    }
                    if(HasErrors) {
                        return;
                    }

                    // check if one or more arguments have been specified
                    var arguments = publishedModulesArgument.Values.Any()
                        ? publishedModulesArgument.Values
                        : new List<string> { Directory.GetCurrentDirectory() };
                    Console.WriteLine($"Readying module for deployment tier '{settings.Tier}'");
                    foreach(var argument in arguments) {
                        string moduleKey = null;
                        string moduleSource = null;
                        if(Directory.Exists(argument)) {

                            // check if argument is pointing to a folder containing a cloudformation file
                            if(File.Exists(Path.Combine(argument, "cloudformation.json"))) {
                                settings.WorkingDirectory = Path.GetFullPath(argument);
                                settings.OutputDirectory = settings.WorkingDirectory;
                            } else {
                                moduleSource = Path.Combine(Path.GetFullPath(argument), "Module.yml");
                            }
                        } else if((Path.GetExtension(argument) == ".yml") || (Path.GetExtension(argument) == ".yaml")) {
                            moduleSource = Path.GetFullPath(argument);
                        } else if(Path.GetFileName(argument) == "cloudformation.json") {
                            settings.WorkingDirectory = Path.GetDirectoryName(argument);
                            settings.OutputDirectory = settings.WorkingDirectory;
                        } else {
                            moduleKey = argument;
                        }
                        if(moduleSource != null) {
                            settings.WorkingDirectory = Path.GetDirectoryName(moduleSource);
                            settings.OutputDirectory = outputDirectoryOption.HasValue()
                                ? Path.GetFullPath(outputDirectoryOption.Value())
                                : Path.Combine(settings.WorkingDirectory, "bin");
                            if(!await BuildStepAsync(
                                settings,
                                outputCloudFormationFilePathOption.Value() ?? Path.Combine(settings.OutputDirectory, "cloudformation.json"),
                                skipAssemblyValidationOption.HasValue(),
                                dryRun == DryRunLevel.CloudFormation,
                                gitShaOption.Value() ?? GetGitShaValue(settings.WorkingDirectory),
                                buildConfigurationOption.Value() ?? "Release",
                                selectorOption.Value(),
                                moduleSource
                            )) {
                                break;
                            }
                        }
                        if(dryRun == null) {
                            if(moduleKey == null) {
                                moduleKey = await PublishStepAsync(settings);
                                if(moduleKey == null) {
                                    break;
                                }
                            }
                            if(!await DeployStepAsync(
                                settings,
                                dryRun,
                                moduleKey,
                                alternativeNameOption.Value(),
                                allowDataLossOption.HasValue(),
                                protectStackOption.HasValue(),
                                inputs,
                                forceDeployOption.HasValue()
                            )) {
                                break;
                            }
                        }
                    }
                });
            });
        }

        public async Task<bool> BuildStepAsync(
            Settings settings,
            string outputCloudFormationFilePath,
            bool skipAssemblyValidation,
            bool skipFunctionBuild,
            string gitsha,
            string buildConfiguration,
            string selector,
            string moduleSource
        ) {
            try {
                if(!File.Exists(moduleSource)) {
                    AddError($"could not find '{moduleSource}'");
                    return false;
                }

                // delete output files
                File.Delete(Path.Combine(settings.OutputDirectory, "manifest.json"));
                File.Delete(outputCloudFormationFilePath);

                // read input file
                Console.WriteLine();
                Console.WriteLine($"Compiling module: {Path.GetRelativePath(Directory.GetCurrentDirectory(), moduleSource)}");
                var source = await File.ReadAllTextAsync(moduleSource);

                // parse yaml to module AST
                var moduleAst = new ModelParser(settings, moduleSource).Parse(source, selector);
                if(HasErrors) {
                    return false;
                }

                // validate module AST
                new ModelValidation(settings, moduleSource).Process(moduleAst);
                if(HasErrors) {
                    return false;
                }

                // convert module AST to model
                var module = new ModelConverter(settings, moduleSource).Process(moduleAst);
                if(HasErrors) {
                    return false;
                }

                // package all functions
                new ModelFunctionPackager(settings, moduleSource).Process(
                    module,
                    skipCompile: skipFunctionBuild,
                    skipAssemblyValidation: skipAssemblyValidation,
                    gitsha: gitsha,
                    buildConfiguration: buildConfiguration
                );

                // package all files
                new ModelFilesPackager(settings, moduleSource).Process(module);

                // augment module definitions
                new ModelAugmenter(settings, moduleSource).Augment(module);
                if(HasErrors) {
                    return false;
                }

                // resolve all references
                new ModelReferenceResolver(settings, moduleSource).Resolve(module);
                if(HasErrors) {
                    return false;
                }

                // create folder for cloudformation output
                var outputCloudFormationDirectory = Path.GetDirectoryName(outputCloudFormationFilePath);
                if(outputCloudFormationDirectory != "") {
                    Directory.CreateDirectory(outputCloudFormationDirectory);
                }

// TODO: make this optional
// var moduleJson = Path.ChangeExtension(outputCloudFormationFilePath, null) + "-module.json";
// File.WriteAllText(moduleJson, JsonConvert.SerializeObject(module, Formatting.Indented));
// Console.WriteLine($"{moduleJson} generated");

                // generate & save cloudformation template
                var template = new ModelGenerator(settings, moduleSource).Generate(module.ToModule(), gitsha);
                if(HasErrors) {
                    return false;
                }
                File.WriteAllText(outputCloudFormationFilePath, template);
                Console.WriteLine("=> Module compilation done");
                return true;
            } catch(Exception e) {
                AddError(e);
                return false;
            }
        }

        public async Task<string> PublishStepAsync(Settings settings) {
            var cloudformationFile = Path.Combine(settings.OutputDirectory, "cloudformation.json");
            if(!File.Exists(cloudformationFile)) {
                AddError("folder does not contain a CloudFormation file for publishing");
                return null;
            }
            // load cloudformation file
            var cloudformationText = File.ReadAllText(cloudformationFile);
            var cloudformation = JsonConvert.DeserializeObject<JObject>(cloudformationText);
            var manifest = GetManifest(cloudformation);
            if(manifest == null) {
                AddError("CloudFormation file does not contain a LambdaSharp manifest");
                return null;
            }
            if(manifest.Version != ModuleManifest.CurrentVersion) {
                AddError($"Incompatible LambdaSharp manifest version (found: {manifest.Version ?? "<null>"}, expected: {ModuleManifest.CurrentVersion})");
                return null;
            }
            await PopulateToolSettingsAsync(settings);
            if(HasErrors) {
                return null;
            }

            // make sure there is a deployment bucket
            if(settings.DeploymentBucketName == null) {
                AddError("missing deployment bucket", new LambdaSharpToolConfigException(settings.ToolProfile));
                return null;
            }

            // publish module
            return await new ModelPublisher(settings, cloudformationFile).PublishAsync(manifest);
        }

        public async Task<bool> DeployStepAsync(
            Settings settings,
            DryRunLevel? dryRun,
            string moduleKey,
            string instanceName,
            bool allowDataLoos,
            bool protectStack,
            Dictionary<string, string> inputs,
            bool forceDeploy
        ) {
            await PopulateToolSettingsAsync(settings);
            if(HasErrors) {
                return false;
            }
            await PopulateRuntimeSettingsAsync(settings);
            if(HasErrors) {
                return false;
            }

            // module key formats
            // * MODULENAME:VERSION
            // * s3://bucket-name/Modules/{ModuleName}/{Version}/

            var originalDeploymentBucketName = settings.DeploymentBucketName;
            try {
                string cloudformationPath = null;
                if(moduleKey.StartsWith("s3://", StringComparison.Ordinal)) {
                    var uri = new Uri(moduleKey);
                    settings.DeploymentBucketName = uri.Host;

                    // absolute path always starts with '/', which needs to be removed
                    var path = uri.AbsolutePath.Substring(1);
                    if(!path.EndsWith("/cloudformation.json", StringComparison.Ordinal)) {
                        cloudformationPath = path.TrimEnd('/') + "/cloudformation.json";
                    } else {
                        cloudformationPath = path;
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
                    var foundVersion = await new[] {
                        settings.DeploymentBucketName,
                        $"lambdasharp-{settings.AwsRegion}"
                    }.Select(async bucket => {
                        settings.DeploymentBucketName = bucket;
                        return await FindNewestVersion(settings, moduleName, requestedVersion);
                    }).FirstOrDefault();
                    if(foundVersion == null) {
                        AddError($"could not find module: {moduleName} (v{requestedVersion})");
                        return false;
                    }
                    cloudformationPath = $"Modules/{moduleName}/Versions/{foundVersion}/cloudformation.json";
                }

                // download manifest
                var cloudformationText = await GetS3ObjectContents(settings, cloudformationPath);
                if(cloudformationText == null) {
                    AddError($"could not load CloudFormation template from s3://{settings.DeploymentBucketName}/{cloudformationPath}");
                    return false;
                }
                var cloudformation = JsonConvert.DeserializeObject<JObject>(cloudformationText);
                var manifest = GetManifest(cloudformation);
                if(manifest == null) {
                    AddError("CloudFormation file does not contain a LambdaSharp manifest");
                    return false;
                }
                if(manifest.Version != ModuleManifest.CurrentVersion) {
                    AddError($"Incompatible LambdaSharp manifest version (found: {manifest.Version ?? "<null>"}, expected: {ModuleManifest.CurrentVersion})");
                    return false;
                }

                // check that the LambdaSharp runtime & CLI versions match
                if(settings.RuntimeVersion == null) {

                    // runtime module doesn't expect a deployment tier to exist
                    if(!forceDeploy && !manifest.HasPragma("no-runtime-version-check")) {
                        AddError("could not determine the LambdaSharp runtime version; use --force-deploy to proceed anyway", new LambdaSharpDeploymentTierSetupException(settings.Tier));
                        return false;
                    }
                } else if(!settings.ToolVersion.IsCompatibleWith(settings.RuntimeVersion)) {
                    if(!forceDeploy) {
                        AddError($"LambdaSharp CLI (v{settings.ToolVersion}) and runtime (v{settings.RuntimeVersion}) versions do not match; use --force-deploy to proceed anyway");
                        return false;
                    }
                }

                // deploy module
                if(dryRun == null) {
                    try {
                        return await new ModelUpdater(settings, sourceFilename: null).DeployChangeSetAsync(
                            manifest,
                            cloudformationPath,
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
            } finally {
                settings.DeploymentBucketName = originalDeploymentBucketName;
            }
        }

        private async Task<VersionInfo> FindNewestVersion(Settings settings, string moduleName, VersionInfo requestedVersion) {

            // enumerate versions in bucket
            var versions = new List<VersionInfo>();
            var request = new ListObjectsV2Request {
                BucketName = settings.DeploymentBucketName,
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

        private async Task<string> GetS3ObjectContents(Settings settings, string key) {
            try {
                var response = await settings.S3Client.GetObjectAsync(new GetObjectRequest {
                    BucketName = settings.DeploymentBucketName,
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
