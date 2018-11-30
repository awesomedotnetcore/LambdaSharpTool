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
                ForEach("Inputs", module.Inputs, ConvertInput);
                ForEach("Outputs", module.Outputs, ConvertOutput);
                ForEach("Variables", module.Variables, ConvertVariable);
                ForEach("Entries", module.Entries, ConvertEntry);
                ForEach("Functions",  module.Functions, ConvertFunction);
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

        private void ConvertInput(int index, EntryNode node) => ConvertEntry(null, index, node, new[] { "Parameter", "Import" });

        private void ConvertVariable(int index, EntryNode variable) => ConvertVariable(null, index, variable);

        private void ConvertVariable(AModuleEntry parent, int index, EntryNode variable)
            => ConvertEntry(parent, index, variable, new[] {
                "Var.Resource",
                "Var.Reference",
                "Var.Value",
                "Var.Secret",
                "Var.Empty",
                "Var.Module",
                "Package"
            });

        private void ConvertFunction(int index, EntryNode function) {
            ConvertEntry(null, index, function, new[] { "Function" });
        }

        private AFunctionSource ConvertFunctionSource(EntryNode function, int index, FunctionSourceNode source) {
            var type = DeterminNodeType("source", index, source, FunctionSourceNode.FieldCheckers, FunctionSourceNode.FieldCombinations, new[] {
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
                "Var.Resource",
                "Var.Reference",
                "Var.Value",
                "Var.Secret",
                "Var.Empty",
                "Var.Module",
                "Package",
                "Function",
                "Export",
                "CustomResource",
                "Macro"
            });

        private void ConvertEntry(AModuleEntry parent, int index, EntryNode node, IEnumerable<string> expectedTypes) {
            var type = DeterminNodeType("output", index, node, EntryNode.FieldCheckers, EntryNode.FieldCombinations, expectedTypes);
            switch(type) {
            case "Parameter":
                AtLocation(node.Parameter, () => {
                    var inputType = node.Type ?? "String";
                    if(node.Resource != null) {
                        AtLocation("Resource", () => {
                            Validate(ConvertToStringList(node.Resource.DependsOn).Any() != true, "'DependsOn' cannot be used on an input");
                            if(node.Default == null) {
                                Validate(node.Resource.Properties == null, "'Properties' section cannot be used with `Input` attribute unless the 'Default' is set to a blank string");
                            }
                        });
                    }
                    _builder.AddInput(
                        node.Parameter,
                        node.Description,
                        inputType,
                        node.Section,
                        node.Label,
                        node.Scope,
                        node.NoEcho,
                        node.Default,
                        node.ConstraintDescription,
                        node.AllowedPattern,
                        node.AllowedValues,
                        node.MaxLength,
                        node.MaxValue,
                        node.MinLength,
                        node.MinValue,
                        node.Resource?.Type,
                        node.Resource?.Allow,
                        node.Resource?.Properties,
                        node.Resource?.ArnAttribute
                    );
                });
                break;
            case "Import":
                AtLocation(node.Import, () => {
                    var inputType = node.Type ?? "String";
                    Validate(node.Import.Split("::").Length == 2, "incorrect format for `Import` attribute");
                    if(node.Resource != null) {
                        Validate(inputType == "String", "input 'Type' must be string");
                        AtLocation("Resource", () => {
                            Validate(node.Resource.Type != null, "'Type' attribute is required");
                            Validate(node.Resource.Allow != null, "'Allow' attribute is required");
                            Validate(ConvertToStringList(node.Resource.DependsOn).Any() != true, "'DependsOn' cannot be used on an input");
                        });
                    }
                    _builder.AddImport(
                        node.Import,
                        node.Description,
                        inputType,
                        node.Section,
                        node.Label,
                        node.Scope,
                        node.NoEcho,
                        node.Resource?.Type,
                        node.Resource?.Allow
                    );
                });
                break;
            case "Var.Resource":

                // managed resource
                AtLocation(node.Var, () => {

                    // create managed resource entry
                    var result = _builder.AddResource(
                        parent: parent,
                        name: node.Var,
                        description: node.Description,
                        scope: node.Scope,
                        awsType: node.Resource.Type,
                        awsProperties: node.Resource.Properties,
                        awsArnAttribute: node.Resource.ArnAttribute,
                        dependsOn: ConvertToStringList(node.Resource.DependsOn),
                        condition: null
                    );

                    // request managed resource grants
                    AtLocation("Resource", () => {
                        if(node.Resource.Type == null) {
                            AddError("missing Type attribute");
                        } else if(
                            node.Resource.Type.StartsWith("AWS::", StringComparison.Ordinal)
                            && !ResourceMapping.IsResourceTypeSupported(node.Resource.Type)
                        ) {
                            AddError($"unsupported resource type: {node.Resource.Type}");
                        } else if(!node.Resource.Type.StartsWith("AWS::", StringComparison.Ordinal)) {
                            Validate(node.Resource.Allow == null, "'Allow' attribute is not valid for custom resources");
                        }
                        _builder.AddGrant(result.LogicalId, node.Resource.Type, result.GetExportReference(), node.Resource.Allow);
                    });

                    // recurse
                    ConvertVariables(result);
                });
                break;
            case "Var.Reference":

                // existing resource
                AtLocation(node.Var, () => {

                    // create existing resource entry
                    var result = _builder.AddValue(
                        parent: parent,
                        name: node.Var,
                        description: node.Description,
                        reference: node.Value,
                        scope: node.Scope,
                        isSecret: false
                    );

                    // validate literal references are ARNs
                    if(node.Value is string text) {
                        ValidateARN(text);
                    } else if(node.Value is IList<object> values) {
                        foreach(var value in values) {
                            ValidateARN(value);
                        }
                    }

                    // request existing resource grants
                    AtLocation("Resource", () => {
                        _builder.AddGrant(result.LogicalId, node.Resource.Type, node.Value, node.Resource.Allow);
                    });

                    // recurse
                    ConvertVariables(result);
                });
                break;
            case "Var.Value":

                // literal value
                AtLocation(node.Var, () => {

                    // create literal value entry
                    var result = _builder.AddValue(
                        parent: parent,
                        name: node.Var,
                        description: node.Description,
                        reference: (node.Value is IList<object> values)
                            ? FnJoin(",", values)
                            : node.Value,
                        scope: node.Scope,
                        isSecret: false
                    );

                    // recurse
                    ConvertVariables(result);
                });
                break;
            case "Var.Secret":

                // encrypted value
                AtLocation(node.Var, () => {

                    // create encrypted value entry
                    var result = _builder.AddValue(
                        parent: parent,
                        name: node.Var,
                        description: node.Description,
                        scope: node.Scope,
                        reference: FnJoin(
                            "|",
                            new object[] {
                                node.Secret
                            }.Union(node.EncryptionContext
                                ?.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")
                                ?? new string[0]
                            ).ToArray()
                        ),
                        isSecret: true
                    );

                    // recurse
                    ConvertVariables(result);
                });
                break;
            case "Var.Empty":

                // empty entry
                AtLocation(node.Var, () => {

                    // create empty entry
                    var result = _builder.AddValue(
                        parent: parent,
                        name: node.Var,
                        description: node.Description,
                        reference: "",
                        scope: node.Scope,
                        isSecret: false
                    );

                    // recurse
                    ConvertVariables(result);
                });
                break;
            case "Var.Module":
                AtLocation(node.Var, () => {

                    // create module entry
                    var result = _builder.AddModule(
                        parent: parent,
                        name: node.Var,
                        description: node.Description,
                        module: node.Module,
                        version: node.Version,
                        sourceBucketName: node.SourceBucketName,
                        scope: node.Scope,
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
                    Validate(node.Bucket != null, "missing 'Bucket' attribute");
                    if(node.Bucket is string bucketParameter) {

                        // verify that target bucket is defined as parameter with correct type
                        ValidateSourceParameter(bucketParameter, "AWS::S3::Bucket", "S3 bucket resource");
                    }

                    // check if package is nested
                    if(parent != null) {
                        AddError("parameter package cannot be nested");
                    }

                    // create package resource entry
                    var result = _builder.AddPackage(
                        parent: parent,
                        name: node.Package,
                        description: node.Description,
                        scope: node.Scope,
                        destinationBucket: (node.Bucket is string)
                            ? _builder.GetEntry((string)node.Bucket).GetExportReference()
                            : node.Bucket,
                        destinationKeyPrefix: node.Prefix ?? "",
                        sourceFilepath: node.Files
                    );

                    // recurse
                    ConvertVariables(result);
                });
                break;
            case "Function":
                AtLocation(node.Function, () => {
                    Validate(node.Memory != null, "missing Memory attribute");
                    Validate(int.TryParse(node.Memory, out _), "invalid Memory value");
                    Validate(node.Timeout != null, "missing Name attribute");
                    Validate(int.TryParse(node.Timeout, out _), "invalid Timeout value");
                    ValidateFunctionSource(node.Sources ?? new FunctionSourceNode[0]);

                    // initialize VPC configuration if provided
                    object subnets = null;
                    object securityGroups = null;
                    if(node.VPC?.Any() == true) {
                        AtLocation("VPC", () => {
                            if(
                                !node.VPC.TryGetValue("SubnetIds", out subnets)
                                || !node.VPC.TryGetValue("SecurityGroupIds", out securityGroups)
                            ) {
                                AddError("Lambda function contains a VPC definition that does not include 'SubnetIds' or 'SecurityGroupIds' attributes");
                            }
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
                        subnets: subnets,
                        securityGroups: securityGroups
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
                ForEach("Variables", node.Variables, (i, p) => ConvertVariable(result, i, p));
                ForEach("Entries", node.Entries, (i, p) => ConvertEntry(result, i, p, expectedTypes));
            }

            void ValidateARN(object resourceArn) {
                if((resourceArn is string text) && !text.StartsWith("arn:") && (text != "*")) {
                    AddError($"resource name must be a valid ARN or wildcard: {resourceArn}");
                }
            }
        }

        private string DeterminNodeType<T>(
            string label,
            int index,
            T instance,
            Dictionary<string, Func<T, bool>> fieldChecker,
            Dictionary<string, IEnumerable<string>> fieldCombinations,
            IEnumerable<string> expectedTypes
        ) {
            return AtLocation($"[{index}]", () => {

                // find all declaration fields with a non-null value; use alphabetical order for consistency
                var matches = fieldCombinations
                    .OrderBy(kv => kv.Key)
                    .Where(kv => {
                        if(!fieldChecker.TryGetValue(kv.Key, out Func<T, bool> checker)) {
                            throw new InvalidOperationException($"missing field checker for '{kv.Key}'");
                        }
                        return checker(instance);
                    })
                    .Select(kv => new {
                        EntryType = kv.Key,
                        ValidFields = kv.Value
                    })
                    .ToArray();
                switch(matches.Length) {
                case 0:
                    AddError($"unknown {label} type");
                    return null;
                case 1:

                    // good to go
                    break;
                default:
                    AddError($"ambiguous {label} type: {string.Join(", ", matches.Select(kv => kv.EntryType))}");
                    return null;
                }

                // validate match
                var match = matches.First();
                foreach(var checker in fieldChecker.Where(kv =>
                    (kv.Key != match.EntryType)             // don't recheck the key we already used
                    && kv.Value(instance)                   // check if field is set
                    && !match.ValidFields.Contains(kv.Key)  // check if field is not valid
                )) {
                    AddError($"'{checker.Key}' cannot be used with '{match.EntryType}'");
                }
                if(!expectedTypes.Contains(match.EntryType)) {
                    AddError($"unexpected node type: {match.EntryType}");
                    return null;

                }
                return match.EntryType;
            });
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

                        // verify source exists
                        ValidateSourceParameter(source.S3, "AWS::S3::Bucket", "S3 bucket");
                    } else if(source.SlackCommand != null) {

                        // TODO (2018-11-10, bjorg): validate API expression
                    } else if(source.Topic != null) {

                        // verify source exists
                        ValidateSourceParameter(source.Topic, "AWS::SNS::Topic", "SNS topic");
                    } else if(source.Sqs != null) {

                        // validate settings
                        AtLocation("BatchSize", () => {
                            if((source.BatchSize < 1) || (source.BatchSize > 10)) {
                                AddError($"invalid BatchSize value: {source.BatchSize}");
                            }
                        });

                        // verify source exists
                        ValidateSourceParameter(source.Sqs, "AWS::SQS::Queue", "SQS queue");
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

                        // verify source exists
                        ValidateSourceParameter(source.DynamoDB, "AWS::DynamoDB::Table", "DynamoDB table");
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

                        // verify source exists
                        ValidateSourceParameter(source.Kinesis, "AWS::Kinesis::Stream", "Kinesis stream");
                    } else {
                        AddError("unknown source type");
                    }
                });
            }
        }

        private void ValidateSourceParameter(string fullName, string awsType, string typeDescription) {
            if(!_builder.TryGetEntry(fullName, out AModuleEntry entry)) {
                AddError($"could not find function source: '{fullName}'");
                return;
            }

            // TODO (2018-11-29, bjorg): validate AWS type of referenced entry
            // AddError($"function source must be an {typeDescription} resource: '{fullName}'");
        }
    }
}