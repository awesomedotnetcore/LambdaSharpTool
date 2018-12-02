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
using System.Text;
using System.Text.RegularExpressions;
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;
using MindTouch.LambdaSharp.Tool.Model.AST;

namespace MindTouch.LambdaSharp.Tool.Build {
    using Newtonsoft.Json.Linq;
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
                ForEach("Pragmas", module.Pragmas ?? new List<object>(), ConvertPragma);
                ForEach("Secrets", module.Secrets ?? new List<string>(), ConvertSecret);
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
                    TopicName = source.Topic
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
                        Validate(node.Allow != null, "'Allow' attribute is required");
                    }
                    Validate((node.Allow == null) || ResourceMapping.IsResourceTypeSupported(node.Type), "'Allow' attribute can only be used with AWS resource types");

                    // create input parameter entry
                    _builder.AddParameter(
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
                        encryptionContext: node.EncryptionContext
                    );
                });
                break;
            case "Import":
                AtLocation(node.Import, () => {

                    // validation
                    Validate(node.Import.Split("::").Length == 2, "incorrect format for `Import` attribute");
                    if(node.Properties != null) {
                        Validate(node.Type != null, "'Type' attribute is required");
                        Validate(node.Allow != null, "'Allow' attribute is required");
                    }
                    Validate((node.Allow == null) || ResourceMapping.IsResourceTypeSupported(node.Type), "'Allow' attribute can only be used with AWS resource types");

                    // create import/cross-module reference entry
                    _builder.AddImport(
                        import: node.Import,
                        section: node.Section,
                        label: node.Label,
                        description: node.Description,
                        type: node.Type ?? "String",
                        scope: node.Scope,
                        noEcho: node.NoEcho,
                        allow: node.Allow
                    );
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
                    ConvertVariables(result);
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
                    } else if(awsType.StartsWith("AWS::", StringComparison.Ordinal) == true) {
                        if(!ResourceMapping.IsResourceTypeSupported(awsType)) {
                            AddError($"unsupported resource type: {type}");
                        }
                    } else if(awsType != "AWS") {
                        Validate(node.Allow == null, "cannot use 'Allow' attribute with custom resources");
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
                        condition: null
                    );

                    // recurse
                    ConvertVariables(result);
                });
                break;
            case "Module":
                AtLocation(node.Module, () => {

                    // validation
                    AtLocation("Location", () => {
                        Validate(node.Location?.Name != null, "missing 'Name' attribute");
                        Validate(node.Location?.Version != null, "missing 'Version' attribute");
                        Validate(node.Location?.S3Bucket != null, "missing 'S3Bucket' attribute");
                    });

                    // create module entry
                    var result = _builder.AddModule(
                        parent: parent,
                        name: node.Module,
                        description: node.Description,
                        module: node.Location?.Name,
                        version: node.Location?.Version,
                        sourceBucketName: node.Location?.S3Bucket,
                        scope: ConvertScope(node.Scope),
                        dependsOn: node.DependsOn,
                        parameters: node.Parameters
                    );

                    // recurse
                    ConvertVariables(result);
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
                        scope: node.Scope,
                        files: node.Files
                    );

                    // recurse
                    ConvertVariables(result);
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

                    // create function entry
                    var sources = AtLocation(
                        "Sources",
                        () => node.Sources
                            ?.Select((source, eventIndex) => ConvertFunctionSource(node, eventIndex, source))
                            .Where(evt => evt != null)
                            .ToList()
                    );
                    var result = _builder.AddFunction(
                        parent: null,
                        name: node.Function,
                        description: node.Description,
                        project: node.Project,
                        language: node.Language,
                        environment: node.Environment,
                        sources: sources,
                        pragmas: node.Pragmas,
                        timeout: node.Timeout,
                        runtime: node.Runtime,
                        reservedConcurrency: node.ReservedConcurrency,
                        memory: node.Memory,
                        handler: node.Handler,
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

                // TODO (2018-09-20, bjorg): add custom resource name validation
                Validate(node.Handler != null, "missing Handler attribute");

                // TODO (2018-09-20, bjorg): confirm that `Handler` is set to an SNS topic or lambda function
                AtLocation(node.CustomResource, () => _builder.AddCustomResource(node.CustomResource, node.Description, node.Handler));
                break;
            case "Macro":

                // TODO (2018-11-29, bjorg): add macro name validation
                Validate(node.Handler != null, "missing Handler attribute");

                // TODO (2018-10-30, bjorg): confirm that `Handler` is set to a lambda function
                AtLocation(node.Macro, () => _builder.AddMacro(node.Macro, node.Description, node.Handler));
                break;
            }

            // local functions
            void ConvertVariables(AModuleEntry result) {
                ForEach("Entries", node.Entries, (i, p) => ConvertEntry(result, i, p, expectedTypes));
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