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
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;
using MindTouch.LambdaSharp.Tool.Model.AST;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MindTouch.LambdaSharp.Tool.Cli.Build {
    using static ModelFunctions;

    public class ModelAstToModuleConverter : AModelProcessor {

        //--- Constants ---
        private const string CUSTOM_RESOURCE_PREFIX = "Custom::";
        private const string SECRET_ALIAS_PATTERN = "[0-9a-zA-Z/_\\-]+";

        //--- Fields ---
        private ModuleBuilder _builder;

        //--- Constructors ---
        public ModelAstToModuleConverter(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public ModuleBuilder Convert(ModuleNode module) {

            // convert module definition
            try {

                // ensure version is present
                VersionInfo version;
                if(module.Version == null) {
                    version = VersionInfo.Parse("1.0");
                } else if(!VersionInfo.TryParse(module.Version, out version)) {
                    AddError("`Version` expected to have format: Major.Minor[.Build[.Revision]]");
                    version = VersionInfo.Parse("0.0");
                }

                // initialize module
                _builder = new ModuleBuilder(Settings, SourceFilename, new Module {
                    Name = module.Module,
                    Version = version,
                    Description = module.Description
                });

                // convert collections
                ForEach("Pragmas", module.Pragmas, ConvertPragma);
                ForEach("Secrets", module.Secrets, ConvertSecret);
                ForEach("Dependencies", module.Dependencies, ConvertDependency);
                ForEach("Outputs", module.Outputs, ConvertOutput);
                ForEach("Entries", module.Entries, ConvertEntry);
                return _builder;
            } catch(Exception e) {
                AddError(e);
                return null;
            }
        }

        private void ConvertPragma(int index, object pragma) {
            AtLocation($"[{index}]", () => _builder.AddPragma(pragma));
        }

        private void ConvertSecret(int index, string secret) {
            AtLocation($"[{index}]", () => {
                if(string.IsNullOrEmpty(secret)) {
                    AddError($"secret has no value");
                } else if(secret.Equals("aws/ssm", StringComparison.OrdinalIgnoreCase)) {
                    AddError($"cannot grant permission to decrypt with aws/ssm");
                } else if(secret.StartsWith("arn:")) {
                    if(!Regex.IsMatch(secret, $"arn:aws:kms:{Settings.AwsRegion}:{Settings.AwsAccountId}:key/[a-fA-F0-9\\-]+")) {
                        AddError("secret key must be a valid ARN for the current region and account ID");
                    }
                } else if(!Regex.IsMatch(secret, SECRET_ALIAS_PATTERN)) {
                    AddError("secret key must be a valid alias");
                }
                _builder.AddSecret(secret);
            });
        }

        private void ConvertDependency(int index, ModuleDependencyNode dependency) {
            AtLocation($"[{index}]", () => {
                VersionInfo minVersion = null;
                VersionInfo maxVersion = null;
                if(dependency.Version != null) {
                    Validate(dependency.MinVersion == null, "'Version' and 'MinVersion' attributes cannot be used at the same time");
                    Validate(dependency.MaxVersion == null, "'Version' and 'MaxVersion' attributes cannot be used at the same time");
                    AtLocation("Version", () => {
                        if(!VersionInfo.TryParse(dependency.Version, out VersionInfo version)) {
                            AddError("invalid value");
                        } else {

                            // TODO (2018-12-07, bjorg): add support for min-max version
                            minVersion = version;
                            maxVersion = version;
                        }
                    });
                }
                if(dependency.MinVersion != null) {
                    AtLocation("MinVersion", () => {
                        if(!VersionInfo.TryParse(dependency.MinVersion, out minVersion)) {
                            AddError("invalid value");
                        }
                    });
                }
                if(dependency.MaxVersion != null) {
                    AtLocation("MaxVersion", () => {
                        if(!VersionInfo.TryParse(dependency.MaxVersion, out maxVersion)) {
                            AddError("invalid value");
                        }
                    });
                }
                _builder.AddDependency(
                    moduleName: dependency.Module,
                    minVersion: minVersion,
                    maxVersion: maxVersion,
                    bucketName: dependency.BucketName
                );
            });
        }

        private AFunctionSource ConvertFunctionSource(EntryNode function, int index, FunctionSourceNode source) {
            var type = DeterminNodeType("source", index, source, FunctionSourceNode.FieldCombinations, new[] {
                "Api",
                "Schedule",
                "S3",
                "SlackCommand",
                "Topic",
                "Sqs",
                "Alexa",
                "DynamoDB",
                "Kinesis",
            });
            switch(type) {
            case "Api":
                return AtLocation("Api", () => {

                    // extract http method from route
                    var api = source.Api.Trim();
                    var pathSeparatorIndex = api.IndexOfAny(new[] { ':', ' ' });
                    if(pathSeparatorIndex < 0) {
                        AddError("invalid api format");
                        return new ApiGatewaySource {
                            Method = "ANY",
                            Path = new string[0],
                            Integration = ApiGatewaySourceIntegration.RequestResponse
                        };
                    }
                    var method = api.Substring(0, pathSeparatorIndex).ToUpperInvariant();
                    if(method == "*") {
                        method = "ANY";
                    }
                    var path = api.Substring(pathSeparatorIndex + 1).TrimStart().Split('/', StringSplitOptions.RemoveEmptyEntries);

                    // parse integration into a valid enum
                    var integration = AtLocation("Integration", () => Enum.Parse<ApiGatewaySourceIntegration>(source.Integration ?? "RequestResponse", ignoreCase: true));
                    return new ApiGatewaySource {
                        Method = method,
                        Path = path,
                        Integration = integration,
                        OperationName = source.OperationName,
                        ApiKeyRequired = source.ApiKeyRequired
                    };
                });
            case "Schedule":
                return AtLocation("Schedule", () => new ScheduleSource {
                    Expression = source.Schedule,
                    Name = source.Name
                });
            case "S3":
                return AtLocation("S3", () => new S3Source {
                    Bucket = source.S3,
                    Events = source.Events ?? new List<string> {

                        // default S3 events to listen to
                        "s3:ObjectCreated:*"
                    },
                    Prefix = source.Prefix,
                    Suffix = source.Suffix
                });
            case "SlackCommand":
                return AtLocation("SlackCommand", () => new ApiGatewaySource {
                    Method = "POST",
                    Path = source.SlackCommand.Split('/', StringSplitOptions.RemoveEmptyEntries),
                    Integration = ApiGatewaySourceIntegration.SlackCommand,
                    OperationName = source.OperationName
                });
            case "Topic":
                return AtLocation("Topic", () => new TopicSource {
                    TopicName = source.Topic,
                    Filters = source.Filters
                });
            case "Sqs":
                return AtLocation("Sqs", () => new SqsSource {
                    Queue = source.Sqs,
                    BatchSize = source.BatchSize ?? 10
                });
            case "Alexa":
                return AtLocation("Alexa", () => new AlexaSource {
                    EventSourceToken = source.Alexa
                });
            case "DynamoDB":
                return AtLocation("DynamoDB", () => new DynamoDBSource {
                    DynamoDB = source.DynamoDB,
                    BatchSize = source.BatchSize ?? 100,
                    StartingPosition = source.StartingPosition ?? "LATEST"
                });
            case "Kinesis":
                return AtLocation("Kinesis", () => new KinesisSource {
                    Kinesis = source.Kinesis,
                    BatchSize = source.BatchSize ?? 100,
                    StartingPosition = source.StartingPosition ?? "LATEST"
                });
            }
            return null;
        }

        private void ConvertOutput(int index, EntryNode output) => ConvertEntry(null, index, output, new[] { "Export", "CustomResource", "Macro" });

        private void ConvertEntry(int index, EntryNode node)
            => ConvertEntry(null, index, node, new[] {
                "Parameter",
                "Import",
                "Variable",
                "Resource",
                "Module",
                "Package",
                "Function",
                "Export",
                "CustomResource",
                "Macro"
            });

        private void ConvertEntry(AModuleEntry parent, int index, EntryNode node, IEnumerable<string> expectedTypes) {
            var type = DeterminNodeType("entry", index, node, EntryNode.FieldCombinations, expectedTypes);
            switch(type) {
            case "Parameter":
                AtLocation(node.Parameter, () => {

                    // validation
                    Validate((node.Default != null) || (node.Properties == null), "'Properties' section cannot be used unless the 'Default' attribute is set");
                    if(node.Properties != null) {
                        Validate(node.Type != null, "'Type' attribute is required");
                    }
                    Validate((node.Allow == null) || (node.Type == "AWS") || ResourceMapping.IsResourceTypeSupported(node.Type), "'Allow' attribute can only be used with AWS resource types");

                    // create input parameter entry
                    _builder.AddParameter(
                        parent: parent,
                        name: node.Parameter,
                        section: node.Section,
                        label: node.Label,
                        description: node.Description,
                        type: node.Type ?? "String",
                        scope: ConvertScope(node.Scope),
                        noEcho: node.NoEcho,
                        defaultValue: node.Default,
                        constraintDescription: node.ConstraintDescription,
                        allowedPattern: node.AllowedPattern,
                        allowedValues: node.AllowedValues,
                        maxLength: node.MaxLength,
                        maxValue: node.MaxValue,
                        minLength: node.MinLength,
                        minValue: node.MinValue,
                        allow: node.Allow,
                        properties: node.Properties,
                        arnAttribute: node.ArnAttribute,
                        encryptionContext: node.EncryptionContext,
                        pragmas: node.Pragmas
                    );
                });
                break;
            case "Import":
                AtLocation(node.Import, () => {

                    // create import/cross-module reference entry
                    var result = _builder.AddImport(
                        import: node.Import,
                        description: node.Description
                    );

                    // recurse
                    ConvertEntries(result, new[] { "Parameter" });
                });
                break;
            case "Variable":
                AtLocation(node.Variable, () => {

                    // validation
                    Validate((node.EncryptionContext == null) || (node.Type == "Secret"), "entry must have Type 'Secret' to use 'EncryptionContext' section");
                    Validate((node.Type != "Secret") || !(node.Value is IList<object>), "entry with type 'Secret' cannot have a list of values");

                    // create variable entry
                    var result = _builder.AddVariable(
                        parent: parent,
                        name: node.Variable,
                        description: node.Description,
                        type: node.Type ?? "String",
                        scope: ConvertScope(node.Scope),
                        value: node.Value ?? "",
                        encryptionContext: node.EncryptionContext
                    );

                    // recurse
                    ConvertEntries(result);
                });
                break;
            case "Resource":
                AtLocation(node.Resource, () => {

                    // validation
                    Validate((node.Value == null) || (node.Properties == null), "cannot use 'Properties' section with a reference resource");
                    var awsType = node.Type ?? "AWS";
                    if(node.Value != null) {
                        Validate(node.Properties == null, "cannot use 'Properties' section with a reference resource");
                        if(node.Value is string text) {
                            ValidateARN(text);
                        } else if(node.Value is IList<object> values) {
                            foreach(var arn in values) {
                                ValidateARN(arn);
                            }
                        }
                    } else if((awsType != "AWS") && !awsType.StartsWith("AWS::", StringComparison.Ordinal)) {
                        Validate((node.Allow == null) || (awsType == "AWS") || ResourceMapping.IsResourceTypeSupported(awsType), "'Allow' attribute can only be used with AWS resource types");
                    }

                    // create resource entry
                    var result = _builder.AddResource(
                        parent: parent,
                        name: node.Resource,
                        description: node.Description,
                        type: awsType,
                        scope: ConvertScope(node.Scope),
                        value: node.Value,
                        allow: node.Allow,
                        properties: node.Properties,
                        dependsOn: ConvertToStringList(node.DependsOn),
                        arnAttribute: node.ArnAttribute,
                        condition: null,
                        pragmas: node.Pragmas
                    );

                    // recurse
                    ConvertEntries(result);
                });
                break;
            case "Module":
                AtLocation(node.Module, () => {

                    // read optional properties
                    string moduleName = null;
                    object moduleVersion = null;
                    object moduleSourceBucket = null;
                    if(node.Properties != null) {
                        AtLocation("Properties", () => {
                            node.Properties.TryGetValue("ModuleName", out object moduleNameObject);
                            if(!(moduleNameObject is string moduleNameText)) {
                                AddError("'ModuleName' attribute must be a string value");
                            } else {
                                moduleName = moduleNameText;
                            }
                            node.Properties.TryGetValue("Version", out moduleVersion);
                            node.Properties.TryGetValue("SourceBucket", out moduleSourceBucket);
                        });
                    }

                    // create module entry
                    var result = _builder.AddModule(
                        parent: parent,
                        name: node.Module,
                        description: node.Description,
                        module: moduleName,
                        version: moduleVersion,
                        sourceBucketName: moduleSourceBucket,
                        scope: ConvertScope(node.Scope),
                        dependsOn: node.DependsOn,
                        parameters: node.Parameters
                    );
                });
                break;
            case "Package":

                // package resource
                AtLocation(node.Package, () => {

                    // check if required attributes are present
                    Validate(node.Files != null, "missing 'Files' attribute");

                    // create package resource entry
                    var result = _builder.AddPackage(
                        parent: parent,
                        name: node.Package,
                        description: node.Description,
                        scope: ConvertScope(node.Scope),
                        sourceFilepath: node.Files
                    );

                    // recurse
                    ConvertEntries(result);
                });
                break;
            case "Function":
                AtLocation(node.Function, () => {

                    // validation
                    Validate(node.Memory != null, "missing Memory attribute");
                    Validate(int.TryParse(node.Memory, out _), "invalid Memory value");
                    Validate(node.Timeout != null, "missing Name attribute");
                    Validate(int.TryParse(node.Timeout, out _), "invalid Timeout value");
                    ValidateFunctionSource(node.Sources ?? new FunctionSourceNode[0]);

                    // initialize VPC configuration if provided
                    if(node.VPC != null) {
                        AtLocation("VPC", () => {
                            Validate(node.VPC.SubnetIds != null, "missing 'SubnetIds' attribute");
                            Validate(node.VPC.SecurityGroupIds != null, "missing 'SecurityGroupIds' attribute");
                        });
                    }

                    // determine function type
                    var project = node.Project;
                    var language = node.Language;
                    var runtime = node.Runtime;
                    var handler = node.Handler;
                    DetermineFunctionType(node.Function, ref project, ref language, ref runtime, ref handler);

                    // create function entry
                    var sources = AtLocation("Sources", () => node.Sources
                        ?.Select((source, eventIndex) => ConvertFunctionSource(node, eventIndex, source))
                        .Where(evt => evt != null)
                        .ToList()
                    );
                    var result = _builder.AddFunction(
                        parent: null,
                        name: node.Function,
                        description: node.Description,
                        project: project,
                        language: language,
                        environment: node.Environment,
                        sources: sources,
                        pragmas: node.Pragmas,
                        timeout: node.Timeout,
                        runtime: runtime,
                        reservedConcurrency: node.ReservedConcurrency,
                        memory: node.Memory,
                        handler: handler,
                        subnets: node.VPC?.SubnetIds,
                        securityGroups: node.VPC?.SecurityGroupIds
                    );
                });
                break;
            case "Export":

                // TODO (2018-09-20, bjorg): add name validation
                AtLocation(node.Export, () => _builder.AddExport(node.Export, node.Description, node.Value));
                break;
            case "CustomResource":
                Validate(node.Handler != null, "missing Handler attribute");

                // TODO (2018-09-20, bjorg): add custom resource name validation
                // TODO (2018-09-20, bjorg): confirm that `Handler` is set to an SNS topic or lambda function

                AtLocation(node.CustomResource, () => {
                    ModuleManifestCustomResource properties = null;
                    if(node.Properties != null) {
                        AtLocation("Properties", () => {
                            try {
                                properties = JObject.FromObject(node.Properties).ToObject<ModuleManifestCustomResource>();
                            } catch(JsonSerializationException e) {
                                AddError(e.Message);
                            }
                        });
                        Validate((properties.Request?.Count() ?? 0) > 0, "missing or empty 'Request' section");
                        Validate((properties.Response?.Count() ?? 0) > 0, "missing or empty 'Response' section");
                    } else {
                        AddError("missing 'Properties' section");
                    }
                    _builder.AddCustomResource(node.CustomResource, node.Description, node.Handler, properties);
                });
                break;
            case "Macro":

                // TODO (2018-11-29, bjorg): add macro name validation
                Validate(node.Handler != null, "missing Handler attribute");

                // TODO (2018-10-30, bjorg): confirm that `Handler` is set to a lambda function
                AtLocation(node.Macro, () => _builder.AddMacro(node.Macro, node.Description, node.Handler));
                break;
            }

            // local functions
            void ConvertEntries(AModuleEntry result, IEnumerable<string> nestedExpectedTypes = null) {
                ForEach("Entries", node.Entries, (i, p) => ConvertEntry(result, i, p, nestedExpectedTypes ?? expectedTypes));
            }

            void ValidateARN(object resourceArn) {
                if((resourceArn is string text) && !text.StartsWith("arn:") && (text != "*")) {
                    AddError($"resource name must be a valid ARN or wildcard: {resourceArn}");
                }
            }
        }

        private void ValidateFunctionSource(IEnumerable<FunctionSourceNode> sources) {
            var index = 0;
            foreach(var source in sources) {
                ++index;
                AtLocation($"{index}", () => {
                    if(source.Api != null) {

                        // TODO (2018-11-10, bjorg): validate API expression
                    } else if(source.Schedule != null) {

                        // TODO (2018-06-27, bjorg): add cron/rate expression validation
                    } else if(source.S3 != null) {

                        // TODO (2018-06-27, bjorg): add events, prefix, suffix validation
                    } else if(source.SlackCommand != null) {

                        // TODO (2018-11-10, bjorg): validate API expression
                    } else if(source.Topic != null) {

                        // nothing to validate
                    } else if(source.Sqs != null) {

                        // validate settings
                        AtLocation("BatchSize", () => {
                            if((source.BatchSize < 1) || (source.BatchSize > 10)) {
                                AddError($"invalid BatchSize value: {source.BatchSize}");
                            }
                        });
                    } else if(source.Alexa != null) {

                        // TODO (2018-11-10, bjorg): validate Alexa Skill ID
                    } else if(source.DynamoDB != null) {

                        // validate settings
                        AtLocation("BatchSize", () => {
                            if((source.BatchSize < 1) || (source.BatchSize > 100)) {
                                AddError($"invalid BatchSize value: {source.BatchSize}");
                            }
                        });
                        AtLocation("StartingPosition", () => {
                            switch(source.StartingPosition) {
                            case "TRIM_HORIZON":
                            case "LATEST":
                            case null:
                                break;
                            default:
                                AddError($"invalid StartingPosition value: {source.StartingPosition}");
                                break;
                            }
                        });
                    } else if(source.Kinesis != null) {

                        // validate settings
                        AtLocation("BatchSize", () => {
                            if((source.BatchSize < 1) || (source.BatchSize > 100)) {
                                AddError($"invalid BatchSize value: {source.BatchSize}");
                            }
                        });
                        AtLocation("StartingPosition", () => {
                            switch(source.StartingPosition) {
                            case "TRIM_HORIZON":
                            case "LATEST":
                            case null:
                                break;
                            default:
                                AddError($"invalid StartingPosition value: {source.StartingPosition}");
                                break;
                            }
                        });
                    } else {
                        AddError("unknown source type");
                    }
                });
            }
        }

        private string DeterminNodeType(
            string entryName,
            int index,
            object instance,
            Dictionary<string, IEnumerable<string>> typeChecks,
            IEnumerable<string> expectedTypes
        ) {
            var instanceLookup = JObject.FromObject(instance);

            return AtLocation($"[{index}]", () => {

                // find all declaration fields with a non-null value; use alphabetical order for consistency
                var matches = typeChecks
                    .OrderBy(kv => kv.Key)
                    .Where(kv => IsFieldSet(kv.Key))
                    .Select(kv => new {
                        EntryType = kv.Key,
                        ValidFields = kv.Value
                    })
                    .ToArray();
                switch(matches.Length) {
                case 0:
                    AddError($"unknown {entryName} type");
                    return null;
                case 1:

                    // good to go
                    break;
                default:
                    AddError($"ambiguous {entryName} type: {string.Join(", ", matches.Select(kv => kv.EntryType))}");
                    return null;
                }

                // validate match
                var match = matches.First();
                var invalidFields = typeChecks

                    // collect all field names
                    .SelectMany(kv => kv.Value)
                    .Distinct()

                    // only keep names that are not defined for the matched type
                    .Where(field => !match.ValidFields.Contains(field))

                    // check if the field is set on the instance
                    .Where(field => IsFieldSet(field))
                    .OrderBy(field => field)
                    .ToArray();
                if(invalidFields.Any()) {
                    AddError($"'{string.Join(", ", invalidFields)}' cannot be used with '{match.EntryType}'");
                }

                // check if the matched entry was expected
                if(!expectedTypes.Contains(match.EntryType)) {
                    AddError($"unexpected node type: {match.EntryType}");
                    return null;
                }
                return match.EntryType;
            });

            // local functions
            bool IsFieldSet(string field)
                => instanceLookup.TryGetValue(field, out JToken token) && (token.Type != JTokenType.Null);
        }

        private void DetermineFunctionType(
            string functionName,
            ref string project,
            ref string language,
            ref string runtime,
            ref string handler
        ) {

            // identify folder for function
            var folderName = new[] {
                functionName,
                $"{_builder.Name}.{functionName}"
            }.FirstOrDefault(name => Directory.Exists(Path.Combine(Settings.WorkingDirectory, name)));
            if(folderName == null) {
                AddError($"could not locate function directory");
                return;
            }

            // determine the function project
            project = project ?? new [] {
                Path.Combine(Settings.WorkingDirectory, folderName, $"{folderName}.csproj"),
                Path.Combine(Settings.WorkingDirectory, folderName, "index.js")
            }.FirstOrDefault(path => File.Exists(path));
            if(project == null) {
                AddError("could not locate the function project");
                return;
            }
            switch(Path.GetExtension((string)project).ToLowerInvariant()) {
            case ".csproj":
                DetermineDotNetFunctionProperties(functionName, project, ref language, ref runtime, ref handler);
                break;
            case ".js":
                DetermineJavascriptFunctionProperties(functionName, project, ref language, ref runtime, ref handler);
                break;
            default:
                AddError("could not determine the function language");
                return;
            }
        }

        private void DetermineDotNetFunctionProperties(
            string functionName,
            string project,
            ref string language,
            ref string runtime,
            ref string handler
        ) {
            language = "csharp";

            // compile function project
            var projectName = Path.GetFileNameWithoutExtension(project);

            // check if the handler/runtime were provided or if they need to be extracted from the project file
            var csproj = XDocument.Load(project);
            var mainPropertyGroup = csproj.Element("Project")?.Element("PropertyGroup");

            // make sure the .csproj file contains the lambda tooling
            var hasAwsLambdaTools = csproj.Element("Project")
                ?.Elements("ItemGroup")
                .Any(el => (string)el.Element("DotNetCliToolReference")?.Attribute("Include") == "Amazon.Lambda.Tools") ?? false;
            if(!hasAwsLambdaTools) {
                AddError($"the project is missing the AWS lambda tool defintion; make sure that {project} includes <DotNetCliToolReference Include=\"Amazon.Lambda.Tools\"/>");
            }

            // check if we need to parse the <TargetFramework> element to determine the lambda runtime
            var targetFramework = mainPropertyGroup?.Element("TargetFramework").Value;
            if(runtime == null) {
                switch(targetFramework) {
                case "netcoreapp1.0":
                    runtime = "dotnetcore1.0";
                    break;
                case "netcoreapp2.0":
                    runtime =  "dotnetcore2.0";
                    break;
                case "netcoreapp2.1":
                    runtime = "dotnetcore2.1";
                    break;
                default:
                    AddError($"could not determine runtime from target framework: {targetFramework}; specify 'Runtime' attribute explicitly");
                    break;
                }
            }

            // check if we need to read the project file <RootNamespace> element to determine the handler name
            if(handler == null) {
                var rootNamespace = mainPropertyGroup?.Element("RootNamespace")?.Value;
                if(rootNamespace != null) {
                    handler = $"{projectName}::{rootNamespace}.Function::FunctionHandlerAsync";
                } else {
                    AddError("could not auto-determine handler; either add 'Handler' attribute or <RootNamespace> to project file");
                }
            }
        }

        private void DetermineJavascriptFunctionProperties(
            string functionName,
            string project,
            ref string language,
            ref string runtime,
            ref string handler
        ) {
            language = "javascript";
            runtime = runtime ?? "nodejs8.10";
            handler = handler ?? "index.handler";
        }

        private IList<string> ConvertScope(object scope) {
            if(scope == null) {
                return new string[0];
            }
            return AtLocation("Scope", () => {
                return (scope == null)
                    ? new List<string>()
                    : ConvertToStringList(scope);
            });
        }
    }
}