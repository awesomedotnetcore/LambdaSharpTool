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
        public string Generate(Module module, string gitSha) {
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
                AddOutput(output);
            }

            // add interface for presenting inputs
            var inputParameters = _module.Entries.OfType<InputEntry>();
            _stack.AddTemplateMetadata("AWS::CloudFormation::Interface", new Dictionary<string, object> {
                ["ParameterLabels"] = inputParameters.ToDictionary(input => input.LogicalId, input => new Dictionary<string, object> {
                    ["default"] = input.Label
                }),
                ["ParameterGroups"] = inputParameters
                    .GroupBy(input => input.Section)
                    .Select(section => new Dictionary<string, object> {
                        ["Label"] = new Dictionary<string, string> {
                            ["default"] = section.Key
                        },
                        ["Parameters"] = section.Select(input => input.LogicalId).ToList()
                    }
                )
            });

            // add module manifest
            _stack.AddTemplateMetadata("LambdaSharp::Manifest", new ModuleManifest {
                ModuleName = module.Name,
                ModuleVersion = module.Version.ToString(),
                Hash = new JsonStackSerializer().Serialize(_stack).ToMD5Hash(),
                GitSha = gitSha,
                Pragmas = module.Pragmas,
                Assets = module.Assets.ToList()
            });

            // generate JSON template
            var template = new JsonStackSerializer().Serialize(_stack);
            return template;
        }

        private void AddOutput(AOutput output) {
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
                _stack.Add($"{customResourceHandlerOutput.CustomResourceName.ToIdentifier()}Handler", new Humidifier.Output {
                    Description = customResourceHandlerOutput.Description,
                    Value = customResourceHandlerOutput.Handler,
                    Export = new Dictionary<string, dynamic> {
                        ["Name"] = Fn.Sub($"${{DeploymentPrefix}}CustomResource-{customResourceHandlerOutput.CustomResourceName}")
                    }
                });
                break;
            default:
                throw new InvalidOperationException($"cannot generate output for this type: {output?.GetType()}");
            }
        }

        private void AddResource(AModuleEntry entry) {
            var logicalId = entry.LogicalId;
            switch(entry) {
            case ValueEntry value:

                // nothing to do
                break;
            case PackageEntry package:
                _stack.Add(logicalId, package.Package);
                break;
            case HumidifierEntry humidifier:
                _stack.Add(
                    logicalId,
                    humidifier.Resource,
                    humidifier.Condition,
                    dependsOn: humidifier.DependsOn.ToArray()
                );
                break;
            case InputEntry input:
                _stack.Add(logicalId, input.Parameter);
                break;
            case FunctionEntry function:

                // TODO: make sure all these fields get set
                // _stack.Add(function.Name, new Lambda.Function {
                //     Code = new Lambda.FunctionTypes.Code {
                //         S3Bucket = FnRef("DeploymentBucketName"),
                //         S3Key = FnSub($"Modules/{module.Name}/Assets/{Path.GetFileName(function.PackagePath)}")
                //     },
                // });

                _stack.Add(function.LogicalId, function.Function);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entry), entry, "unknown parameter type");
            }
        }
    }
}