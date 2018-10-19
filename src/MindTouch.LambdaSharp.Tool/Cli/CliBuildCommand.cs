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
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MindTouch.LambdaSharp.Tool.Cli {

    public class CliBuildPublishDeployCommand : ACliCommand {

        //--- Methods ---
        public void Register(CommandLineApplication app) {

            // NOTE (2018-10-16, bjorg): we're keeping the build/publish/deploy commands in a single
            //  class to make it easier to chain these commands consistently.

            // add 'build' command
            app.Command("build", cmd => {
                cmd.HelpOption();
                cmd.Description = "Build LambdaSharp module";

                // build options
                var modulesArgument = cmd.Argument("<NAME>", "(optional) Path to module file/folder (default: Module.yml)", multipleValues: true);
                var skipFunctionBuildOption = cmd.Option("--skip-function-build", "(optional) Do not build the function projects", CommandOptionType.NoValue);
                var skipAssemblyValidationOption = cmd.Option("--skip-assembly-validation", "(optional) Disable validating LambdaSharp assembly references in function project files", CommandOptionType.NoValue);
                var buildConfigurationOption = cmd.Option("-c|--configuration <CONFIGURATION>", "(optional) Build configuration for function projects (default: \"Release\")", CommandOptionType.SingleValue);
                var gitShaOption = cmd.Option("--gitsha <VALUE>", "(optional) GitSha of most recent git commit (default: invoke `git rev-parse HEAD` command)", CommandOptionType.SingleValue);
                var outputDirectoryOption = cmd.Option("-o|--output <DIRECTORY>", "(optional) Path to output directory (default: bin)", CommandOptionType.SingleValue);
                var selectorOption = cmd.Option("--selector <NAME>", "(optional) Selector for resolving conditional compilation choices in module", CommandOptionType.SingleValue);
                var outputCloudFormationFilePathOption = cmd.Option("--cf-output <FILE>", "(optional) Name of generated CloudFormation template file (default: bin/cloudformation.json)", CommandOptionType.SingleValue);

                // misc options
                var dryRunOption = cmd.Option("--dryrun:<LEVEL>", "(optional) Generate output assets without deploying (0=everything, 1=cloudformation)", CommandOptionType.SingleOrNoValue);
                var initSettingsCallback = CreateSettingsInitializer(cmd, requireAwsProfile: false);
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
                            skipFunctionBuildOption.HasValue() || (dryRun == DryRunLevel.CloudFormation),
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
                var compiledModulesArgument = cmd.Argument("<NAME>", "(optional) Path to assets folder or module file/folder (default: Module.yml)", multipleValues: true);
                var skipFunctionBuildOption = cmd.Option("--skip-function-build", "(optional) Do not build the function projects", CommandOptionType.NoValue);
                var skipAssemblyValidationOption = cmd.Option("--skip-assembly-validation", "(optional) Disable validating LambdaSharp assembly references in function project files", CommandOptionType.NoValue);
                var buildConfigurationOption = cmd.Option("-c|--configuration <CONFIGURATION>", "(optional) Build configuration for function projects (default: \"Release\")", CommandOptionType.SingleValue);
                var gitShaOption = cmd.Option("--gitsha <VALUE>", "(optional) GitSha of most recent git commit (default: invoke `git rev-parse HEAD` command)", CommandOptionType.SingleValue);
                var outputDirectoryOption = cmd.Option("-o|--output <DIRECTORY>", "(optional) Path to output directory (default: bin)", CommandOptionType.SingleValue);
                var selectorOption = cmd.Option("--selector <NAME>", "(optional) Selector for resolving conditional compilation choices in module", CommandOptionType.SingleValue);

                // misc options
                var dryRunOption = cmd.Option("--dryrun:<LEVEL>", "(optional) Generate output assets without deploying (0=everything, 1=cloudformation)", CommandOptionType.SingleOrNoValue);
                var outputCloudFormationFilePathOption = cmd.Option("--cf-output <FILE>", "(optional) Name of generated CloudFormation template file (default: bin/cloudformation.json)", CommandOptionType.SingleValue);
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

                    // check if one or more arguments have been specified
                    var arguments = compiledModulesArgument.Values.Any()
                        ? compiledModulesArgument.Values
                        : new List<string> { Directory.GetCurrentDirectory() };

                    // run build & publish steps
                    foreach(var argument in arguments) {
                        string moduleSource = null;
                        if(Directory.Exists(argument)) {

                            // check if argument is pointing to a folder containing a module definition
                            if(File.Exists(Path.Combine(argument, "manifest.json"))) {
                                settings.WorkingDirectory = Path.GetFullPath(argument);
                                settings.OutputDirectory = settings.WorkingDirectory;
                            } else {
                                moduleSource = Path.Combine(Path.GetFullPath(argument), "Module.yml");
                            }
                        } else if((Path.GetExtension(argument) == ".yml") || (Path.GetExtension(argument) == ".yaml")) {
                            moduleSource = Path.GetFullPath(argument);
                        } else if(Path.GetFileName(argument) == "manifest.json") {
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
                                skipFunctionBuildOption.HasValue() || (dryRun == DryRunLevel.CloudFormation),
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
                var publishedModulesArgument = cmd.Argument("<NAME>", "(optional) Published module name, or path to assets folder, or module file/folder (default: Module.yml)", multipleValues: true);
                var altModuleNameOption = cmd.Option("--name", "(optional) Specify an alternate module name for the deployment (default: module name)", CommandOptionType.SingleOrNoValue);
                var tierOption = cmd.Option("--tier|-T <NAME>", "(optional) Name of deployment tier (default: LAMBDASHARP_TIER environment variable)", CommandOptionType.SingleValue);
                var inputsFileOption = cmd.Option("--inputs|-I <FILE>", "(optional) Specify module inputs (default: none)", CommandOptionType.SingleValue);
                var inputOption = cmd.Option("--input|-KV <KEY>=<VALUE>", "(optional) Specify module input (can be used multiple times)", CommandOptionType.MultipleValue);
                var allowDataLossOption = cmd.Option("--allow-data-loss", "(optional) Allow CloudFormation resource update operations that could lead to data loss", CommandOptionType.NoValue);
                var protectStackOption = cmd.Option("--protect", "(optional) Enable termination protection for the deployed module", CommandOptionType.NoValue);
                var forceDeployOption = cmd.Option("--force-deploy", "(optional) Force module deployment", CommandOptionType.NoValue);

                // build options
                var skipFunctionBuildOption = cmd.Option("--skip-function-build", "(optional) Do not build the function projects", CommandOptionType.NoValue);
                var skipAssemblyValidationOption = cmd.Option("--skip-assembly-validation", "(optional) Disable validating LambdaSharp assembly references in function project files", CommandOptionType.NoValue);
                var buildConfigurationOption = cmd.Option("-c|--configuration <CONFIGURATION>", "(optional) Build configuration for function projects (default: \"Release\")", CommandOptionType.SingleValue);
                var gitShaOption = cmd.Option("--gitsha <VALUE>", "(optional) GitSha of most recent git commit (default: invoke `git rev-parse HEAD` command)", CommandOptionType.SingleValue);
                var outputDirectoryOption = cmd.Option("-o|--output <DIRECTORY>", "(optional) Path to output directory (default: bin)", CommandOptionType.SingleValue);
                var selectorOption = cmd.Option("--selector <NAME>", "(optional) Selector for resolving conditional compilation choices in module", CommandOptionType.SingleValue);

                // misc options
                var dryRunOption = cmd.Option("--dryrun:<LEVEL>", "(optional) Generate output assets without deploying (0=everything, 1=cloudformation)", CommandOptionType.SingleOrNoValue);
                var outputCloudFormationFilePathOption = cmd.Option("--cf-output <FILE>", "(optional) Name of generated CloudFormation template file (default: bin/cloudformation.json)", CommandOptionType.SingleValue);
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

                    // initialize deployment tier value
                    var tier = tierOption.Value() ?? Environment.GetEnvironmentVariable("LAMBDASHARP_TIER");
                    if(string.IsNullOrEmpty(tier)) {
                        AddError("missing deployment tier name");
                        return;
                    }
                    if(tier == "Default") {
                        AddError("deployment tier cannot be 'Default' because it is a reserved name");
                        return;
                    }

                    // check if one or more arguments have been specified
                    var arguments = publishedModulesArgument.Values.Any()
                        ? publishedModulesArgument.Values
                        : new List<string> { Directory.GetCurrentDirectory() };
                    Console.WriteLine($"Readying module for deployment tier '{tier}'");
                    foreach(var argument in arguments) {
                        string moduleKey = null;
                        string moduleSource = null;
                        if(Directory.Exists(argument)) {

                            // check if argument is pointing to a folder containing a module definition
                            if(File.Exists(Path.Combine(argument, "manifest.json"))) {
                                settings.WorkingDirectory = Path.GetFullPath(argument);
                                settings.OutputDirectory = settings.WorkingDirectory;
                            } else {
                                moduleSource = Path.Combine(Path.GetFullPath(argument), "Module.yml");
                            }
                        } else if((Path.GetExtension(argument) == ".yml") || (Path.GetExtension(argument) == ".yaml")) {
                            moduleSource = Path.GetFullPath(argument);
                        } else if(Path.GetFileName(argument) == "manifest.json") {
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
                                skipFunctionBuildOption.HasValue() || (dryRun == DryRunLevel.CloudFormation),
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
                                altModuleNameOption.Value(),
                                allowDataLossOption.HasValue(),
                                protectStackOption.HasValue(),
                                inputs,
                                tier,
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

                // read input file
                Console.WriteLine();
                Console.WriteLine($"Processing module: {moduleSource}");
                var source = await File.ReadAllTextAsync(moduleSource);

                // preprocess file
                var tokenStream = new ModelPreprocessor(settings, moduleSource).Preprocess(source, selector);
                if(HasErrors) {
                    return false;
                }

                // parse yaml module file
                var parsedModule = new ModelParser(settings, moduleSource).Parse(tokenStream);
                if(HasErrors) {
                    return false;
                }

                // validate module
                new ModelValidation(settings, moduleSource).Process(parsedModule);
                if(HasErrors) {
                    return false;
                }

                // TODO (2018-10-04, bjorg): refactor all model processing to use the strict model instead of the parsed model

                // package all functions
                new ModelFunctionPackager(settings, moduleSource).Process(
                    parsedModule,
                    settings.ToolVersion,
                    skipCompile: skipFunctionBuild,
                    skipAssemblyValidation: skipAssemblyValidation,
                    gitsha: gitsha,
                    buildConfiguration: buildConfiguration
                );

                // package all files
                new ModelFilesPackager(settings, moduleSource).Process(parsedModule);

                // compile module file
                var module = new ModelConverter(settings, moduleSource).Process(parsedModule);
                if(HasErrors) {
                    return false;
                }

                // resolve all parameter references
                new ModelReferenceResolver(settings, moduleSource).Resolve(module);
                if(HasErrors) {
                    return false;
                }

                // generate & save cloudformation template
                var template = new ModelGenerator(settings, moduleSource).Generate(module);
                if(HasErrors) {
                    return false;
                }
                var outputCloudFormationDirectory = Path.GetDirectoryName(outputCloudFormationFilePath);
                if(outputCloudFormationDirectory != "") {
                    Directory.CreateDirectory(outputCloudFormationDirectory);
                }
                File.WriteAllText(outputCloudFormationFilePath, template);

                // generate & save module manifest
                var functions = module.Functions
                    .Where(f => f.PackagePath != null)
                    .Select(f => Path.GetRelativePath(settings.OutputDirectory, f.PackagePath))
                    .ToList();
                var packages = module.Parameters.OfType<PackageParameter>()
                    .Select(p => Path.GetRelativePath(settings.OutputDirectory, p.PackagePath))
                    .ToList();
                var manifest = new ModuleManifest {
                    Name = module.Name,
                    Version = module.Version.ToString(),
                    Hash = template.ToMD5Hash(),
                    GitSha = gitsha,
                    Pragmas = module.Pragmas,
                    Template = Path.GetRelativePath(settings.OutputDirectory, outputCloudFormationFilePath),
                    FunctionAssets = functions,
                    PackageAssets = packages
                };
                var manifestFilePath = Path.Combine(settings.OutputDirectory, "manifest.json");
                if(!Directory.Exists(settings.OutputDirectory)) {
                    Directory.CreateDirectory(settings.OutputDirectory);
                }
                File.WriteAllText(manifestFilePath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
                Console.WriteLine("=> Module processing done");
                return true;
            } catch(Exception e) {
                AddError(e);
                return false;
            }
        }

        public async Task<string> PublishStepAsync(Settings settings) {
            var manifestFile = Path.Combine(settings.OutputDirectory, "manifest.json");
            if(!File.Exists(manifestFile)) {
                AddError("folder does not contain a module manifest for publishing");
                return null;
            }
            // load manifest file
            var manifestText = File.ReadAllText(manifestFile);
            var manifest = JsonConvert.DeserializeObject<ModuleManifest>(manifestText);
            await PopulateToolSettingsAsync(settings);

            // make sure there is a deployment bucket
            if(settings.DeploymentBucketName == null) {
                AddError("missing deployment bucket", new LambdaSharpToolConfigException(settings.ToolProfile));
                return null;
            }

            // publish module
            var templateFilePath = manifest.Template;
            return await new ModelPublisher(settings, manifestFile).PublishAsync(manifest);
        }

        public async Task<bool> DeployStepAsync(
            Settings settings,
            DryRunLevel? dryRun,
            string moduleKey,
            string instanceName,
            bool allowDataLoos,
            bool protectStack,
            Dictionary<string, string> inputs,
            string tier,
            bool forceDeploy
        ) {
            await PopulateToolSettingsAsync(settings);
            await PopulateEnvironmentSettingsAsync(settings, tier);

            // module key formats
            // * MODULENAME:VERSION
            // * s3://bucket-name/path/cloudformation.json

            string marker;
            if(moduleKey.StartsWith("s3://", StringComparison.Ordinal)) {
                var uri = new Uri(moduleKey);
                settings.DeploymentBucketName = uri.Host;

                // absolute path always starts with '/', which needs to be removed
                var path = uri.AbsolutePath.Substring(1);
                if(!path.EndsWith(".json", StringComparison.Ordinal)) {
                    marker = await GetS3ObjectContents(settings, path);
                    if(marker == null) {
                        AddError($"could not find module location: {moduleKey})");
                        return false;
                    }
                } else {
                    marker = path;
                }
            } else {
                VersionInfo version = null;
                string moduleName;

                // check if a version suffix is specified
                if(moduleKey.Contains(':')) {
                    var parts = moduleKey.Split(':', 2);
                    moduleName = parts[0];
                    if(parts[1] != "*") {
                        version = VersionInfo.Parse(parts[1]);
                    }
                } else {
                    moduleName = moduleKey;
                }

                // attempt to find the module in the deployment bucket and then the regional lambdasharp bucket
                marker = null;
                foreach(var bucket in new[] {
                    settings.DeploymentBucketName,
                    $"lambdasharp-{settings.AwsRegion}"
                }) {
                    settings.DeploymentBucketName = bucket;
                    if(version == null) {
                        version = await FindNewestVersion(settings, moduleKey);
                        if(HasErrors) {
                            return false;
                        }
                        if(version == null) {
                            continue;
                        }
                    }
                    marker = await GetS3ObjectContents(settings, $"{settings.DeploymentBucketPath}{moduleName}/Versions/{version}");
                    if(marker != null) {
                        break;
                    }
                }
                if(marker == null) {
                    AddError($"could not find module: {moduleName} (v{version})");
                    return false;
                }
            }

            // download manifest
            var manifest = JsonConvert.DeserializeObject<ModuleManifest>(
                await GetS3ObjectContents(settings, marker)
            );

            // bootstrap module doesn't expect an environment to exist
            if(forceDeploy || !manifest.HasPragma("no-environment-check")) {
                if(settings.EnvironmentVersion == null) {
                    AddError("could not determine the LambdaSharp Environment version", new LambdaSharpDeploymentTierSetupException(tier));
                    return false;
                } else {

                    // check that LambdaSharp environment & tool versions match
                    if(settings.EnvironmentVersion != settings.ToolVersion) {
                        AddError($"LambdaSharp tool (v{settings.ToolVersion}) and environment (v{settings.EnvironmentVersion}) versions do not match", new LambdaSharpDeploymentTierSetupException(tier));
                        return false;
                    }
                }
            }

            // deploy module
            if(dryRun == null) {
                try {
                    return await new ModelUpdater(settings, sourceFilename: null).DeployAsync(
                        manifest,
                        instanceName,
                        allowDataLoos,
                        protectStack,
                        inputs,
                        tier,
                        forceDeploy
                    );
                } catch(Exception e) {
                    AddError(e);
                    return false;
                }
            }
            return true;
        }

        private Dictionary<string, string> ReadInputParametersFiles(Settings settings, string filename) {
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
                    if(inputs.TryGetValue("ModuleSecrets", out object keys)) {
                        if(keys is string key) {
                            inputs["ModuleSecrets"] = ConvertAliasToKeyArn(key);
                        } else if(keys is IList<object> list) {
                            inputs["ModuleSecrets"] = list.Select(item => ConvertAliasToKeyArn(item as string)).ToList();
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

        private async Task<VersionInfo> FindNewestVersion(Settings settings, string moduleName) {

            // enumerate versions in bucket
            var versions = new List<VersionInfo>();
            var request = new ListObjectsV2Request {
                BucketName = settings.DeploymentBucketName,
                Prefix = $"{settings.DeploymentBucketPath}{moduleName}/Versions/",
                Delimiter = "/",
                MaxKeys = 100
            };
            do {
                var response = await settings.S3Client.ListObjectsV2Async(request);
                versions.AddRange(response.S3Objects
                    .Select(found => VersionInfo.Parse(found.Key.Substring(request.Prefix.Length)))
                    .Where(v => !v.IsPreRelease)
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
    }
}
