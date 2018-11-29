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
                ForEach("Variables", module.Variables, ConvertParameter);
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

        private void ConvertInput(int index, InputNode input) {
            var type = DeterminNodeType("input", index, input, InputNode.FieldCheckers, InputNode.FieldCombinations, new[] { "Parameter", "Import" });
            var inputType = input.Type ?? "String";
            switch(type) {
            case "Parameter":
                if(input.Resource != null) {
                    Validate(input.Type == "String", "input 'Type' must be string");
                    AtLocation("Resource", () => {
                        Validate(ConvertToStringList(input.Resource.DependsOn).Any() != true, "'DependsOn' cannot be used on an input");
                        if(input.Default == null) {
                            Validate(input.Resource.Properties == null, "'Properties' section cannot be used with `Input` attribute unless the 'Default' is set to a blank string");
                        }
                    });
                }
                AtLocation(input.Parameter, () => _builder.AddInput(
                    input.Parameter,
                    input.Description,
                    inputType,
                    input.Section,
                    input.Label,
                    input.Scope,
                    input.NoEcho,
                    input.Default,
                    input.ConstraintDescription,
                    input.AllowedPattern,
                    input.AllowedValues,
                    input.MaxLength,
                    input.MaxValue,
                    input.MinLength,
                    input.MinValue,
                    input.Resource?.Type,
                    input.Resource?.Allow,
                    input.Resource?.Properties,
                    input.Resource?.ArnAttribute
                ));
                break;
            case "Import":
                Validate(input.Import.Split("::").Length == 2, "incorrect format for `Import` attribute");
                if(input.Resource != null) {
                    Validate(inputType == "String", "input 'Type' must be string");
                    AtLocation("Resource", () => {
                        Validate(input.Resource.Type != null, "'Type' attribute is required");
                        Validate(input.Resource.Allow != null, "'Allow' attribute is required");
                        Validate(ConvertToStringList(input.Resource.DependsOn).Any() != true, "'DependsOn' cannot be used on an input");
                    });
                }
                AtLocation(input.Import, () => _builder.AddImport(
                    input.Import,
                    input.Description,
                    inputType,
                    input.Section,
                    input.Label,
                    input.Scope,
                    input.NoEcho,
                    input.Resource?.Type,
                    input.Resource?.Allow
                ));
                break;
            }
        }

        private void ConvertParameter(int index, ParameterNode parameter) => ConvertParameter(null, index, parameter);

        private void ConvertParameter(
            AModuleEntry parent,
            int index,
            ParameterNode parameter
        ) {
            var type = DeterminNodeType("variable", index, parameter, ParameterNode.FieldCheckers, ParameterNode.FieldCombinations, new[] {
                "Var.Resource",
                "Var.Reference",
                "Var.Value",
                "Var.Secret",
                "Var.Empty",
                "Package"
            });
            switch(type) {
            case "Var.Resource":

                // managed resource
                AtLocation(parameter.Var, () => {

                    // create managed resource entry
                    var result = _builder.AddResource(
                        parent: parent,
                        name: parameter.Var,
                        description: parameter.Description,
                        scope: parameter.Scope,
                        awsType: parameter.Resource.Type,
                        awsProperties: parameter.Resource.Properties,
                        awsArnAttribute: parameter.Resource.ArnAttribute,
                        dependsOn: ConvertToStringList(parameter.Resource.DependsOn),
                        condition: null
                    );

                    // request managed resource grants
                    AtLocation("Resource", () => {
                        if(parameter.Resource.Type == null) {
                            AddError("missing Type attribute");
                        } else if(
                            parameter.Resource.Type.StartsWith("AWS::", StringComparison.Ordinal)
                            && !ResourceMapping.IsResourceTypeSupported(parameter.Resource.Type)
                        ) {
                            AddError($"unsupported resource type: {parameter.Resource.Type}");
                        } else if(!parameter.Resource.Type.StartsWith("AWS::", StringComparison.Ordinal)) {
                            Validate(parameter.Resource.Allow == null, "'Allow' attribute is not valid for custom resources");
                        }
                        _builder.AddGrant(result.LogicalId, parameter.Resource.Type, result.GetExportReference(), parameter.Resource.Allow);
                    });

                    // recurse
                    ConvertParameters(result);
                });
                break;
            case "Var.Reference":

                // existing resource
                AtLocation(parameter.Var, () => {

                    // create existing resource entry
                    var result = _builder.AddValue(
                        parent: parent,
                        name: parameter.Var,
                        description: parameter.Description,
                        reference: parameter.Value,
                        scope: parameter.Scope,
                        isSecret: false
                    );

                    // validate literal references are ARNs
                    if(parameter.Value is string text) {
                        ValidateARN(text);
                    } else if(parameter.Value is IList<object> values) {
                        foreach(var value in values) {
                            ValidateARN(value);
                        }
                    }

                    // request existing resource grants
                    AtLocation("Resource", () => {
                        _builder.AddGrant(result.LogicalId, parameter.Resource.Type, parameter.Value, parameter.Resource.Allow);
                    });

                    // recurse
                    ConvertParameters(result);
                });
                break;
            case "Var.Value":

                // literal value
                AtLocation(parameter.Var, () => {

                    // create literal value entry
                    var result = _builder.AddValue(
                        parent: parent,
                        name: parameter.Var,
                        description: parameter.Description,
                        reference: (parameter.Value is IList<object> values)
                            ? FnJoin(",", values)
                            : parameter.Value,
                        scope: parameter.Scope,
                        isSecret: false
                    );

                    // recurse
                    ConvertParameters(result);
                });
                break;
            case "Var.Secret":

                // encrypted value
                AtLocation(parameter.Var, () => {

                    // create encrypted value entry
                    var result = _builder.AddValue(
                        parent: parent,
                        name: parameter.Var,
                        description: parameter.Description,
                        scope: parameter.Scope,
                        reference: FnJoin(
                            "|",
                            new object[] {
                                parameter.Secret
                            }.Union(parameter.EncryptionContext
                                ?.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")
                                ?? new string[0]
                            ).ToArray()
                        ),
                        isSecret: true
                    );

                    // recurse
                    ConvertParameters(result);
                });
                break;
            case "Var.Empty":

                // empty entry
                AtLocation(parameter.Var, () => {

                    // create empty entry
                    var result = _builder.AddValue(
                        parent: parent,
                        name: parameter.Var,
                        description: parameter.Description,
                        reference: "",
                        scope: parameter.Scope,
                        isSecret: false
                    );

                    // recurse
                    ConvertParameters(result);
                });
                break;
            case "Package":

                // package resource
                AtLocation(parameter.Package, () => {

                    // check if required attributes are present
                    Validate(parameter.Files != null, "missing 'Files' attribute");
                    Validate(parameter.Bucket != null, "missing 'Bucket' attribute");
                    if(parameter.Bucket is string bucketParameter) {

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
                        name: parameter.Package,
                        description: parameter.Description,
                        scope: parameter.Scope,
                        destinationBucket: (parameter.Bucket is string)
                            ? _builder.GetEntry((string)parameter.Bucket).GetExportReference()
                            : parameter.Bucket,
                        destinationKeyPrefix: parameter.Prefix ?? "",
                        sourceFilepath: parameter.Files
                    );

                    // recurse
                    ConvertParameters(result);
                });
                break;
            }

            // local functions
            void ConvertParameters(AModuleEntry result) {
                ForEach("Variables", parameter.Variables, (i, p) => ConvertParameter(result, i, p));
            }

            void ValidateARN(object resourceArn) {
                if((resourceArn is string text) && !text.StartsWith("arn:") && (text != "*")) {
                    AddError($"resource name must be a valid ARN or wildcard: {resourceArn}");
                }
            }
        }

        private void ConvertFunction(int index, FunctionNode function) {
            AtLocation(function.Function, () => {
                Validate(function.Memory != null, "missing Memory attribute");
                Validate(int.TryParse(function.Memory, out _), "invalid Memory value");
                Validate(function.Timeout != null, "missing Name attribute");
                Validate(int.TryParse(function.Timeout, out _), "invalid Timeout value");
                ValidateFunctionSource(function.Sources);

                // initialize VPC configuration if provided
                object subnets = null;
                object securityGroups = null;
                if(function.VPC?.Any() == true) {
                    AtLocation("VPC", () => {
                        if(
                            !function.VPC.TryGetValue("SubnetIds", out subnets)
                            || !function.VPC.TryGetValue("SecurityGroupIds", out securityGroups)
                        ) {
                            AddError("Lambda function contains a VPC definition that does not include 'SubnetIds' or 'SecurityGroupIds' attributes");
                        }
                    });
                }

                // create function entry
                var sources = AtLocation(
                    "Sources",
                    () => function.Sources
                        ?.Select((source, eventIndex) => ConvertFunctionSource(function, eventIndex, source))
                        .Where(evt => evt != null)
                        .ToList()
                );
                var result = _builder.AddFunction(
                    parent: null,
                    name: function.Function,
                    description: function.Description,
                    project: function.Project,
                    language: function.Language,
                    environment: function.Environment,
                    sources: sources,
                    pragmas: function.Pragmas,
                    timeout: function.Timeout,
                    runtime: function.Runtime,
                    reservedConcurrency: function.ReservedConcurrency,
                    memory: function.Memory,
                    handler: function.Handler,
                    subnets: subnets,
                    securityGroups: securityGroups
                );
            });
        }

        private AFunctionSource ConvertFunctionSource(FunctionNode function, int index, FunctionSourceNode source) {
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

        private void ConvertOutput(int index, OutputNode output) {
            var type = DeterminNodeType("output", index, output, OutputNode.FieldCheckers, OutputNode.FieldCombinations, new[] { "Export", "CustomResource", "Macro" });
            switch(type) {
            case "Export":

                // TODO (2018-09-20, bjorg): add name validation
                AtLocation(output.Export, () => _builder.AddExport(output.Export, output.Description, output.Value));
                break;
            case "CustomResource":

                // TODO (2018-09-20, bjorg): add custom resource name validation
                Validate(output.Handler != null, "missing Handler attribute");

                // TODO (2018-09-20, bjorg): confirm that `Handler` is set to an SNS topic or lambda function
                AtLocation(output.CustomResource, () => _builder.AddCustomResource(output.CustomResource, output.Description, output.Handler));
                break;
            case "Macro":

                // TODO (2018-11-29, bjorg): add macro name validation
                Validate(output.Handler != null, "missing Handler attribute");

                // TODO (2018-10-30, bjorg): confirm that `Handler` is set to a lambda function
                AtLocation(output.Macro, () => _builder.AddMacro(output.Macro, output.Description, output.Handler));
                break;
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

                // find all declaration field with a non-null value; use alphabetical order for consistency
                var matches = fieldCombinations
                    .OrderBy(kv => kv.Key)
                    .Where(kv => {
                        if(!fieldChecker.TryGetValue(kv.Key, out Func<T, bool> checker)) {
                            throw new InvalidOperationException($"missing field checker for '{kv.Key}'");
                        }
                        return checker(instance);
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
                    AddError($"ambiguous {label} type: {string.Join(", ", matches.Select(kv => kv.Key))}");
                    return null;
                }

                // validate match
                var match = matches.First();
                foreach(var checker in fieldChecker.Where(kv =>
                    (kv.Key != match.Key
                    && !match.Value.Contains(kv.Key))
                    && kv.Value(instance)
                )) {
                    AddError($"'{checker.Key}' cannot be used with '{match.Key}'");
                }
                if(!expectedTypes.Contains(match.Key)) {
                    AddError($"unexpected node type: {match.Key}");
                    return null;

                }
                return match.Key;
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