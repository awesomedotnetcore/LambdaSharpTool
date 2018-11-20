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
            ForEach("Secrets", module.Secrets, ConvertSecret);
            ForEach("Inputs", module.Inputs, ConvertInput);
            ForEach("Outputs", module.Outputs, ConvertOutput);
            ForEach("Variables", module.Variables, ConvertParameter);
            ForEach("Functions",  module.Functions, ConvertFunction);
            return _builder.ToModule();
        }

        private void ConvertSecret(int index, object secret) {
            AtLocation($"[{index}]", () => _builder.AddSecret(secret));
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
                        dependsOn: ConvertToStringList(parameter.Resource.DependsOn),
                        condition: null
                    );

                    // register managed resource reference
                    result.Reference = (parameter.Resource.ArnAttribute != null)
                        ? FnGetAtt(result.ResourceName, parameter.Resource.ArnAttribute)
                        : ResourceMapping.GetArnReference(parameter.Resource.Type, result.ResourceName);

                    // request managed resource grants
                    _builder.AddGrant(result.LogicalId, parameter.Resource.Type, result.Reference, parameter.Resource.Allow);

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

                    // request existing resource grants
                    _builder.AddGrant(result.LogicalId, parameter.Resource.Type, parameter.Value, parameter.Resource.Allow);

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
                AtLocation(parameter.Var, () => {

                    // create package resource entry
                    var result = _builder.AddPackage(
                        parent: parent,
                        name: parameter.Package,
                        description: parameter.Description,
                        scope: parameter.Scope,
                        destinationBucket: (parameter.Bucket is string bucketParameter)
                            ? FnRef(bucketParameter)
                            : parameter.Bucket,
                        destinationKeyPrefix: parameter.Prefix ?? "",
                        sourceFilepath: parameter.Files
                    );

                    // register package resource reference
                    result.Reference = FnGetAtt(result.ResourceName, "Url");

                    // recurse
                    ConvertParameters(result);
                });
                break;
            }

            // local functions
            void ConvertParameters(AModuleEntry result) {
                ForEach("Variables", parameter.Variables, (i, p) => ConvertParameter(result, i, p));
            }
        }

        private void ConvertFunction(int index, FunctionNode function) {
            AtLocation(function.Function, () => {

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
            });
        }
    }
}