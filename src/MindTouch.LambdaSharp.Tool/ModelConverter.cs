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

namespace MindTouch.LambdaSharp.Tool {
    using Fn = Humidifier.Fn;
    using Condition = Humidifier.Condition;

    public class ModelConverter : AModelProcessor {

        //--- Constants ---
        private const string CUSTOM_RESOURCE_PREFIX = "Custom::";

        //--- Fields ---
        private ModuleBuilder _builder;

        //--- Constructors ---
        public ModelConverter(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public Module Process(ModuleNode module) {

            // convert module definition
            try {
                return Convert(module);
            } catch(Exception e) {
                AddError(e);
                return null;
            }
        }

        private Module Convert(ModuleNode module) {

            // initialize module
            _builder = new ModuleBuilder(Settings, SourceFilename, new Module {
                Name = module.Module,
                Version = VersionInfo.Parse(module.Version),
                Description = module.Description
            });

            // convert collections
            ForEach("Secrets", module.Secrets, (index, secret) => {
                AtLocation($"[{index}]", () => _builder.AddSecret(secret));
            });
            ForEach("Inputs", module.Inputs, ConvertInput);
            ForEach("Outputs", module.Outputs, ConvertOutput);
            ForEach("Variables", module.Variables, (index, parameter) => ConvertParameter(parent: null, index: index, parameter: parameter));
            ForEach("Functions",  module.Functions, ConvertFunction);
            return _builder.ToModule();
        }

        private void ConvertInput(int index, InputNode input) {
            var type = DeterminNodeType("input", index, input, InputNode.FieldCheckers, InputNode.FieldCombinations, new[] { "Parameter", "Import" });
            switch(type) {
            case "Parameter":
                AtLocation(input.Parameter, () => _builder.AddInput(
                    input.Parameter,
                    input.Description,
                    input.Type,
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
                    input.Resource?.Properties

                ));
                break;
            case "Import":
                AtLocation(input.Import, () => _builder.AddImport(
                    input.Import,
                    input.Description,
                    input.Type,
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

        private void ConvertParameter(
            ModuleBuilder.Entry<AResource> parent,
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
                    var result = parent.AddEntry(new CloudFormationResourceParameter {
                        Name = parameter.Var,
                        Description = parameter.Description,
                        Scope = _builder.ConvertScope(parameter.Scope),
                        Resource = CreateResource(parameter.Resource)
                    });

                    // register managed resource reference
                    result.Reference = (parameter.Resource.ArnAttribute != null)
                        ? FnGetAtt(result.ResourceName, parameter.Resource.ArnAttribute)
                        : ResourceMapping.GetArnReference(parameter.Resource.Type, result.ResourceName);

                    // request managed resource grants
                    _builder.AddGrant(result.LogicalId, parameter.Resource.Type, result.Reference, parameter.Resource.Allow);

                    // recurse
                    ConvertParameters(result.Cast<AResource>());
                });
                break;
            case "Var.Reference":

                // existing resource
                AtLocation(parameter.Var, () => {

                    // create exiting resource entry
                    var result = parent.AddEntry(new ReferencedResourceParameter {
                        Name = parameter.Var,
                        Description = parameter.Description,
                        Scope = _builder.ConvertScope(parameter.Scope),
                        Reference = parameter.Value
                    });

                    // request existing resource grants
                    _builder.AddGrant(result.LogicalId, parameter.Resource.Type, parameter.Value, parameter.Resource.Allow);

                    // recurse
                    ConvertParameters(result.Cast<AResource>());
                });
                break;
            case "Var.Value":

                // literal value
                AtLocation(parameter.Var, () => {

                    // create literal value entry
                    var result = parent.AddEntry(new ValueParameter {
                        Name = parameter.Var,
                        Description = parameter.Description,
                        Scope = _builder.ConvertScope(parameter.Scope),
                        Reference = (parameter.Value is IList<object> values)
                            ? FnJoin(",", values)
                            : parameter.Value
                    });

                    // recurse
                    ConvertParameters(result.Cast<AResource>());
                });
                break;
            case "Var.Secret":

                // encrypted value
                AtLocation(parameter.Var, () => {

                    // create encrypted value entry
                    var result = parent.AddEntry(new SecretParameter {
                        Name = parameter.Var,
                        Description = parameter.Description,
                        Scope = _builder.ConvertScope(parameter.Scope),
                        Reference = FnJoin(
                            "|",
                            new object[] {
                                parameter.Value
                            }.Union(parameter.EncryptionContext
                                ?.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")
                                ?? new string[0]
                            ).ToArray()
                        )
                    });

                    // recurse
                    ConvertParameters(result.Cast<AResource>());
                });
                break;
            case "Var.Empty":

                // empty entry
                AtLocation(parameter.Var, () => {

                    // create empty entry
                    var result = parent.AddEntry(new ValueParameter {
                        Name = parameter.Var,
                        Description = parameter.Description,
                        Scope = _builder.ConvertScope(parameter.Scope),
                        Reference = ""
                    });

                    // recurse
                    ConvertParameters(result.Cast<AResource>());
                });
                break;
            case "Package":

                // package resource
                AtLocation(parameter.Var, () => {

                    // create package resource entry
                    var result = parent.AddEntry(new PackageParameter {
                        Name = parameter.Package,
                        Description = parameter.Description,
                        Scope = _builder.ConvertScope(parameter.Scope),
                        DestinationBucketParameterName = parameter.Bucket,
                        DestinationKeyPrefix = parameter.Prefix ?? "",
                        SourceFilepath = parameter.Files
                    });

                    // register package resource reference
                    result.Reference = FnGetAtt(result.ResourceName, "Url");

                    // recurse
                    ConvertParameters(result.Cast<AResource>());
                });
                break;
            }

            // local functions
            void ConvertParameters(ModuleBuilder.Entry<AResource> result) {
                ForEach("Variables", parameter.Variables, (i, p) => ConvertParameter(result, i, p));
            }
        }

        private void ConvertFunction(int index, FunctionNode function) {
            AtLocation(function.Function ?? $"[{index}]", () => {

                // append the version to the function description
                if(function.Description != null) {
                    function.Description = function.Description.TrimEnd() + $" (v{_builder.Version})";
                }

                // initialize VPC configuration if provided
                Humidifier.Lambda.FunctionTypes.VpcConfig vpc = null;
                if(function.VPC?.Any() == true) {
                    if(
                        function.VPC.TryGetValue("SubnetIds", out var subnets)
                        && function.VPC.TryGetValue("SecurityGroupIds", out var securityGroups)
                    ) {
                        AtLocation("VPC", () => {
                            vpc = new Humidifier.Lambda.FunctionTypes.VpcConfig {
                                SubnetIds = subnets,
                                SecurityGroupIds = securityGroups
                            };
                        });
                    } else {
                        AddError("Lambda function contains a VPC definition that does not include 'SubnetIds' or 'SecurityGroupIds' attributes");
                    }
                }

                // create function entry
                var eventIndex = 0;
                var result = _builder.AddEntry(new FunctionParameter {
                    Name = function.Function,
                    Description = function.Description,
                    Project = function.Project,
                    Language = function.Language,

                    // TODO (2018-11-10, bjorg): don't put generator logic into the converter
                    Environment = function.Environment.ToDictionary(
                        kv => "STR_" + kv.Key.Replace("::", "_").ToUpperInvariant(),
                        kv => kv.Value
                    ) ?? new Dictionary<string, object>(),
                    Sources = AtLocation("Sources", () => function.Sources?.Select(source => ConvertFunctionSource(function, ++eventIndex, source)).Where(evt => evt != null).ToList(), null) ?? new List<AFunctionSource>(),
                    Pragmas = function.Pragmas,
                    Function = new Humidifier.Lambda.Function {
                        Description = function.Description,
                        Timeout = function.Timeout,
                        Runtime = function.Runtime,
                        ReservedConcurrentExecutions = function.ReservedConcurrency,
                        MemorySize = function.Memory,
                        Handler = function.Handler,
                        VpcConfig = vpc,
                        Role = FnGetAtt("Module::Role", "Arn"),
                        DeadLetterConfig = new Humidifier.Lambda.FunctionTypes.DeadLetterConfig {
                            TargetArn = FnRef("Module::DeadLetterQueueArn")
                        },
                        Environment = new Humidifier.Lambda.FunctionTypes.Environment {
                            Variables = new Dictionary<string, dynamic>()
                        }
                    }
                });
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
                    var integration = AtLocation("Integration", () => Enum.Parse<ApiGatewaySourceIntegration>(source.Integration ?? "RequestResponse", ignoreCase: true), ApiGatewaySourceIntegration.Unsupported);
                    return new ApiGatewaySource {
                        Method = method,
                        Path = path,
                        Integration = integration,
                        OperationName = source.OperationName,
                        ApiKeyRequired = source.ApiKeyRequired
                    };
                }, null);
            case "Schedule":
                return AtLocation("Schedule", () => new ScheduleSource {
                    Expression = source.Schedule,
                    Name = source.Name
                }, null);
            case "S3":
                return AtLocation("S3", () => new S3Source {
                    Bucket = source.S3,
                    Events = source.Events ?? new List<string> {

                        // default S3 events to listen to
                        "s3:ObjectCreated:*"
                    },
                    Prefix = source.Prefix,
                    Suffix = source.Suffix
                }, null);
            case "SlackCommand":
                return AtLocation("SlackCommand", () => new ApiGatewaySource {
                    Method = "POST",
                    Path = source.SlackCommand.Split('/', StringSplitOptions.RemoveEmptyEntries),
                    Integration = ApiGatewaySourceIntegration.SlackCommand,
                    OperationName = source.OperationName
                }, null);
            case "Topic":
                return AtLocation("Topic", () => new TopicSource {
                    TopicName = source.Topic
                }, null);
            case "Sqs":
                return AtLocation("Sqs", () => new SqsSource {
                    Queue = source.Sqs,
                    BatchSize = source.BatchSize ?? 10
                }, null);
            case "Alexa":
                return AtLocation("Alexa", () => new AlexaSource {
                    EventSourceToken = source.Alexa
                }, null);
            case "DynamoDB":
                return AtLocation("DynamoDB", () => new DynamoDBSource {
                    DynamoDB = source.DynamoDB,
                    BatchSize = source.BatchSize ?? 100,
                    StartingPosition = source.StartingPosition ?? "LATEST"
                }, null);
            case "Kinesis":
                return AtLocation("Kinesis", () => new KinesisSource {
                    Kinesis = source.Kinesis,
                    BatchSize = source.BatchSize ?? 100,
                    StartingPosition = source.StartingPosition ?? "LATEST"
                }, null);
            }
            return null;
        }

        private void ConvertOutput(int index, OutputNode output) {
            var type = DeterminNodeType("output", index, output, OutputNode.FieldCheckers, OutputNode.FieldCombinations, new[] { "Export", "CustomResource", "Macro" });
            switch(type) {
            case "Export":
                AtLocation(output.Export, () => _builder.AddExport(output.Export, output.Description, output.Value));
                break;
            case "CustomResource":
                AtLocation(output.CustomResource, () => _builder.AddCustomResource(output.CustomResource, output.Description, output.Handler));
                break;
            case "Macro":
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
            }, null);
        }
    }
}