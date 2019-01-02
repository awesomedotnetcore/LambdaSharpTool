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
using System.Collections;
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
                ForEach("Requires", module.Requires, ConvertDependency);
                ForEach("Entries", module.Declarations, ConvertEntry);
                return _builder;
            } catch(Exception e) {
                AddError(e);
                return null;
            }
        }

        private void ConvertPragma(int index, object pragma) {
            AtLocation($"{index}", () => _builder.AddPragma(pragma));
        }

        private void ConvertSecret(int index, string secret) {
            AtLocation($"{index}", () => {
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
            AtLocation($"{index}", () => {
                VersionInfo minVersion = null;
                VersionInfo maxVersion = null;
                if(dependency.Version != null) {
                    Validate(dependency.MinVersion == null, "'Version' and 'MinVersion' attributes cannot be used at the same time");
                    Validate(dependency.MaxVersion == null, "'Version' and 'MaxVersion' attributes cannot be used at the same time");
                    AtLocation("Version", () => {
                        if(!VersionInfo.TryParse(dependency.Version, out VersionInfo version)) {
                            AddError("invalid value");
                        } else {
                            minVersion = version;
                            maxVersion = version;
                        }
                    });
                } else {
                    if(dependency.MinVersion != null) {
                        AtLocation("MinVersion", () => {

                        });
                        if(!VersionInfo.TryParse(dependency.MinVersion, out minVersion)) {
                            AddError("invalid value");
                        }
                    }
                    if(dependency.MaxVersion != null) {
                        AtLocation("MaxVersion", () => {

                        });
                        if(!VersionInfo.TryParse(dependency.MaxVersion, out maxVersion)) {
                            AddError("invalid value");
                        }
                    }
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

        private void ConvertEntry(int index, EntryNode node)
            => ConvertEntry(null, index, node, new[] {
                "Condition",
                "Export",
                "Function",
                "Import",
                "Macro",
                "Mapping",
                "Module",
                "Namespace",
                "Package",
                "Parameter",
                "Resource",
                "ResourceType",
                "Variable"
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
                    Validate((node.Allow == null) || (node.Type == "AWS") || ResourceMapping.IsCloudFormationType(node.Type), "'Allow' attribute can only be used with AWS resource types");

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
                        arnAttribute: node.DefaultAttribute,
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

                    // recurse, but only allow 'Parameter' nodes
                    ConvertEntries(result, new[] { "Parameter" });
                });
                break;
            case "Variable":
                AtLocation(node.Variable, () => {

                    // validation
                    Validate(node.Value != null, "missing `Value` attribute");
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
                        allow: null,
                        encryptionContext: node.EncryptionContext
                    );

                    // recurse
                    ConvertEntries(result);
                });
                break;
            case "Namespace":
                AtLocation(node.Namespace, () => {

                    // create namespace entry
                    var result = _builder.AddVariable(
                        parent: parent,
                        name: node.Namespace,
                        description: node.Description,
                        type: "String",
                        scope: null,
                        value: "",
                        allow: null,
                        encryptionContext: null
                    );

                    // recurse
                    ConvertEntries(result);
                });
                break;
            case "Resource":
                AtLocation(node.Resource, () => {
                    if(node.Value != null) {

                        // validation
                        Validate((node.Allow == null) || (node.Type == null) || ResourceMapping.IsCloudFormationType(node.Type), "'Allow' attribute can only be used with AWS resource types");
                        Validate(node.If == null, "'If' attribute cannot be used with a referenced resource");
                        Validate(node.Properties == null, "'Properties' section cannot be used with a referenced resource");
                        if(node.Value is IList<object> values) {
                            foreach(var arn in values) {
                                ValidateARN(arn);
                            }
                        } else {
                            ValidateARN(node.Value);
                        }

                        // create variable entry
                        _builder.AddVariable(
                            parent: parent,
                            name: node.Resource,
                            description: node.Description,
                            type: node.Type ?? "String",
                            scope: ConvertScope(node.Scope),
                            value: node.Value,
                            allow: node.Allow,
                            encryptionContext: node.EncryptionContext
                        );
                    } else {

                        // validation
                        Validate(node.Type != null, "missing 'Type' attribute");
                        Validate((node.Allow == null) || ResourceMapping.IsCloudFormationType(node.Type ?? ""), "'Allow' attribute can only be used with AWS resource types");

                        // create resource entry
                        _builder.AddResource(
                            parent: parent,
                            name: node.Resource,
                            description: node.Description,
                            type: node.Type,
                            scope: ConvertScope(node.Scope),
                            allow: node.Allow,
                            properties: node.Properties,
                            dependsOn: ConvertToStringList(node.DependsOn),
                            arnAttribute: node.DefaultAttribute,
                            condition: node.If,
                            pragmas: node.Pragmas
                        );
                    }
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

                            // TODO (2018-12-13, bjorg): should this be `SourceBucketName` or simply `BucketName`?
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

                    // discover files to package
                    var files = new List<KeyValuePair<string, string>>();
                    if(node.Files != null) {
                        string folder;
                        string filePattern;
                        SearchOption searchOption;
                        var packageFiles = Path.Combine(Settings.WorkingDirectory, node.Files);
                        if((packageFiles.EndsWith("/", StringComparison.Ordinal) || Directory.Exists(packageFiles))) {
                            folder = Path.GetFullPath(packageFiles);
                            filePattern = "*";
                            searchOption = SearchOption.AllDirectories;
                        } else {
                            folder = Path.GetDirectoryName(packageFiles);
                            filePattern = Path.GetFileName(packageFiles);
                            searchOption = SearchOption.TopDirectoryOnly;
                        }
                        if(Directory.Exists(folder)) {
                            foreach(var filePath in Directory.GetFiles(folder, filePattern, searchOption)) {
                                var entryName = Path.GetRelativePath(folder, filePath);
                                files.Add(new KeyValuePair<string, string>(entryName, filePath));
                            }
                            files = files.OrderBy(file => file.Key).ToList();
                        } else {
                            AddError($"cannot find folder '{Path.GetRelativePath(Settings.WorkingDirectory, folder)}'");
                        }
                    } else {
                        AddError("missing 'Files' attribute");
                    }

                    // create package resource entry
                    var result = _builder.AddPackage(
                        parent: parent,
                        name: node.Package,
                        description: node.Description,
                        scope: ConvertScope(node.Scope),
                        files: files
                    );
                });
                break;
            case "Function":
                AtLocation(node.Function, () => {

                    // validation
                    Validate(node.Memory != null, "missing 'Memory' attribute");
                    Validate(int.TryParse(node.Memory, out _), "invalid 'Memory' value");
                    Validate(node.Timeout != null, "missing 'Timeout' attribute");
                    Validate(int.TryParse(node.Timeout, out _), "invalid 'Timeout' value");
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
                        parent: parent,
                        name: node.Function,
                        description: node.Description,
                        project: project,
                        language: language,
                        environment: node.Environment,
                        sources: sources,
                        condition: node.If,
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
            case "Condition":
                AtLocation(node.Condition, () => {
                    AtLocation("Value", () => {
                        Validate(node.Value != null, "missing 'Value' attribute");
                    });
                    _builder.AddCondition(
                        parent: parent,
                        name: node.Condition,
                        description: node.Description,
                        value: node.Value
                    );
                });
                break;
            case "Mapping":
                AtLocation(node.Mapping, () => {
                    IDictionary<string, IDictionary<string, string>> topLevelResults = new Dictionary<string, IDictionary<string, string>>();
                    if(node.Value is IDictionary topLevelEntries) {
                        AtLocation("Value", () => {
                            Validate(topLevelEntries.Count > 0, "missing top-level mappings");

                            // iterate over top-level entries
                            foreach(DictionaryEntry topLevel in topLevelEntries) {
                                AtLocation((string)topLevel.Key, () => {
                                    var secondLevelResults = new Dictionary<string, string>();
                                    topLevelResults[(string)topLevel.Key] = secondLevelResults;

                                    // convert top-level entry
                                    if(topLevel.Value is IDictionary secondLevelEntries) {
                                        Validate(secondLevelEntries.Count > 0, "missing second-level mappings");

                                        // iterate over second-level entries
                                        foreach(DictionaryEntry secondLevel in secondLevelEntries) {
                                            AtLocation((string)secondLevel.Key, () => {

                                                // convert second-level entry
                                                if(secondLevel.Value is string secondLevelValue) {
                                                    secondLevelResults[(string)secondLevel.Key] = secondLevelValue;
                                                } else {
                                                    AddError("invalid value");
                                                }
                                            });
                                        }
                                    } else {
                                        AddError("invalid value");
                                    }
                                });
                            }
                        });
                    } else if(node.Value != null) {
                        AddError("invalid value for 'Value' attribute");
                    } else {
                        AddError("missing 'Value' attribute");
                    }
                    _builder.AddMapping(
                        parent: parent,
                        name: node.Mapping,
                        description: node.Description,
                        value: topLevelResults
                    );
                });
                break;
            case "Export":
                Validate(node.Value != null, "missing `Value` attribute");
                AtLocation(node.Export, () => _builder.AddExport(node.Export, node.Description, node.Value));
                break;
            case "ResourceType":
                Validate(node.Handler != null, "missing 'Handler' attribute");
                AtLocation(node.ResourceType, () => {
                    ModuleManifestCustomResource properties = null;
                    if(node.Properties != null) {
                        AtLocation("Properties", () => {
                            try {
                                properties = JObject.FromObject(node.Properties, new JsonSerializer {
                                    NullValueHandling = NullValueHandling.Ignore
                                }).ToObject<ModuleManifestCustomResource>();
                            } catch(JsonSerializationException e) {
                                AddError(e.Message);
                            }
                        });
                        Validate((properties.Request?.Count() ?? 0) > 0, "missing or empty 'Request' section");
                        Validate((properties.Response?.Count() ?? 0) > 0, "missing or empty 'Response' section");
                    } else {
                        AddError("missing 'Properties' section");
                    }
                    _builder.AddCustomResource(node.ResourceType, node.Description, node.Handler, properties);
                });
                break;
            case "Macro":
                Validate(node.Handler != null, "missing 'Handler' attribute");
                AtLocation(node.Macro, () => _builder.AddMacro(node.Macro, node.Description, node.Handler));
                break;
            }

            // local functions
            void ConvertEntries(AModuleEntry result, IEnumerable<string> nestedExpectedTypes = null) {
                ForEach("Entries", node.Declarations, (i, p) => ConvertEntry(result, i, p, nestedExpectedTypes ?? expectedTypes));
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

            return AtLocation($"{index}", () => {

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