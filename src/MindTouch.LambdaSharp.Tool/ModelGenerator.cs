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
using Humidifier;
using Humidifier.Json;
using Humidifier.Logs;
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;
using Newtonsoft.Json;

namespace MindTouch.LambdaSharp.Tool {
    using ApiGateway = Humidifier.ApiGateway;
    using Events = Humidifier.Events;
    using IAM = Humidifier.IAM;
    using Lambda = Humidifier.Lambda;
    using SNS = Humidifier.SNS;

    public class ModelGenerator : AModelProcessor {

        //--- Fields ---
        private Module _module;
        private Stack _stack;

        //--- Constructors ---
        public ModelGenerator(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public string Generate(Module module) {
            _module = module;

            // stack header
            _stack = new Stack {
                AWSTemplateFormatVersion = "2010-09-09",
                Description = (_module.Description != null)
                    ? _module.Description.TrimEnd() + $" (v{_module.Version})"
                    : null
            };

            // add conditions
            foreach(var condition in _module.Conditions) {
                _stack.Add(condition.Key, new Condition(condition.Value));
            }
            _stack.Add($"ModuleIsNotNested", new Condition(Fn.Equals(Fn.Ref("DeploymentParent"), "")));

            // add parameters
            foreach(var entry in _module.Entries) {
                AddResource(entry);
            }

            // add outputs
            _stack.Add("ModuleName", new Humidifier.Output {
                Value = _module.Name
            });
            _stack.Add("ModuleVersion", new Humidifier.Output {
                Value = _module.Version.ToString()
            });
            foreach(var output in module.Outputs) {
                switch(output) {
                case ExportOutput exportOutput:
                    _stack.Add(exportOutput.Name, new Humidifier.Output {
                        Description = exportOutput.Description,
                        Value = exportOutput.Value
                    });
                    _stack.Add($"{exportOutput.Name}Export", new Humidifier.Output {
                        Description = exportOutput.Description,
                        Value = exportOutput.Value,
                        Export = new Dictionary<string, dynamic> {
                            ["Name"] = Fn.Sub($"${{AWS::StackName}}::{exportOutput.Name}")
                        },
                        Condition = "ModuleIsNotNested"
                    });
                    break;
                case CustomResourceHandlerOutput customResourceHandlerOutput:
                    _stack.Add($"{new string(customResourceHandlerOutput.CustomResourceName.Where(char.IsLetterOrDigit).ToArray())}Handler", new Humidifier.Output {
                        Description = customResourceHandlerOutput.Description,
                        Value = Fn.Ref(customResourceHandlerOutput.Handler),
                        Export = new Dictionary<string, dynamic> {
                            ["Name"] = Fn.Sub($"${{DeploymentPrefix}}CustomResource-{customResourceHandlerOutput.CustomResourceName}")
                        }
                    });
                    break;
                case MacroOutput macroOutput:
                    _stack.Add($"{macroOutput.Macro}Macro", new CustomResource("AWS::CloudFormation::Macro") {

                        // TODO (2018-10-30, bjorg): we may want to set 'LogGroupName' and 'LogRoleARN' as well
                        ["Name"] = Fn.Sub("${DeploymentPrefix}" + macroOutput.Macro),
                        ["Description"] = macroOutput.Description ?? "",
                        ["FunctionName"] = Fn.Ref(macroOutput.Handler)
                    });
                    break;
                default:
                    throw new InvalidOperationException($"cannot generate output for this type: {output?.GetType()}");
                }
            }

            // add interface for presenting inputs
            var inputParameters = _module.GetAllEntriesOfType<InputParameter>();
            _stack.AddTemplateMetadata("AWS::CloudFormation::Interface", new Dictionary<string, object> {
                ["ParameterLabels"] = inputParameters.ToDictionary(input => input.LogicalId, input => new Dictionary<string, object> {
                    ["default"] = ((InputParameter)input.Resource).Label
                }),
                ["ParameterGroups"] = inputParameters
                    .GroupBy(input => ((InputParameter)input.Resource).Section)
                    .Select(section => new Dictionary<string, object> {
                        ["Label"] = new Dictionary<string, string> {
                            ["default"] = section.Key
                        },
                        ["Parameters"] = section.Select(input => input.LogicalId).ToList()
                    }
                )
            });

            // add module manifest
            _stack.AddTemplateMetadata("LambdaSharp::Manifest", new Dictionary<string, object> {
                ["Version"] = "2018-10-22",
                ["ModuleName"] = _module.Name,
                ["ModuleVersion"] = _module.Version.ToString(),
                ["Pragmas"] = _module.Pragmas
            });

            // generate JSON template
            var template = new JsonStackSerializer().Serialize(_stack);
            return template;
        }

        private void AddResource(ModuleEntry entry) {
            var fullEnvName = entry.FullName.Replace("::", "_").ToUpperInvariant();
            var logicalId = entry.LogicalId;
            switch(entry.Resource) {
            case SecretParameter secretParameter:
                AddEnvironmentParameter(isSecret: true, value: GetReference());
                break;
            case ValueParameter _:
                AddEnvironmentParameter(isSecret: false, value: GetReference());
                break;
            case PackageParameter packageParameter:
                AddEnvironmentParameter(isSecret: false, value: GetReference());

                // NOTE: this CloudFormation resource can only be created once the file packager has run
                _stack.Add(logicalId, new Humidifier.CustomResource("LambdaSharp::S3::Package") {
                    ["DestinationBucketName"] = Fn.Ref(packageParameter.DestinationBucketParameterName),
                    ["DestinationKeyPrefix"] = packageParameter.DestinationKeyPrefix,
                    ["SourceBucketName"] = Fn.Ref("DeploymentBucketName"),
                    ["SourcePackageKey"] = Fn.Sub($"Modules/{_module.Name}/Assets/{Path.GetFileName(packageParameter.PackagePath)}")
                });
                break;
            case HumidifierParameter humidifierParameter: {
                    var resourceTemplate = humidifierParameter.Resource;
                    _stack.Add(logicalId, resourceTemplate, condition: humidifierParameter.Condition, dependsOn: humidifierParameter.DependsOn.ToArray());
                    AddEnvironmentParameter(isSecret: false, value: GetReference());
                }
                break;
            case InputParameter valueInputParameter: {
                    _stack.Add(logicalId, valueInputParameter.Parameter);
                    AddEnvironmentParameter(valueInputParameter.IsSecret, GetReference());
                }
                break;
            case FunctionParameter function:
                AddFunction(_module, function);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entry), entry, "unknown parameter type");
            }

            // local function
            void AddEnvironmentParameter(bool isSecret, object value) {

                // TODO: let's make this a tad more efficient!
                foreach(var function in entry.Scope.Select(name => _module.GetAllEntriesOfType<FunctionParameter>().First(f => f.FullName == name))) {
                    var resource = (FunctionParameter)function.Resource;
                    var environment = resource.Function.Environment.Variables;
                    if(isSecret) {
                        environment["SEC_" + fullEnvName] = value;
                    } else {
                        environment["STR_" + fullEnvName] = value;
                    }
                }
            }

            object GetReference() => _module.GetReference(entry.FullName);
        }

        private void AddFunction(Module module, FunctionParameter function) {

            // initialize function environment variables
            var environment = function.Function.Environment.Variables;
            foreach(var kv in function.Environment) {

                // add explicit environment variable as string value
                var key = "STR_" + kv.Key.Replace("::", "_").ToUpperInvariant();
                environment[key] = (dynamic)kv.Value;
            }
            environment["MODULE_NAME"] = module.Name;
            environment["MODULE_ID"] = FnRef("AWS::StackName");
            environment["MODULE_VERSION"] = module.Version.ToString();
            environment["LAMBDA_NAME"] = function.Name;
            environment["LAMBDA_RUNTIME"] = function.Function.Runtime;
            environment["DEADLETTERQUEUE"] = module.GetReference("LambdaSharp::DeadLetterQueueArn");
            environment["DEFAULTSECRETKEY"] = module.GetReference("LambdaSharp::DefaultSecretKeyArn");

            // TODO: make sure all these fields get set
            // _stack.Add(function.Name, new Lambda.Function {
            //     Code = new Lambda.FunctionTypes.Code {
            //         S3Bucket = FnRef("DeploymentBucketName"),
            //         S3Key = FnSub($"Modules/{module.Name}/Assets/{Path.GetFileName(function.PackagePath)}")
            //     },
            // });

            // create function definition
            _stack.Add(function.Name, function.Function);
        }
    }
}